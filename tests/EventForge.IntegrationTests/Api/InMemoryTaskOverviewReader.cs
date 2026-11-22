using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// An in-memory <see cref="ITaskOverviewReader"/> for the API tests that run without a database. By default it behaves
/// like a projection that follows the in-memory store: the row is the pure reducer's fold of the task's events. Tests can
/// override a row or hide it to model drift and legacy streams. It reads the store's events directly and never calls
/// <see cref="InMemoryEventStore.LoadStreamAsync"/>, so <see cref="InMemoryEventStore.LoadCalls"/> still shows whether
/// a request loaded the stream. Not evidence of PostgreSQL behaviour.
/// </summary>
public sealed class InMemoryTaskOverviewReader(InMemoryEventStore store) : ITaskOverviewReader
{
    private readonly Dictionary<Guid, TaskOverviewRow> _overrides = [];
    private readonly HashSet<Guid> _hidden = [];

    public int Calls { get; private set; }

    public Exception? FindException { get; set; }

    public void Override(TaskOverviewRow row) => _overrides[row.TaskId] = row;

    /// <summary>Models a stream whose overview row does not exist (for example a stream written before the projection).</summary>
    public void Hide(Guid taskId) => _hidden.Add(taskId);

    public Task<TaskOverviewLookup> FindAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        Calls++;
        if (FindException is not null)
        {
            throw FindException;
        }

        var events = store.EventsOf(taskId.Value);
        if (_overrides.TryGetValue(taskId.Value, out var row))
        {
            return Task.FromResult<TaskOverviewLookup>(new TaskOverviewFound(row));
        }

        if (_hidden.Contains(taskId.Value) || events.Count == 0)
        {
            return Task.FromResult<TaskOverviewLookup>(new TaskOverviewAbsent(events.Count > 0));
        }

        try
        {
            var mapped = events.Select(TaskEventMapper.FromStored).ToList();
            return Task.FromResult<TaskOverviewLookup>(new TaskOverviewFound(TaskOverviewProjector.ReduceBatch(null, mapped, 1)));
        }
        catch (Exception exception) when (exception is TaskEventMappingException or TaskOverviewProjectionException)
        {
            // The store holds events the projector could never have folded: no row exists for them.
            return Task.FromResult<TaskOverviewLookup>(new TaskOverviewAbsent(true));
        }
    }
}
