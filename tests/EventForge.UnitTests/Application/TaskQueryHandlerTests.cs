using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Application.StoredEvents;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

public sealed class TaskQueryHandlerTests
{
    private static readonly StreamKey Stream = new("Task", T1.Value);

    /// <summary>A read-only scripted store. Calls are counted so the tests can prove what a query did and did not touch.</summary>
    private sealed class FakeEventStore(IReadOnlyList<StoredEvent> events) : IEventStore
    {
        public int AppendCalls { get; private set; }

        public int LoadCalls { get; private set; }

        public Exception? LoadException { get; init; }

        public CancellationToken LastLoadToken { get; private set; }

        public Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
        {
            LoadCalls++;
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

    /// <summary>A scripted projection reader. It records calls and the token so tests can prove how it was used.</summary>
    private sealed class FakeOverviewReader(TaskOverviewLookup lookup) : ITaskOverviewReader
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; init; }

        public CancellationToken LastToken { get; private set; }

        public Task<TaskOverviewLookup> FindAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            return Failure is null ? Task.FromResult(lookup) : throw Failure;
        }
    }

    private static readonly TaskOverviewLookup NoRowNoEvents = new TaskOverviewAbsent(false);

    private static TaskOverviewFound Found(
        WorkflowStatus status, string? agent, string? generation, TestOutcome? result, int version) =>
        new(new TaskOverviewRow(T1.Value, status, agent, generation, result, version));

    private static (TaskQueryHandler Handler, FakeEventStore Store, FakeOverviewReader Reader) Create(
        TaskOverviewLookup? lookup = null, params StoredEvent[] seeded)
    {
        var store = new FakeEventStore(seeded);
        var reader = new FakeOverviewReader(lookup ?? NoRowNoEvents);
        return (new TaskQueryHandler(store, reader), store, reader);
    }

    [Fact]
    public async Task A_task_without_row_or_events_is_not_found_for_state_and_history()
    {
        var (handler, store, _) = Create();

        Assert.Equal(new QueryTaskNotFound(), await handler.GetStateAsync(T1, default));
        Assert.Equal(new QueryTaskNotFound(), await handler.GetHistoryAsync(T1, default));
        Assert.Equal(0, store.AppendCalls);
    }

    [Theory]
    [InlineData(WorkflowStatus.Created, null, null, null, 1)]
    [InlineData(WorkflowStatus.Assigned, "agent-1", null, null, 2)]
    [InlineData(WorkflowStatus.Assigned, "agent-1", "g1", null, 3)]
    [InlineData(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Failed, 4)]
    [InlineData(WorkflowStatus.Assigned, "agent-1", "g2", null, 5)]
    [InlineData(WorkflowStatus.Assigned, "agent-1", "g2", TestOutcome.Passed, 6)]
    [InlineData(WorkflowStatus.Completed, "agent-1", "g2", TestOutcome.Passed, 7)]
    public async Task State_is_the_projection_row_unchanged_and_the_event_stream_is_never_read(
        WorkflowStatus status, string? agent, string? generation, TestOutcome? result, int version)
    {
        var row = Found(status, agent, generation, result, version);
        var (handler, store, reader) = Create(row);

        var found = Assert.IsType<TaskStateFound>(await handler.GetStateAsync(T1, default));

        Assert.Equal(row.Row, found.State);
        Assert.Equal(version, found.State.LastStreamVersion);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(0, store.LoadCalls); // no stream load, so no fold of the history
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task A_row_missing_while_task_events_exist_is_a_projection_fault_never_not_found()
    {
        var (handler, store, _) = Create(new TaskOverviewAbsent(true));

        var result = await handler.GetStateAsync(T1, default);

        Assert.Equal(new QueryProjectionUnavailable(TaskOverviewFault.MissingRow), result);
        Assert.Equal(0, store.LoadCalls);
    }

    public static TheoryData<int> ImpossibleRows => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(ImpossibleRows))]
    public async Task An_implausible_or_foreign_row_is_a_projection_fault_and_never_returned(int index)
    {
        TaskOverviewFound[] rows =
        [
            Found(WorkflowStatus.Created, null, null, null, 5), // a version no created task can have
            Found(WorkflowStatus.Assigned, "agent-1", null, null, 1),
            Found(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Passed, 4),
            Found(WorkflowStatus.Assigned, null, "g1", TestOutcome.Failed, 4), // nullability contradiction
            new(new TaskOverviewRow(Guid.NewGuid(), WorkflowStatus.Created, null, null, null, 1)), // another task's row
        ];
        var (handler, _, _) = Create(rows[index]);

        Assert.Equal(new QueryProjectionUnavailable(TaskOverviewFault.InvalidRow), await handler.GetStateAsync(T1, default));
    }

    [Fact]
    public async Task A_reader_that_reports_an_invalid_row_is_a_projection_fault()
    {
        var reader = new FakeOverviewReader(NoRowNoEvents)
        {
            Failure = new TaskOverviewProjectionException(TaskOverviewFault.InvalidRow),
        };
        var handler = new TaskQueryHandler(new FakeEventStore([]), reader);

        Assert.Equal(new QueryProjectionUnavailable(TaskOverviewFault.InvalidRow), await handler.GetStateAsync(T1, default));
    }

    [Fact]
    public async Task Other_reader_failures_propagate_and_the_token_reaches_the_reader()
    {
        var reader = new FakeOverviewReader(NoRowNoEvents) { Failure = new InvalidOperationException("boom") };
        var handler = new TaskQueryHandler(new FakeEventStore([]), reader);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.GetStateAsync(T1, cts.Token));

        Assert.Equal(cts.Token, reader.LastToken);
    }

    [Fact]
    public async Task A_cancelled_reader_propagates_cancellation_unchanged()
    {
        var reader = new FakeOverviewReader(NoRowNoEvents) { Failure = new OperationCanceledException() };
        var handler = new TaskQueryHandler(new FakeEventStore([]), reader);

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.GetStateAsync(T1, default));
    }

    [Fact]
    public async Task History_ignores_the_projection_and_reads_only_the_event_store()
    {
        var stored = History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1));
        // The projection is deliberately wrong: history must not consult it.
        var (handler, store, reader) = Create(Found(WorkflowStatus.Completed, "x", "y", TestOutcome.Passed, 99), stored);

        var found = Assert.IsType<TaskHistoryFound>(await handler.GetHistoryAsync(T1, default));

        Assert.Equal(0, reader.Calls);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(T1, found.TaskId);
        Assert.Equal(3, found.Version);
        Assert.Equal(stored.Select(e => e.EventId), found.Events.Select(e => e.EventId));
        Assert.Equal([1, 2, 3], found.Events.Select(e => e.StreamVersion));
        Assert.Equal(["TaskCreated", "TaskAssigned", "CodeGenerated"], found.Events.Select(e => e.EventType));
        // The stored envelope's schema version is reported: CodeGenerated is now written as v2, the rest as v1.
        Assert.Equal([1, 1, 2], found.Events.Select(e => e.SchemaVersion));
        Assert.All(found.Events, e => Assert.Equal(DateTimeOffset.UnixEpoch, e.RecordedAt));
        Assert.Equal(
            new TaskEvent[] { new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1) },
            found.Events.Select(e => e.Event));
    }

    [Fact]
    public async Task History_reports_the_original_stored_schema_version_for_historical_v1_and_current_v2_events()
    {
        var created = History(new TaskCreated(T1))[0];
        var assigned = Of(2, new TaskAssigned(Agent));
        var historicalV1 = Raw(3, "CodeGenerated", 1, """{"generationId":"g1","artifactRef":"artifact-1"}""");
        var currentV2 = Raw(4, "CodeGenerated", 2, """{"generationId":"g2","artifactReference":"artifact-2"}""");
        var (handler, _, _) = Create(Found(WorkflowStatus.Created, null, null, null, 1), [created, assigned, historicalV1, currentV2]);

        var found = Assert.IsType<TaskHistoryFound>(await handler.GetHistoryAsync(T1, default));

        Assert.Equal([1, 1, 1, 2], found.Events.Select(e => e.SchemaVersion));
        Assert.Equal(new CodeGenerated(G1, Art1), found.Events[2].Event);
        Assert.Equal(new CodeGenerated(G2, Art2), found.Events[3].Event);
    }

    [Theory]
    [MemberData(nameof(TaskCommandHandlerTests.BadHistories), MemberType = typeof(TaskCommandHandlerTests))]
    public async Task Unreadable_history_is_reported_at_its_position_with_no_partial_result(
        StoredEvent[] history, InvalidHistoryReason reason, int position)
    {
        var (handler, store, _) = Create(null, history);

        Assert.Equal(new QueryInvalidHistory(reason, position), await handler.GetHistoryAsync(T1, default));
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task A_corrupted_stream_from_the_store_is_invalid_history()
    {
        var store = new FakeEventStore([]) { LoadException = new EventStreamCorruptedException(Stream, "gap") };
        var handler = new TaskQueryHandler(store, new FakeOverviewReader(NoRowNoEvents));

        Assert.Equal(
            new QueryInvalidHistory(InvalidHistoryReason.StreamCorrupted, null),
            await handler.GetHistoryAsync(T1, default));
    }

    [Fact]
    public async Task Other_store_failures_propagate_from_history_and_the_token_reaches_the_store()
    {
        var store = new FakeEventStore([]) { LoadException = new InvalidOperationException("boom") };
        var handler = new TaskQueryHandler(store, new FakeOverviewReader(NoRowNoEvents));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.GetHistoryAsync(T1, cts.Token));

        Assert.Equal(cts.Token, store.LastLoadToken);
    }
}
