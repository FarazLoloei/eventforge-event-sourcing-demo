using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// Reads one <c>eventforge.task_overview</c> row (migration 002) for projection-backed Task state queries (EVF-38).
/// </summary>
/// <remarks>
/// One statement on a pooled connection under PostgreSQL's default READ COMMITTED isolation, so only committed state is
/// visible and the row and the existence check see the same snapshot (a concurrent append commits its events and its
/// row together). The event table is consulted only when no row was found, and only through <c>EXISTS</c>: a state read
/// never loads or folds the stream. Read-only; it never repairs or backfills a missing row. Unknown stored status text
/// (which the table's CHECK constraints prevent) throws <see cref="TaskOverviewProjectionException"/>, and database errors propagate unchanged.
/// </remarks>
public sealed class PostgreSqlTaskOverviewReader(PostgreSqlDatabase database) : ITaskOverviewReader
{
    private const string Overview = PostgreSqlDatabase.Schema + ".task_overview";
    private const string Events = PostgreSqlDatabase.Schema + ".events";

    // The left join always yields one row; o.* is NULL when there is no overview row, and only then is the stream probed.
    private const string FindSql = $"""
        SELECT o.status, o.assigned_agent_id, o.current_generation_id, o.latest_test_result, o.last_stream_version,
               o.task_id IS NOT NULL AS has_row,
               CASE WHEN o.task_id IS NULL
                    THEN EXISTS (SELECT 1 FROM {Events} WHERE stream_type = @stream_type AND stream_id = @task_id)
                    ELSE NULL END AS events_exist
        FROM (SELECT 1) AS probe
        LEFT JOIN {Overview} AS o ON o.task_id = @task_id
        """;

    public async Task<TaskOverviewLookup> FindAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FindSql, connection);
        command.Parameters.Add("task_id", NpgsqlDbType.Uuid).Value = taskId.Value;
        command.Parameters.Add("stream_type", NpgsqlDbType.Text).Value = TaskEventContract.StreamType;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        if (!reader.GetBoolean(5))
        {
            return new TaskOverviewAbsent(reader.GetBoolean(6));
        }

        // Unknown status text throws TaskOverviewProjectionException(InvalidRow); the handler turns it into a safe result.
        return new TaskOverviewFound(new TaskOverviewRow(
            taskId.Value,
            PostgreSqlTaskOverviewProjector.ParseStatus(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : PostgreSqlTaskOverviewProjector.ParseResult(reader.GetString(3)),
            reader.GetInt32(4)));
    }
}
