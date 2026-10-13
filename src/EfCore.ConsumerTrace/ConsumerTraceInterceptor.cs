using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.ConsumerTrace;

/// <summary>
/// Prepends a SQL comment to every command EF Core sends to the database.
/// </summary>
public sealed class ConsumerTraceInterceptor : DbCommandInterceptor
{
    private const string Comment = "Hello, world!";

    // CommandInitialized runs once per command after EF has set its text and parameters,
    // and before execution, so it covers queries, scalars and non-queries, sync and async.
    // A block comment terminates on its own, so it can't swallow the SQL that follows it.
    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        result.CommandText = $"/* {Comment} */{Environment.NewLine}{result.CommandText}";
        return result;
    }
}
