using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Application.StoredEvents;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

public sealed class TaskCommandHandlerTests
{
    private static readonly StreamKey Stream = new("Task", T1.Value);

    /// <summary>
    /// A scripted test double. It models a single stream's version check so the handler flow can be exercised,
    /// but it is not evidence of PostgreSQL behavior (that is covered by the integration tests).
    /// </summary>
    private sealed class FakeEventStore : IEventStore
    {
        public List<StoredEvent> Events { get; } = [];

        public int LoadCalls { get; private set; }

        public int AppendCalls { get; private set; }

        public int? LastExpectedVersion { get; private set; }

        public IReadOnlyList<NewEvent> LastAppended { get; private set; } = [];

        public CancellationToken LastLoadToken { get; private set; }

        public Exception? LoadException { get; set; }

        public Exception? AppendException { get; set; }

        public AppendResult? ForcedAppendResult { get; set; }

        public Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
        {
            LoadCalls++;
            LastLoadToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadException is not null)
            {
                throw LoadException;
            }

            return Task.FromResult(new LoadedStream(stream, Events.Count, Events.ToList()));
        }

        public Task<AppendResult> AppendAsync(
            StreamKey stream,
            int expectedVersion,
            IReadOnlyList<NewEvent> events,
            CancellationToken cancellationToken)
        {
            AppendCalls++;
            LastExpectedVersion = expectedVersion;
            LastAppended = events;
            if (AppendException is not null)
            {
                throw AppendException;
            }

            if (ForcedAppendResult is not null)
            {
                return Task.FromResult(ForcedAppendResult);
            }

            if (expectedVersion != Events.Count)
            {
                return Task.FromResult(AppendResult.VersionConflict(Events.Count));
            }

            var stored = events.Select((e, i) => new StoredEvent(
                e.EventId, stream.StreamType, stream.StreamId, expectedVersion + 1 + i, e.EventType,
                e.SchemaVersion, e.Payload, e.Metadata, DateTimeOffset.UnixEpoch)).ToList();
            Events.AddRange(stored);
            return Task.FromResult(AppendResult.Appended(Events.Count, stored));
        }
    }

    private static (TaskCommandHandler Handler, FakeEventStore Store) Create(params StoredEvent[] seeded)
    {
        var store = new FakeEventStore();
        store.Events.AddRange(seeded);
        var counter = 0;
        var handler = new TaskCommandHandler(store, () => new Guid(++counter, 0, 0, new byte[8]));
        return (handler, store);
    }

    private static Task<TaskCommandResult> Run(
        TaskCommandHandler handler,
        TaskCommand command,
        int expectedVersion,
        string? correlationId = null,
        CancellationToken token = default) =>
        handler.HandleAsync(command, expectedVersion, correlationId, token);

    // ---- success and atomic batch ----

    [Fact]
    public async Task Creating_a_task_appends_one_schema_v1_event_at_expected_version_zero()
    {
        var (handler, store) = Create();

        var result = await Run(handler, new CreateTask(T1), 0);

        var succeeded = Assert.IsType<CommandSucceeded>(result);
        Assert.Equal(1, succeeded.NewVersion);
        Assert.Equal(new TaskEvent[] { new TaskCreated(T1) }, succeeded.Events);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(1, store.AppendCalls);
        Assert.Equal(0, store.LastExpectedVersion);
        var appended = Assert.Single(store.LastAppended);
        Assert.Equal("TaskCreated", appended.EventType);
        Assert.Equal(1, appended.SchemaVersion);
        Assert.Equal("{}", appended.Metadata.GetRawText());
    }

    [Fact]
    public async Task A_full_lifecycle_runs_through_the_handler_and_rebuilds_to_the_expected_state()
    {
        var (handler, store) = Create();
        var steps = new (TaskCommand Command, int ExpectedVersion)[]
        {
            (new CreateTask(T1), 0),
            (new AssignTask(T1, Agent), 1),
            (new RecordCodeGenerated(T1, G1, Art1), 2),
            (new RecordTestsPassed(T1, G1), 3),
            (new CompleteTask(T1), 4),
        };

        foreach (var (command, expectedVersion) in steps)
        {
            var succeeded = Assert.IsType<CommandSucceeded>(await Run(handler, command, expectedVersion));
            Assert.Equal(expectedVersion + 1, succeeded.NewVersion);
        }

        var state = TaskAggregate.Rehydrate(store.Events.Select(TaskEventMapper.FromStored));
        AssertSameState(Completed(), state);
        Assert.Equal(5, store.AppendCalls);
    }

    [Fact]
    public async Task Appended_events_use_the_injected_event_ids_and_the_correlation_id()
    {
        var (handler, store) = Create();

        await Run(handler, new CreateTask(T1), 0, "corr-1");

        var appended = Assert.Single(store.LastAppended);
        Assert.Equal(new Guid(1, 0, 0, new byte[8]), appended.EventId);
        Assert.Equal("""{"correlationId":"corr-1"}""", appended.Metadata.GetRawText());
    }

    // ---- domain rejections write nothing ----

    public static IEnumerable<object[]> Rejections() =>
    [
        [History(new TaskCreated(T1)), new CreateTask(T1), RejectionCode.TaskAlreadyExists],
        [Array.Empty<StoredEvent>(), new AssignTask(T1, Agent), RejectionCode.TaskNotFound],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1), new TestsPassed(G1), new TaskCompleted()),
            new AssignTask(T1, Agent), RejectionCode.TaskAlreadyCompleted],
        [History(new TaskCreated(T1), new TaskAssigned(Agent)), new AssignTask(T1, Agent), RejectionCode.TaskAlreadyAssigned],
        [History(new TaskCreated(T1)), new RecordCodeGenerated(T1, G1, Art1), RejectionCode.TaskNotAssigned],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1)),
            new RecordCodeGenerated(T1, G1, Art2), RejectionCode.GenerationAlreadyUsed],
        [History(new TaskCreated(T1), new TaskAssigned(Agent)), new RecordTestsPassed(T1, G1), RejectionCode.NoGeneration],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1)),
            new RecordTestsFailed(T1, G9), RejectionCode.UnknownGeneration],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1), new CodeGenerated(G2, Art2)),
            new RecordTestsPassed(T1, G1), RejectionCode.SupersededGeneration],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new CodeGenerated(G1, Art1)),
            new CompleteTask(T1), RejectionCode.TestsNotPassed],
    ];

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task A_domain_rejection_is_returned_and_nothing_is_appended(
        StoredEvent[] history,
        TaskCommand command,
        RejectionCode code)
    {
        var (handler, store) = Create(history);

        var result = await Run(handler, command, history.Length);

        Assert.Equal(new CommandRejected(code), result);
        Assert.Equal(0, store.AppendCalls);
        Assert.Equal(history.Length, store.Events.Count);
    }

    // ---- expected version and conflicts ----

    [Fact]
    public async Task A_stale_expected_version_is_a_conflict_before_anything_is_decided_or_appended()
    {
        var (handler, store) = Create(History(new TaskCreated(T1)));

        // Both calls carry a stale version; the second command would also violate a domain rule.
        var stale = await Run(handler, new AssignTask(T1, Agent), 0);
        var staleAndInvalid = await Run(handler, new CreateTask(T1), 0);

        Assert.Equal(new VersionConflict(1), stale);
        Assert.Equal(new VersionConflict(1), staleAndInvalid);
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task An_expected_version_ahead_of_the_stream_is_a_conflict()
    {
        var (handler, store) = Create(History(new TaskCreated(T1)));

        Assert.Equal(new VersionConflict(1), await Run(handler, new AssignTask(T1, Agent), 5));
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task The_append_uses_the_loaded_version_as_its_expected_version()
    {
        var (handler, store) = Create(History(new TaskCreated(T1)));

        await Run(handler, new AssignTask(T1, Agent), 1);

        Assert.Equal(1, store.LastExpectedVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task A_conflict_from_the_store_is_returned_without_retry(int? currentVersion)
    {
        var (handler, store) = Create();
        store.ForcedAppendResult = AppendResult.VersionConflict(currentVersion);

        var result = await Run(handler, new CreateTask(T1), 0);

        Assert.Equal(new VersionConflict(currentVersion), result);
        Assert.Equal(1, store.AppendCalls);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public async Task A_duplicate_event_id_is_returned_without_retry()
    {
        var (handler, store) = Create();
        store.ForcedAppendResult = AppendResult.DuplicateEventId();

        var result = await Run(handler, new CreateTask(T1), 0);

        Assert.Equal(new DuplicateEventId(), result);
        Assert.Equal(1, store.AppendCalls);
    }

    [Fact]
    public async Task An_unknown_commit_outcome_is_returned_without_retry_and_without_the_exception()
    {
        var (handler, store) = Create();
        store.AppendException = new EventStoreCommitUnknownException(Stream, new IOException("connection reset"));

        var result = await Run(handler, new CreateTask(T1), 0);

        Assert.Equal(new CommitUnknown(), result);
        Assert.Equal(1, store.AppendCalls);
    }

    // ---- invalid history ----

    [Fact]
    public async Task A_corrupted_stream_on_load_is_invalid_history_and_nothing_is_appended()
    {
        var (handler, store) = Create();
        store.LoadException = new EventStreamCorruptedException(Stream, "gap");

        var result = await Run(handler, new CreateTask(T1), 0);

        Assert.Equal(new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null), result);
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task A_corrupted_stream_detected_by_the_append_is_invalid_history()
    {
        var (handler, store) = Create();
        store.AppendException = new EventStreamCorruptedException(Stream, "gap");

        Assert.Equal(
            new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null),
            await Run(handler, new CreateTask(T1), 0));
    }

    public static IEnumerable<object[]> BadHistories() =>
    [
        [new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskArchived", 1, "{}") },
            InvalidHistoryReason.UnknownEventType, 2],
        [new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskAssigned", 2, """{"agentId":"a"}""") },
            InvalidHistoryReason.UnsupportedSchemaVersion, 2],
        [new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskAssigned", 1, """{"agentId":"a","x":"y"}""") },
            InvalidHistoryReason.MalformedPayload, 2],
        [new[] { Raw(1, "TaskCreated", 1, """{"taskId":"22222222-2222-2222-2222-222222222222"}""") },
            InvalidHistoryReason.InvalidSequence, 1],
        [History(new TaskCreated(T1), new TaskAssigned(Agent), new TaskAssigned(Agent)),
            InvalidHistoryReason.InvalidSequence, 3],
        [new[] { History(new TaskAssigned(Agent))[0] },
            InvalidHistoryReason.InvalidSequence, 1],
        [new[] { History(new TaskCreated(T1))[0], Of(3, new TaskAssigned(Agent)) },
            InvalidHistoryReason.StreamCorrupted, 2],

        // Stream identity: every event must belong to the requested stream (type and id).
        [new[] { Raw(1, "TaskCreated", 1, """{"taskId":"22222222-2222-2222-2222-222222222222"}""", streamId: T2.Value) },
            InvalidHistoryReason.InvalidSequence, 1],
        [new[] { Raw(1, "TaskCompleted", 1, "{}", streamId: T2.Value) },
            InvalidHistoryReason.InvalidSequence, 1],
        [new[] { Raw(1, "TaskCreated", 1, """{"taskId":"11111111-1111-1111-1111-111111111111"}""", streamType: "Other") },
            InvalidHistoryReason.InvalidSequence, 1],
        [new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskAssigned", 1, """{"agentId":"a"}""", streamId: T2.Value) },
            InvalidHistoryReason.InvalidSequence, 2],
        [new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskAssigned", 1, """{"agentId":"a"}""", streamType: "Other") },
            InvalidHistoryReason.InvalidSequence, 2],
    ];

    [Theory]
    [MemberData(nameof(BadHistories))]
    public async Task Unreadable_stored_history_is_invalid_history_at_its_position_and_nothing_is_appended(
        StoredEvent[] history,
        InvalidHistoryReason reason,
        int position)
    {
        var (handler, store) = Create(history);

        var result = await Run(handler, new CompleteTask(T1), history.Length);

        Assert.Equal(new InvalidHistory(reason, position), result);
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task A_stale_expected_version_wins_over_an_unreadable_history()
    {
        // The shared history fold runs only after the version check, so a stale writer still gets a conflict.
        var history = new[] { History(new TaskCreated(T1))[0], Raw(2, "TaskArchived", 1, "{}") };
        var (handler, store) = Create(history);

        var result = await Run(handler, new CompleteTask(T1), expectedVersion: 1);

        Assert.Equal(new VersionConflict(2), result);
        Assert.Equal(0, store.AppendCalls);
    }

    // ---- arguments and cancellation ----

    [Fact]
    public async Task Cancellation_propagates_and_nothing_is_appended()
    {
        var (handler, store) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(handler, new CreateTask(T1), 0, token: cts.Token));
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_store()
    {
        var (handler, store) = Create();
        using var cts = new CancellationTokenSource();

        await Run(handler, new CreateTask(T1), 0, token: cts.Token);

        Assert.Equal(cts.Token, store.LastLoadToken);
    }

    [Fact]
    public async Task Programmer_errors_throw_before_the_store_is_touched()
    {
        var (handler, store) = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Run(handler, null!, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, new CreateTask(null!), 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Run(handler, new CreateTask(T1), -1));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, new CreateTask(T1), 0, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, new CreateTask(T1), 0, new string('c', 101)));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, new CreateTask(T1), 0, "a\nb"));
        Assert.Equal(0, store.LoadCalls);
    }

    private sealed record UnknownCommand(TaskId TaskId) : TaskCommand(TaskId);

    [Fact]
    public async Task A_malformed_command_throws_before_the_store_is_read_or_written()
    {
        var (handler, store) = Create(History(new TaskCreated(T1)));
        var malformed = new TaskCommand[]
        {
            new CreateTask(null!),
            new AssignTask(T1, null!),
            new AssignTask(null!, Agent),
            new RecordCodeGenerated(T1, null!, Art1),
            new RecordCodeGenerated(T1, G1, null!),
            new RecordTestsPassed(T1, null!),
            new RecordTestsFailed(T1, null!),
            new CompleteTask(null!),
            new UnknownCommand(T1),
        };

        foreach (var command in malformed)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Run(handler, command, 1));
        }

        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task An_empty_event_id_from_the_generator_throws_and_nothing_is_appended()
    {
        var store = new FakeEventStore();
        var handler = new TaskCommandHandler(store, () => Guid.Empty);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(handler, new CreateTask(T1), 0));
        Assert.Equal(0, store.AppendCalls);
    }
}
