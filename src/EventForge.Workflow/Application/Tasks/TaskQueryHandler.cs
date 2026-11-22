using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// Answers Task queries. Read-only: it never appends events or decides commands.
/// <para>
/// <b>State</b> is read from the committed <c>task_overview</c> projection through <see cref="ITaskOverviewReader"/>;
/// the event stream is not loaded or folded, so a state read no longer validates historical event payloads. A missing
/// row is "not found" only when no Task event exists, and an impossible row is refused rather than returned.
/// </para>
/// <para>
/// <b>History</b> stays authoritative and strict: it reads the whole committed stream and folds it with the shared
/// <see cref="TaskHistory"/> logic, so a corrupt history returns an explicit result and never partial events.
/// </para>
/// </summary>
public sealed class TaskQueryHandler(IEventStore store, ITaskOverviewReader overview)
{
    public async Task<TaskQueryResult> GetStateAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        TaskOverviewLookup lookup;
        try
        {
            lookup = await overview.FindAsync(taskId, cancellationToken);
        }
        catch (TaskOverviewProjectionException exception) when (exception.Fault == TaskOverviewFault.InvalidRow)
        {
            return new QueryProjectionUnavailable(TaskOverviewFault.InvalidRow);
        }

        return lookup switch
        {
            TaskOverviewFound { Row: var row } when row.TaskId == taskId.Value && TaskOverviewProjector.IsPlausible(row) =>
                new TaskStateFound(row),
            TaskOverviewFound => new QueryProjectionUnavailable(TaskOverviewFault.InvalidRow),
            TaskOverviewAbsent { TaskEventsExist: false } => new QueryTaskNotFound(),
            TaskOverviewAbsent => new QueryProjectionUnavailable(TaskOverviewFault.MissingRow),
            _ => throw new InvalidOperationException("The overview reader returned an unknown result."),
        };
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
