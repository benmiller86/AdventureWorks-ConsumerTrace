using System.Data;
using System.Text.RegularExpressions;
using EfCore.ConsumerTrace;
using Microsoft.Data.SqlClient;

namespace AdventureWorks.Web.Services;

/// <summary>An aggregated entry from the plan cache: a batch the application sent, with its execution stats.</summary>
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
/// Reads aggregated execution stats for the app's database from SQL Server's plan cache, as a monitoring tool would:
/// batches from sys.dm_exec_query_stats and stored procedures from sys.dm_exec_procedure_stats.
/// Uses a plain SqlConnection rather than the DbContext so polling isn't intercepted or logged by EF.
/// </summary>
public sealed partial class RecentQueryFeed(IConfiguration configuration, ConsumerTraceInterceptor consumerTraceInterceptor)
{
    // Matches ", link: https://github.com/..." in the ConsumerTraceInterceptor comment. The interceptor escapes
    // path segments, so the URL itself contains no whitespace, commas or asterisks.
    [GeneratedRegex(@",\s*link:\s*(?<url>https://github\.com/[^\s,*]+)")]
    private static partial Regex PermalinkPattern();

    // Three result sets in one round trip. The marker comment lets the queries exclude themselves.
    //  1. Batches: dm_exec_query_stats has a row per statement, so rows are grouped per batch (sql_handle) to show the
    //     full batch text, including the leading trace comment. Statements inside stored procedures are left out;
    //     they're covered by (2), because their text is the procedure definition, which has no trace comment.
    //  2. Procedures: aggregated per procedure from dm_exec_procedure_stats.
    //  3. Procedure callers: an EXEC has no query plan of its own, so the calling batch (which carries the trace
    //     comment) never appears in dm_exec_query_stats. It is still in the plan cache, with a use count.
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
          AND (t.objectid IS NULL OR OBJECTPROPERTY(t.objectid, 'IsProcedure') = 0)
          AND (@appCommentPattern IS NULL OR t.text LIKE @appCommentPattern)
        ORDER BY b.last_execution_time DESC;

        SELECT OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id),
               OBJECT_NAME(ps.object_id, ps.database_id),
               MAX(ps.last_execution_time),
               DATEDIFF_BIG(millisecond, MAX(ps.last_execution_time), SYSDATETIME()),
               SUM(ps.execution_count),
               SUM(ps.total_elapsed_time) / 1000.0 / SUM(ps.execution_count)
        FROM sys.dm_exec_procedure_stats AS ps
        WHERE ps.database_id = DB_ID()
        GROUP BY ps.object_id, ps.database_id;

        SELECT CONVERT(bigint, cp.usecounts), t.text
        FROM sys.dm_exec_cached_plans AS cp
        CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) AS t
        CROSS APPLY (SELECT CONVERT(int, a.value) AS dbid
                     FROM sys.dm_exec_plan_attributes(cp.plan_handle) AS a
                     WHERE a.attribute = N'dbid') AS pa
        WHERE pa.dbid = DB_ID()
          AND cp.objtype IN (N'Adhoc', N'Prepared')
          AND t.text LIKE N'%/* app: %'
          AND t.text LIKE N'%EXEC%'
          AND t.text NOT LIKE N'%/* RecentQueryFeed */%';
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

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<RecentQuery> results = new List<RecentQuery>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(CreateEntry(
                reader.GetString(4),
                reader.GetDateTime(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (double)reader.GetDecimal(3)));
        }

        List<ProcedureStats> procedures = new List<ProcedureStats>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            procedures.Add(new ProcedureStats(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDateTime(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                (double)reader.GetDecimal(5)));
        }

        List<(long UseCount, string Text)> callerBatches = new List<(long UseCount, string Text)>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            callerBatches.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        // Each traced batch that EXECs a procedure becomes an entry of its own, shown like any other batch.
        // Its execution count is the batch's own use count; SQL Server only keeps the procedure's last execution
        // time and duration in aggregate, so those are shared by all of the procedure's callers.
        foreach (ProcedureStats procedure in procedures)
        {
            Regex callsProcedure = ExecPattern(procedure.Schema, procedure.Name);
            long tracedExecutions = 0;

            foreach ((long useCount, string text) in callerBatches.Where(batch => callsProcedure.IsMatch(batch.Text)))
            {
                tracedExecutions += useCount;
                if (thisAppOnly && !text.Contains($"/* app: {consumerTraceInterceptor.AppName},", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(CreateEntry(
                    text, procedure.LastExecutionTime, procedure.SecondsAgoMs, useCount, procedure.AvgElapsedMs));
            }

            // Executions from callers without a trace comment (e.g. SSMS), which have nothing to trace back to
            long untracedExecutions = procedure.ExecutionCount - tracedExecutions;
            if (!thisAppOnly && untracedExecutions > 0)
            {
                results.Add(CreateEntry(
                    $"EXEC {procedure.Schema}.{procedure.Name}",
                    procedure.LastExecutionTime, procedure.SecondsAgoMs, untracedExecutions, procedure.AvgElapsedMs));
            }
        }

        return results
            .OrderByDescending(q => q.LastExecutionTime)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Clears the plan cache for the app's database only (not the whole server), which also removes its rows
    /// from sys.dm_exec_query_stats and sys.dm_exec_procedure_stats. Intended for demos; it forces every query to recompile.
    /// </summary>
    public async Task ClearPlanCacheAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = new SqlCommand(
            "/* RecentQueryFeed */ ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ProcedureStats(
        string Schema, string Name, DateTime LastExecutionTime, long SecondsAgoMs, long ExecutionCount, double AvgElapsedMs);

    private static RecentQuery CreateEntry(
        string sqlText, DateTime lastExecutionTime, long secondsAgoMs, long executionCount, double avgElapsedMs)
    {
        Match permalink = PermalinkPattern().Match(sqlText);

        return new RecentQuery(
            lastExecutionTime,
            secondsAgoMs,
            executionCount,
            avgElapsedMs,
            permalink.Success ? sqlText.Remove(permalink.Index, permalink.Length) : sqlText,
            permalink.Success ? permalink.Groups["url"].Value : null);
    }

    // "EXEC proc", "EXECUTE schema.proc" or "EXEC [schema].[proc]", not followed by more of an identifier
    private static Regex ExecPattern(string schema, string name)
    {
        string schemaPart = $@"(?:\[{Regex.Escape(schema)}\]|{Regex.Escape(schema)})";
        string namePart = $@"(?:\[{Regex.Escape(name)}\]|{Regex.Escape(name)})";
        return new Regex($@"\bEXEC(?:UTE)?\s+(?:{schemaPart}\s*\.\s*)?{namePart}(?![\w\]])", RegexOptions.IgnoreCase);
    }

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
