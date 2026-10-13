using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.ConsumerTrace;

/// <summary>
/// <see cref="ConsumerTraceInterceptor"/> whose application details (name, git SHA, repository URL) are read from
/// the assembly containing <typeparamref name="TApp"/>, e.g. <c>new ConsumerTraceInterceptor&lt;Program&gt;()</c>.
/// Prefer this over the non-generic type when the entry assembly isn't the application (test hosts, tools).
/// </summary>
/// <typeparam name="TApp">Any type in the consuming application's assembly, typically its startup type.</typeparam>
public sealed class ConsumerTraceInterceptor<TApp> : ConsumerTraceInterceptor
{
    public ConsumerTraceInterceptor(string? appName = null, string? sourceRoot = null, string? gitSha = null, string? repositoryUrl = null)
        : base(typeof(TApp).Assembly, appName, sourceRoot, gitSha, repositoryUrl)
    {
    }
}

/// <summary>
/// Prepends a SQL comment to every command EF Core sends to the database, identifying the application and the
/// code that issued it (file:line and Class.Method), the git commit the application was built from, and a
/// GitHub permalink to the calling line at that commit.
/// </summary>
public class ConsumerTraceInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
{
    // Frames from these assemblies are EF/framework plumbing between the application code and this interceptor
    private static readonly string[] FrameworkAssemblyPrefixes = ["System", "Microsoft."];

    private static readonly Assembly ThisAssembly = typeof(ConsumerTraceInterceptor).Assembly;

    private readonly string appName;
    private readonly string? sourceRoot;
    private readonly string? gitSha;
    private readonly string? gitHubRepositoryUrl;

    /// <param name="appAssembly">
    /// The consuming application's assembly, which the defaults for the other values are read from.
    /// Defaults to the entry assembly; use <see cref="ConsumerTraceInterceptor{TApp}"/> to pick it by type instead.
    /// </param>
    /// <param name="appName">Application name to report. Defaults to the name of <paramref name="appAssembly"/>.</param>
    /// <param name="sourceRoot">
    /// Directory that caller file paths are made relative to. Defaults to the enclosing git repository root, if any.
    /// Permalinks are only generated for files under this directory, so it should be the repository root.
    /// </param>
    /// <param name="gitSha">
    /// Commit to report. Defaults to the SHA the .NET SDK stamps into the app assembly's informational version.
    /// </param>
    /// <param name="repositoryUrl">
    /// Repository to link to. Defaults to the app assembly's RepositoryUrl metadata
    /// (emitted by the SDK when the project sets PublishRepositoryUrl). Links are only generated for GitHub.
    /// </param>
    public ConsumerTraceInterceptor(
        Assembly? appAssembly = null,
        string? appName = null,
        string? sourceRoot = null,
        string? gitSha = null,
        string? repositoryUrl = null)
    {
        appAssembly ??= Assembly.GetEntryAssembly();

        this.appName = NullIfEmpty(appName) ?? appAssembly?.GetName().Name ?? "unknown";
        this.sourceRoot = sourceRoot ?? FindGitRoot(AppContext.BaseDirectory);
        this.gitSha = NullIfEmpty(gitSha) ?? GetBuildGitSha(appAssembly);
        gitHubRepositoryUrl = ToGitHubWebUrl(NullIfEmpty(repositoryUrl) ?? GetBuildRepositoryUrl(appAssembly));
    }

    /// <summary>The application name written to each command's comment as <c>app: {AppName},</c>.</summary>
    public string AppName => appName;

    // EF opens the connection before it creates the command. When the open has to wait (e.g. the pool needs a new
    // physical connection), CommandInitialized runs on a continuation with the application code no longer on the stack.
    // So the caller is also captured here, while it still is, and kept per context until the command uses it.
    // Contexts aren't used concurrently, and the weak table lets disposed contexts be collected.
    private readonly ConditionalWeakTable<DbContext, StackFrame> callersAtConnectionOpen = new();

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        RememberCaller(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        RememberCaller(eventData.Context);
        return ValueTask.FromResult(result);
    }

    // CommandInitialized runs once per command after EF has set its text and parameters,
    // and before execution, so it covers queries, scalars and non-queries, sync and async.
    // A block comment terminates on its own, so it can't swallow the SQL that follows it.
    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        StackFrame? frame = FindCallerFrame();

        if (eventData.Context is DbContext context && callersAtConnectionOpen.TryGetValue(context, out StackFrame? openedBy))
        {
            // Consume it either way so a later command can't pick up a stale caller
            callersAtConnectionOpen.Remove(context);
            frame ??= openedBy;
        }

        string comment = BuildComment(frame);
        result.CommandText = $"/* {comment} */{Environment.NewLine}{result.CommandText}";
        return result;
    }

    private void RememberCaller(DbContext? context)
    {
        if (context is not null && FindCallerFrame() is StackFrame frame)
        {
            callersAtConnectionOpen.AddOrUpdate(context, frame);
        }
    }

    private string BuildComment(StackFrame? frame)
    {
        MethodBase? method = frame?.GetMethod();
        string? relativePath = GetRepositoryRelativePath(frame?.GetFileName());
        int line = frame?.GetFileLineNumber() ?? 0;

        string caller = DescribeLocation(frame?.GetFileName(), relativePath, line);
        string member = method is null ? "unknown" : DescribeMethod(method);
        string comment = $"app: {appName}, caller: {caller}, method: {member}, sha: {gitSha ?? "unknown"}";

        if (BuildPermalink(relativePath, line) is string permalink)
        {
            comment += $", link: {permalink}";
        }

        return Sanitize(comment);
    }

    // https://github.com/{owner}/{repo}/blob/{sha}/{path}#L{line}
    private string? BuildPermalink(string? relativePath, int line)
    {
        if (gitHubRepositoryUrl is null || gitSha is null || relativePath is null)
        {
            return null;
        }

        string escapedPath = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        string anchor = line > 0 ? $"#L{line}" : "";
        return $"{gitHubRepositoryUrl}/blob/{gitSha}/{escapedPath}{anchor}";
    }

    private static StackFrame? FindCallerFrame()
    {
        StackTrace trace = new StackTrace(skipFrames: 1, fNeedFileInfo: true);

        foreach (StackFrame frame in trace.GetFrames())
        {
            Assembly? assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is null || assembly == ThisAssembly)
            {
                continue;
            }

            string assemblyName = assembly.GetName().Name ?? "";
            if (!FrameworkAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return frame;
            }
        }

        return null;
    }

    private static string DescribeLocation(string? file, string? relativePath, int line)
    {
        if (string.IsNullOrEmpty(file))
        {
            // No PDB available for the calling assembly
            return "unknown";
        }

        return $"{relativePath ?? file.Replace('\\', '/')}:{line}";
    }

    // Path relative to sourceRoot with forward slashes, or null if the file isn't under it
    private string? GetRepositoryRelativePath(string? file)
    {
        if (string.IsNullOrEmpty(file) || sourceRoot is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(sourceRoot, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    // Maps compiler-generated members back to the method the developer wrote, e.g.
    // Products+<LoadAsync>d__42.MoveNext (async state machine) -> Products.LoadAsync
    // Products+<>c__DisplayClass5_0.<OnInitialized>b__0 (lambda) -> Products.OnInitialized
    private static string DescribeMethod(MethodBase method)
    {
        string name = GetSourceName(method.Name) ?? method.Name;
        Type? owner = method.DeclaringType;

        while (owner is { DeclaringType: not null } && owner.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            if (GetSourceName(owner.Name) is string ownerName)
            {
                name = ownerName;
            }

            owner = owner.DeclaringType;
        }

        return owner is null ? name : $"{owner.Name}.{name}";
    }

    // "<LoadAsync>d__42" -> "LoadAsync", "<<OnInit>b__1_0>d" -> "OnInit", "<>c" -> null, "Load" -> null
    private static string? GetSourceName(string generatedName)
    {
        if (!generatedName.StartsWith('<'))
        {
            return null;
        }

        int depth = 0;
        for (int i = 0; i < generatedName.Length; i++)
        {
            depth += generatedName[i] switch { '<' => 1, '>' => -1, _ => 0 };
            if (depth == 0)
            {
                string inner = generatedName[1..i];
                return inner.Length == 0 ? null : GetSourceName(inner) ?? inner;
            }
        }

        return null;
    }

    // The .NET SDK appends "+<commit sha>" to the informational version when building inside a git repository
    private static string? GetBuildGitSha(Assembly? appAssembly)
    {
        string? version = appAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        int plus = version?.IndexOf('+') ?? -1;
        return plus >= 0 ? NullIfEmpty(version![(plus + 1)..]) : null;
    }

    private static string? GetBuildRepositoryUrl(Assembly? appAssembly) =>
        appAssembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?
            .Value;

    // Normalizes https://github.com/owner/repo(.git) and git@github.com:owner/repo(.git) to https://github.com/owner/repo;
    // returns null for anything that isn't GitHub, since the permalink format is GitHub-specific
    private static string? ToGitHubWebUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }

        string url = repositoryUrl.Trim();
        const string sshPrefix = "git@github.com:";
        if (url.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            url = "https://github.com/" + url[sshPrefix.Length..];
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return $"https://github.com{path}";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? FindGitRoot(string startDirectory)
    {
        for (DirectoryInfo? dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    // A "*/" inside a value would end the comment early and expose the rest as SQL
    private static string Sanitize(string value) => value.Replace("*/", "* /", StringComparison.Ordinal);
}
