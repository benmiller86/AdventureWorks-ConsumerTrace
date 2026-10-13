using Microsoft.Data.SqlClient;

namespace AdventureWorks.Web.Services;

public sealed record RecentQuery(
    DateTime LastExecutionTime,
    long SecondsAgoMs,
    long ExecutionCount,
    double AvgElapsedMs,
    string SqlText);

/// <summary>
/// Reads the most recently executed batches against the app's database from SQL Server's plan cache.
/// Uses a plain SqlConnection rather than the DbContext so polling isn't intercepted or logged by EF.
/// </summary>
public sealed class RecentQueryFeed(IConfiguration configuration)
{
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
        ORDER BY b.last_execution_time DESC;
        """;

    private readonly string connectionString = configuration.GetConnectionString("AdventureWorks")
        ?? throw new InvalidOperationException("Connection string 'AdventureWorks' is not configured.");

    public async Task<IReadOnlyList<RecentQuery>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@take", take);

        List<RecentQuery> results = new List<RecentQuery>(take);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RecentQuery(
                reader.GetDateTime(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                (double)reader.GetDecimal(3),
                reader.GetString(4)));
        }

        return results;
    }
}
