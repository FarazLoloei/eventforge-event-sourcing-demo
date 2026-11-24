using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>A loaded stream that is a valid schema-aware Task history: the mapped events and the folded state.</summary>
internal sealed record FoldedHistory(TaskState State, IReadOnlyList<TaskEvent> Events);

/// <summary>Why a loaded stream is not a valid Task history. <see cref="Position"/> is 1-based when known.</summary>
internal sealed record HistoryFault(InvalidHistoryReason Reason, int? Position);

/// <summary>
/// The one place that validates and folds a loaded Task stream, shared by the command and query handlers: event
/// count and positions, stream identity, strict, schema-aware mapping (CodeGenerated v1 is upcast, v2 read directly) and <see cref="TaskAggregate.Apply"/>. It performs
/// no I/O and stops at the first fault, so callers never see a partially validated history.
/// </summary>
internal static class TaskHistory
{
    /// <returns><c>null</c> on success (with <paramref name="folded"/> set), otherwise the first fault.</returns>
    public static HistoryFault? Fold(LoadedStream loaded, out FoldedHistory? folded)
    {
        folded = null;

        if (loaded.Events.Count != loaded.Version)
        {
            return new HistoryFault(InvalidHistoryReason.StreamCorrupted, null);
        }

        var state = TaskState.Initial;
        var events = new List<TaskEvent>(loaded.Events.Count);
        for (var i = 0; i < loaded.Events.Count; i++)
        {
            var position = i + 1;
            var stored = loaded.Events[i];
            if (stored.StreamVersion != position)
            {
                return new HistoryFault(InvalidHistoryReason.StreamCorrupted, position);
            }

            // Every event must belong to the requested stream. Together with the mapper's check that
            // TaskCreated.taskId equals the envelope's stream id, this ties taskId to the requested TaskId.
            if (stored.StreamType != loaded.Stream.StreamType || stored.StreamId != loaded.Stream.StreamId)
            {
                return new HistoryFault(InvalidHistoryReason.InvalidSequence, position);
            }

            try
            {
                var @event = TaskEventMapper.FromStored(stored);
                state = TaskAggregate.Apply(state, @event);
                events.Add(@event);
            }
            catch (TaskEventMappingException exception)
            {
                return new HistoryFault(exception.Reason, position);
            }
            catch (InvalidEventHistoryException)
            {
                return new HistoryFault(InvalidHistoryReason.InvalidSequence, position);
            }
        }

        folded = new FoldedHistory(state, events);
        return null;
    }
}
