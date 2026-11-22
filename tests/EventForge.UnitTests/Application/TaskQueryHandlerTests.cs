using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Application.StoredEvents;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

public sealed class TaskQueryHandlerTests
{
    private static readonly StreamKey Stream = new("Task", T1.Value);

    /// <summary>A read-only scripted store. Any append is counted so the tests can prove queries never write.</summary>
    private sealed class FakeEventStore(IReadOnlyList<StoredEvent> events) : IEventStore
    {
        public int AppendCalls { get; private set; }

        public Exception? LoadException { get; init; }

        public CancellationToken LastLoadToken { get; private set; }

        public Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
        {
            LastLoadToken = cancellationToken;
            return LoadException is null
                ? Task.FromResult(new LoadedStream(stream, events.Count, events))
                : throw LoadException;
        }

        public Task<AppendResult> AppendAsync(
            StreamKey stream, int expectedVersion, IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
        {
            AppendCalls++;
            throw new InvalidOperationException("A query must not append.");
        }
    }

    private static (TaskQueryHandler Handler, FakeEventStore Store) Create(params StoredEvent[] seeded)
    {
        var store = new FakeEventStore(seeded);
        return (new TaskQueryHandler(store), store);
    }

    [Fact]
    public async Task A_missing_task_is_not_found_for_state_and_history()
    {
        var (handler, store) = Create();

        Assert.Equal(new QueryTaskNotFound(), await handler.GetStateAsync(T1, default));
        Assert.Equal(new QueryTaskNotFound(), await handler.GetHistoryAsync(T1, default));
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task State_is_folded_from_the_recorded_events()
    {
        var (handler, _) = Create(History(
            new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1), new TestsFailed(G1),
            new CodeGenerated(G2, Art2)));

        var found = Assert.IsType<TaskStateFound>(await handler.GetStateAsync(T1, default));

        Assert.Equal(WorkflowStatus.Assigned, found.State.Status);
        Assert.Equal(Agent, found.State.AssignedAgent);
        Assert.Equal(G2, found.State.CurrentGeneration);
        Assert.Null(found.State.CurrentTestOutcome); // the new generation cleared the failed outcome
        Assert.Equal(5, found.State.Version);
    }

    [Fact]
    public async Task A_completed_task_reports_completed_with_the_passed_outcome()
    {
        var (handler, _) = Create(History(
            new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1), new TestsPassed(G1),
            new TaskCompleted()));

        var found = Assert.IsType<TaskStateFound>(await handler.GetStateAsync(T1, default));

        Assert.Equal(WorkflowStatus.Completed, found.State.Status);
        Assert.Equal(TestOutcome.Passed, found.State.CurrentTestOutcome);
    }

    [Fact]
    public async Task History_keeps_the_stored_envelope_facts_in_stream_order_with_the_mapped_events()
    {
        var stored = History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1));
        var (handler, _) = Create(stored);

        var found = Assert.IsType<TaskHistoryFound>(await handler.GetHistoryAsync(T1, default));

        Assert.Equal(T1, found.TaskId);
        Assert.Equal(3, found.Version);
        Assert.Equal(stored.Select(e => e.EventId), found.Events.Select(e => e.EventId));
        Assert.Equal([1, 2, 3], found.Events.Select(e => e.StreamVersion));
        Assert.Equal(["TaskCreated", "TaskAssigned", "CodeGenerated"], found.Events.Select(e => e.EventType));
        Assert.All(found.Events, e => Assert.Equal(1, e.SchemaVersion));
        Assert.All(found.Events, e => Assert.Equal(DateTimeOffset.UnixEpoch, e.RecordedAt));
        Assert.Equal(
            new TaskEvent[] { new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1) },
            found.Events.Select(e => e.Event));
    }

    [Theory]
    [MemberData(nameof(TaskCommandHandlerTests.BadHistories), MemberType = typeof(TaskCommandHandlerTests))]
    public async Task Unreadable_history_is_reported_at_its_position_with_no_partial_result(
        StoredEvent[] history, InvalidHistoryReason reason, int position)
    {
        var (handler, store) = Create(history);

        var expected = new QueryInvalidHistory(reason, position);
        Assert.Equal(expected, await handler.GetStateAsync(T1, default));
        Assert.Equal(expected, await handler.GetHistoryAsync(T1, default));
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task A_corrupted_stream_from_the_store_is_invalid_history()
    {
        var store = new FakeEventStore([]) { LoadException = new EventStreamCorruptedException(Stream, "gap") };
        var handler = new TaskQueryHandler(store);

        var expected = new QueryInvalidHistory(InvalidHistoryReason.StreamCorrupted, null);
        Assert.Equal(expected, await handler.GetStateAsync(T1, default));
        Assert.Equal(expected, await handler.GetHistoryAsync(T1, default));
    }

    [Fact]
    public async Task Other_store_failures_propagate_and_the_token_reaches_the_store()
    {
        var store = new FakeEventStore([]) { LoadException = new InvalidOperationException("boom") };
        var handler = new TaskQueryHandler(store);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.GetStateAsync(T1, cts.Token));

        Assert.Equal(cts.Token, store.LastLoadToken);
    }
}
