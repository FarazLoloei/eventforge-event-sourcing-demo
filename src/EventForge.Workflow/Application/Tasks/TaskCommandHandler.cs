using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// Executes one Task command: load the stream, check the caller's expected version, rebuild the aggregate from the
/// stored events, decide, and append the resulting events in a single <see cref="IEventStore.AppendAsync"/> call
/// (one PostgreSQL transaction in the Infrastructure implementation).
/// </summary>
/// <remarks>
/// Version conflicts, duplicate event ids and unknown commit outcomes are returned, never retried. Optimistic
/// concurrency is not request idempotency: a retry after a lost response may meet a conflict or a domain rejection,
/// which does not establish the original outcome. Event ids come from <c>newEventId</c>, outside the pure aggregate.
/// </remarks>
public sealed class TaskCommandHandler(IEventStore store, Func<Guid> newEventId)
{
    private const int MaxCorrelationIdLength = 100;

    public async Task<TaskCommandResult> HandleAsync(
        TaskCommand command,
        int expectedVersion,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        // Fail on a malformed command before any I/O.
        TaskAggregate.ValidateCommand(command);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        var metadata = BuildMetadata(correlationId);
        var stream = new StreamKey(TaskEventContract.StreamType, command.TaskId.Value);

        LoadedStream loaded;
        try
        {
            loaded = await store.LoadStreamAsync(stream, cancellationToken);
        }
        catch (EventStreamCorruptedException)
        {
            return new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null);
        }

        if (loaded.Version != expectedVersion)
        {
            return new VersionConflict(loaded.Version);
        }

        if (loaded.Events.Count != loaded.Version)
        {
            return new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null);
        }

        var state = TaskState.Initial;
        for (var i = 0; i < loaded.Events.Count; i++)
        {
            var position = i + 1;
            var stored = loaded.Events[i];
            if (stored.StreamVersion != position)
            {
                return new InvalidHistory(InvalidHistoryReason.StreamCorrupted, position);
            }

            // Every event must belong to the requested stream. Together with the mapper's check that
            // TaskCreated.taskId equals the envelope's stream id, this ties taskId to the requested TaskId.
            if (stored.StreamType != stream.StreamType || stored.StreamId != stream.StreamId)
            {
                return new InvalidHistory(InvalidHistoryReason.InvalidSequence, position);
            }

            try
            {
                state = TaskAggregate.Apply(state, TaskEventMapper.FromStored(stored));
            }
            catch (TaskEventMappingException exception)
            {
                return new InvalidHistory(exception.Reason, position);
            }
            catch (InvalidEventHistoryException)
            {
                return new InvalidHistory(InvalidHistoryReason.InvalidSequence, position);
            }
        }

        var decision = TaskAggregate.Decide(state, command);
        if (decision is Rejected rejected)
        {
            return new CommandRejected(rejected.Code);
        }

        var accepted = (Accepted)decision;

        var newEvents = accepted.Events
            .Select(e => TaskEventMapper.ToNewEvent(e, newEventId(), metadata))
            .ToList();

        AppendResult result;
        try
        {
            result = await store.AppendAsync(stream, loaded.Version, newEvents, cancellationToken);
        }
        catch (EventStoreCommitUnknownException)
        {
            return new CommitUnknown();
        }
        catch (EventStreamCorruptedException)
        {
            return new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null);
        }

        return result.Outcome switch
        {
            AppendOutcome.Appended => new CommandSucceeded(result.CurrentVersion!.Value, accepted.Events),
            AppendOutcome.VersionConflict => new VersionConflict(result.CurrentVersion),
            AppendOutcome.DuplicateEventId => new DuplicateEventId(),
            _ => throw new InvalidOperationException("The event store returned an unsupported outcome."),
        };
    }

    private static JsonElement BuildMetadata(string? correlationId)
    {
        if (correlationId is null)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        }

        if (string.IsNullOrWhiteSpace(correlationId)
            || correlationId.Length > MaxCorrelationIdLength
            || correlationId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The correlation id must be non-blank, at most 100 characters and free of control characters.",
                nameof(correlationId));
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["correlationId"] = correlationId });
    }
}
