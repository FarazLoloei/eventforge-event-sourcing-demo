using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// Answers Task queries by reading the committed event stream and folding it with the shared
/// <see cref="TaskHistory"/> logic. Read-only: it never appends events or decides commands. The whole stream is read
/// and folded on every call (an intentional small-demo trade-off, not optimized for long histories). A fault returns
/// an explicit result and never a partial state or history.
/// </summary>
public sealed class TaskQueryHandler(IEventStore store)
{
    public async Task<TaskQueryResult> GetStateAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var (failure, _, history) = await LoadAndFoldAsync(taskId, cancellationToken);
        return failure ?? new TaskStateFound(history!.State);
    }

    public async Task<TaskQueryResult> GetHistoryAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        var (failure, loaded, history) = await LoadAndFoldAsync(taskId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var entries = loaded!.Events
            .Select((stored, i) => new TaskHistoryEntry(
                stored.EventId, stored.StreamVersion, stored.EventType, stored.SchemaVersion, stored.RecordedAt,
                history!.Events[i]))
            .ToList();
        return new TaskHistoryFound(taskId, history!.State.Version, entries);
    }

    private async Task<(TaskQueryResult? Failure, LoadedStream? Loaded, FoldedHistory? History)> LoadAndFoldAsync(
        TaskId taskId, CancellationToken cancellationToken)
    {
        var stream = new StreamKey(TaskEventContract.StreamType, taskId.Value);

        LoadedStream loaded;
        try
        {
            loaded = await store.LoadStreamAsync(stream, cancellationToken);
        }
        catch (EventStreamCorruptedException)
        {
            return (new QueryInvalidHistory(InvalidHistoryReason.StreamCorrupted, null), null, null);
        }

        if (loaded.Version == 0 && loaded.Events.Count == 0)
        {
            return (new QueryTaskNotFound(), null, null);
        }

        var fault = TaskHistory.Fold(loaded, out var folded);
        return fault is null
            ? (null, loaded, folded)
            : (new QueryInvalidHistory(fault.Reason, fault.Position), null, null);
    }
}
