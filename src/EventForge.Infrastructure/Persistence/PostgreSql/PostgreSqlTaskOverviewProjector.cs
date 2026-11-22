using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// Keeps <c>eventforge.task_overview</c> (migration 002) in step with the Task event stream, on the transaction of the
/// append that produced the events. It maps the stored schema v1 events, reads the task's current row, folds the batch
/// with the pure <see cref="TaskOverviewProjector"/> and writes the result: an <c>INSERT</c> for a new task, otherwise an
/// <c>UPDATE</c> guarded by the version that was read. It never replays or backfills: a continuing stream without a row
/// is refused, which rolls back the append. Internal to Infrastructure: only <see cref="PostgreSqlEventStore"/> calls it,
/// and it does so for every Task append.
/// </summary>
/// <remarks>
/// Failures are <see cref="TaskOverviewProjectionException"/> (invalid, stale, gapped or missing state) or the
/// <see cref="PostgresException"/> of a violated table constraint; both propagate, so the caller's transaction is never
/// committed. This type opens no connection, never commits and never retries.
/// </remarks>
internal static class PostgreSqlTaskOverviewProjector
{
    private const string UniqueViolation = "23505";
    private const string Table = PostgreSqlDatabase.Schema + ".task_overview";

    private const string SelectSql = $"""
        SELECT status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version
        FROM {Table}
        WHERE task_id = @task_id
        """;

    private const string InsertSql = $"""
        INSERT INTO {Table}
            (task_id, status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version)
        VALUES
            (@task_id, @status, @assigned_agent_id, @current_generation_id, @latest_test_result, @last_stream_version)
        """;

    // The version guard: the row must still be at the version the reducer started from.
    private const string UpdateSql = $"""
        UPDATE {Table}
        SET status = @status,
            assigned_agent_id = @assigned_agent_id,
            current_generation_id = @current_generation_id,
            latest_test_result = @latest_test_result,
            last_stream_version = @last_stream_version
        WHERE task_id = @task_id AND last_stream_version = @previous_version
        """;

    public static async Task ProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StreamKey stream,
        int expectedVersion,
        IReadOnlyList<StoredEvent> appended,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(appended);

        if (stream.StreamType != TaskEventContract.StreamType || appended.Count == 0)
        {
            throw new ArgumentException("The projector received an unsupported append.", nameof(appended));
        }

        var events = new List<TaskEvent>(appended.Count);
        for (var i = 0; i < appended.Count; i++)
        {
            // The store assigned these versions; a mismatch would mean the projection no longer follows the stream.
            if (appended[i].StreamVersion != expectedVersion + 1 + i)
            {
                throw new TaskOverviewProjectionException(TaskOverviewFault.VersionNotContiguous);
            }

            try
            {
                events.Add(TaskEventMapper.FromStored(appended[i]));
            }
            catch (TaskEventMappingException)
            {
                throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidEvent);
            }
        }

        var previous = await ReadAsync(connection, transaction, stream.StreamId, cancellationToken);
        var next = TaskOverviewProjector.ReduceBatch(previous, events, expectedVersion + 1);
        if (next.TaskId != stream.StreamId)
        {
            throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidEvent);
        }

        await WriteAsync(connection, transaction, previous, next, cancellationToken);
    }

    private static async Task<TaskOverviewRow?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectSql, connection, transaction);
        command.Parameters.Add("task_id", NpgsqlDbType.Uuid).Value = taskId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TaskOverviewRow(
            taskId,
            ParseStatus(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : ParseResult(reader.GetString(3)),
            reader.GetInt32(4));
    }

    private static async Task WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TaskOverviewRow? previous,
        TaskOverviewRow next,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(previous is null ? InsertSql : UpdateSql, connection, transaction);
        command.Parameters.Add("task_id", NpgsqlDbType.Uuid).Value = next.TaskId;
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = FormatStatus(next.Status);
        command.Parameters.Add("assigned_agent_id", NpgsqlDbType.Text).Value = (object?)next.AssignedAgentId ?? DBNull.Value;
        command.Parameters.Add("current_generation_id", NpgsqlDbType.Text).Value = (object?)next.CurrentGenerationId ?? DBNull.Value;
        command.Parameters.Add("latest_test_result", NpgsqlDbType.Text).Value =
            next.LatestTestResult is { } result ? (object)FormatResult(result) : DBNull.Value;
        command.Parameters.Add("last_stream_version", NpgsqlDbType.Integer).Value = next.LastStreamVersion;
        if (previous is not null)
        {
            command.Parameters.Add("previous_version", NpgsqlDbType.Integer).Value = previous.LastStreamVersion;
        }

        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // An insert for a task whose row appeared after it was read: the projection is not following the stream.
            throw new TaskOverviewProjectionException(TaskOverviewFault.RowAlreadyExists);
        }

        // Exactly one row must change. Zero means the guard failed (the row moved or vanished): never retried.
        if (affected != 1)
        {
            throw new TaskOverviewProjectionException(TaskOverviewFault.VersionNotContiguous);
        }
    }

    private static WorkflowStatus ParseStatus(string value) => value switch
    {
        "created" => WorkflowStatus.Created,
        "assigned" => WorkflowStatus.Assigned,
        "completed" => WorkflowStatus.Completed,
        _ => throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidRow),
    };

    private static TestOutcome ParseResult(string value) => value switch
    {
        "passed" => TestOutcome.Passed,
        "failed" => TestOutcome.Failed,
        _ => throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidRow),
    };

    private static string FormatStatus(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Created => "created",
        WorkflowStatus.Assigned => "assigned",
        WorkflowStatus.Completed => "completed",
        _ => throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidRow),
    };

    private static string FormatResult(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "passed",
        TestOutcome.Failed => "failed",
        _ => throw new TaskOverviewProjectionException(TaskOverviewFault.InvalidRow),
    };
}
