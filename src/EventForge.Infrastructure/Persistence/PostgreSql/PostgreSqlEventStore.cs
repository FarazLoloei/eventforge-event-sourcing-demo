using System.Data;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL implementation of <see cref="IEventStore"/> over <c>eventforge.events</c>
/// (created by <c>db/migrations/001-create-event-store.sql</c>).
/// </summary>
/// <remarks>
/// Append runs in one READ COMMITTED transaction: it reads the stream head, compares it with the expected version,
/// inserts every event and commits. Writers that both pass the check are separated by the unique
/// (stream_type, stream_id, stream_version) constraint; the loser is rolled back and reported as a conflict.
/// There are no advisory locks and no retries. The transaction logic lives in <see cref="AppendCoreAsync"/>.
/// <para>
/// Task streams are fail-closed: every append to a <c>Task</c> stream also updates <c>task_overview</c> through
/// <see cref="PostgreSqlTaskOverviewProjector"/>, on the same connection and transaction, after the insert and before the
/// single COMMIT. A refused or failed projection rolls back the events too, and success is reported only after the
/// commit. The projector is part of this type, not a configuration option, so no construction path can append Task
/// events without it. Other stream types are generic and never projected.
/// </para>
/// </remarks>
public sealed class PostgreSqlEventStore(PostgreSqlDatabase database) : IEventStore
{
    // Constraint names defined by the migration.
    internal const string StreamVersionConstraint = "events_stream_version_uq";
    internal const string EventIdConstraint = "events_pkey";

    private const string UniqueViolation = "23505";
    private const string Table = PostgreSqlDatabase.Schema + ".events";

    private const string LoadSql = $"""
        SELECT event_id, stream_version, event_type, schema_version, payload::text, metadata::text, recorded_at
        FROM {Table}
        WHERE stream_type = @stream_type AND stream_id = @stream_id
        ORDER BY stream_version
        """;

    private const string HeadSql = $"""
        SELECT count(*), COALESCE(max(stream_version), 0)
        FROM {Table}
        WHERE stream_type = @stream_type AND stream_id = @stream_id
        """;

    private const string InsertSql = $"""
        INSERT INTO {Table}
            (event_id, stream_type, stream_id, stream_version, event_type, schema_version, payload, metadata, recorded_at)
        VALUES
            (@event_id, @stream_type, @stream_id, @stream_version, @event_type, @schema_version, @payload, @metadata,
             transaction_timestamp())
        RETURNING recorded_at
        """;

    public async Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LoadSql, connection);
        AddStreamParameters(command, stream);

        var events = new List<StoredEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var version = reader.GetInt32(1);
                if (version != events.Count + 1)
                {
                    throw new EventStreamCorruptedException(stream, $"expected version {events.Count + 1} but found {version}");
                }

                events.Add(new StoredEvent(
                    reader.GetGuid(0),
                    stream.StreamType,
                    stream.StreamId,
                    version,
                    reader.GetString(2),
                    reader.GetInt32(3),
                    ParseJson(reader.GetString(4)),
                    ParseJson(reader.GetString(5)),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        return new LoadedStream(stream, events.Count, events);
    }

    public async Task<AppendResult> AppendAsync(
        StreamKey stream,
        int expectedVersion,
        IReadOnlyList<NewEvent> events,
        CancellationToken cancellationToken)
    {
        ValidateAppend(stream, expectedVersion, events);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        AppendResult result;
        try
        {
            result = await AppendCoreAsync(connection, transaction, stream, expectedVersion, events, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation &&
                                                  exception.ConstraintName == StreamVersionConstraint)
        {
            // Another writer committed the same version first. Its version is not read back, so it is unknown here.
            return AppendResult.VersionConflict(null);
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation &&
                                                  exception.ConstraintName == EventIdConstraint)
        {
            return AppendResult.DuplicateEventId();
        }

        if (!result.IsAppended)
        {
            return result; // disposing the uncommitted transaction rolls it back
        }

        // Inline Task projection on the same connection and transaction. Any exception (a refused projection, a violated
        // table constraint, cancellation) leaves the scope before COMMIT, so the events are rolled back with it. These
        // failures are not mapped to a conflict or to an unknown commit: they are definite rollbacks.
        if (stream.StreamType == TaskEventContract.StreamType)
        {
            await PostgreSqlTaskOverviewProjector.ProjectAsync(
                connection, transaction, stream, expectedVersion, result.Events, cancellationToken);
        }

        // Up to here any failure is a definite rollback. Once COMMIT is sent, a lost connection or cancellation
        // leaves the outcome unknown; a PostgreSQL error answer to COMMIT means the transaction was not committed.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not PostgresException)
        {
            throw new EventStoreCommitUnknownException(stream, exception);
        }

        return result;
    }

    /// <summary>Checks the stream head and inserts the events on the caller's transaction. Does not commit.</summary>
    internal static async Task<AppendResult> AppendCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StreamKey stream,
        int expectedVersion,
        IReadOnlyList<NewEvent> events,
        CancellationToken cancellationToken)
    {
        long count;
        int head;
        await using (var headCommand = new NpgsqlCommand(HeadSql, connection, transaction))
        {
            AddStreamParameters(headCommand, stream);
            await using var reader = await headCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            count = reader.GetInt64(0);
            head = reader.GetInt32(1);
        }

        // Versions are unique and positive, so exactly 1..head exist if and only if count == head.
        if (count != head)
        {
            throw new EventStreamCorruptedException(stream, "stored versions are not contiguous from 1; refusing to append");
        }

        if (head != expectedVersion)
        {
            return AppendResult.VersionConflict(head);
        }

        var stored = new List<StoredEvent>(events.Count);
        foreach (var (newEvent, index) in events.Select((e, i) => (e, i)))
        {
            var version = expectedVersion + 1 + index;
            await using var insert = new NpgsqlCommand(InsertSql, connection, transaction);
            AddStreamParameters(insert, stream);
            insert.Parameters.Add("event_id", NpgsqlDbType.Uuid).Value = newEvent.EventId;
            insert.Parameters.Add("stream_version", NpgsqlDbType.Integer).Value = version;
            insert.Parameters.Add("event_type", NpgsqlDbType.Text).Value = newEvent.EventType;
            insert.Parameters.Add("schema_version", NpgsqlDbType.Integer).Value = newEvent.SchemaVersion;
            insert.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = newEvent.Payload.GetRawText();
            insert.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = newEvent.Metadata.GetRawText();

            await using var inserted = await insert.ExecuteReaderAsync(cancellationToken);
            if (!await inserted.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The insert did not return recorded_at.");
            }

            // recorded_at is the PostgreSQL-assigned value. Payload and metadata are returned as submitted:
            // JSONB stores them semantically equal but normalised (key order, whitespace).
            stored.Add(new StoredEvent(
                newEvent.EventId, stream.StreamType, stream.StreamId, version, newEvent.EventType,
                newEvent.SchemaVersion, newEvent.Payload, newEvent.Metadata,
                inserted.GetFieldValue<DateTimeOffset>(0)));
        }

        return AppendResult.Appended(expectedVersion + events.Count, stored);
    }

    private static void ValidateAppend(StreamKey stream, int expectedVersion, IReadOnlyList<NewEvent> events)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);

        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "The expected version must not be negative.");
        }

        if (events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(events));
        }

        if (events.Any(e => e is null))
        {
            throw new ArgumentException("Events must not contain null.", nameof(events));
        }

        if (expectedVersion > int.MaxValue - events.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), "The stream version would overflow.");
        }

        if (events.Select(e => e.EventId).Distinct().Count() != events.Count)
        {
            throw new ArgumentException("Event ids must be unique within a batch.", nameof(events));
        }
    }

    private static void AddStreamParameters(NpgsqlCommand command, StreamKey stream)
    {
        command.Parameters.Add("stream_type", NpgsqlDbType.Text).Value = stream.StreamType;
        command.Parameters.Add("stream_id", NpgsqlDbType.Uuid).Value = stream.StreamId;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
