using System.Data;
using System.Text.RegularExpressions;
using EfCore.ConsumerTrace;
using Microsoft.Data.SqlClient;

namespace AdventureWorks.Web.Services;

/// <param name="SqlText">Batch text, with any ConsumerTrace permalink removed (it's surfaced as <paramref name="SourceUrl"/>).</param>
/// <param name="SourceUrl">GitHub permalink to the calling code, if the ConsumerTrace comment included one.</param>
public sealed record RecentQuery(
    DateTime LastExecutionTime,
    long SecondsAgoMs,
    long ExecutionCount,
    double AvgElapsedMs,
    string SqlText,
    string? SourceUrl);

/// <summary>
/// Reads the most recently executed batches against the app's database from SQL Server's plan cache.
/// Uses a plain SqlConnection rather than the DbContext so polling isn't intercepted or logged by EF.
/// </summary>
public sealed partial class RecentQueryFeed(IConfiguration configuration, ConsumerTraceInterceptor consumerTraceInterceptor)
{
    // Matches ", link: https://github.com/..." in the ConsumerTraceInterceptor comment. The interceptor escapes
    // path segments, so the URL itself contains no whitespace, commas or asterisks.
    [GeneratedRegex(@",\s*link:\s*(?<url>https://github\.com/[^\s,*]+)")]
    private static partial Regex PermalinkPattern();

    // The marker comment lets the query exclude itself from its own results.
    // dm_exec_query_stats has a row per statement, so rows are grouped per batch (sql_handle)
    // to show the full batch text, including any leading comment added by an interceptor.
    private const string Sql = """
        /* RecentQueryFeed */
        WITH batches AS (
            SELECT s.sql_handle,
                   MAX(s.last_execution_time) AS last_execution_time,
                   SUM(s.execution_count) AS execution_count,
                   SUM(s.total_elapsed_time) AS total_elapsed_time
            FROM sys.dm_exec_query_stats AS s
            CROSS APPLY (SELECT CONVERT(int, a.value) AS dbid
                         FROM sys.dm_exec_plan_attributes(s.plan_handle) AS a
                         WHERE a.attribute = N'dbid') AS pa
            WHERE pa.dbid = DB_ID()
            GROUP BY s.sql_handle
        )
        SELECT TOP (@take)
               b.last_execution_time,
               DATEDIFF_BIG(millisecond, b.last_execution_time, SYSDATETIME()),
               b.execution_count,
               b.total_elapsed_time / 1000.0 / b.execution_count,
               t.text
        FROM batches AS b
        CROSS APPLY sys.dm_exec_sql_text(b.sql_handle) AS t
        WHERE t.text NOT LIKE N'%/* RecentQueryFeed */%'
          AND (@appCommentPattern IS NULL OR t.text LIKE @appCommentPattern)
        ORDER BY b.last_execution_time DESC;
        """;

    // Matches the start of the ConsumerTraceInterceptor comment for this app: "/* app: {name}, ..."
    // LIKE wildcards in the name are bracket-escaped so they match literally.
    private readonly string appCommentPattern =
        $"%/* app: {EscapeLike(consumerTraceInterceptor.AppName)},%";

    private readonly string connectionString = configuration.GetConnectionString("AdventureWorks")
        ?? throw new InvalidOperationException("Connection string 'AdventureWorks' is not configured.");

    /// <param name="thisAppOnly">Only return batches tagged by this app's ConsumerTraceInterceptor.</param>
    public async Task<IReadOnlyList<RecentQuery>> GetRecentAsync(int take, bool thisAppOnly, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@take", take);
        command.Parameters.Add("@appCommentPattern", SqlDbType.NVarChar, 4000).Value =
            thisAppOnly ? appCommentPattern : DBNull.Value;

        List<RecentQuery> results = new List<RecentQuery>(take);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string sqlText = reader.GetString(4);
            Match permalink = PermalinkPattern().Match(sqlText);

            results.Add(new RecentQuery(
                reader.GetDateTime(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (double)reader.GetDecimal(3),
                permalink.Success ? sqlText.Remove(permalink.Index, permalink.Length) : sqlText,
                permalink.Success ? permalink.Groups["url"].Value : null));
        }

        return results;
    }

    /// <summary>
    /// Clears the plan cache for the app's database only (not the whole server), which also removes its rows
    /// from sys.dm_exec_query_stats. Intended for demos; it forces every query to recompile.
    /// </summary>
    public async Task ClearPlanCacheAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = new SqlCommand(
            "/* RecentQueryFeed */ ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
