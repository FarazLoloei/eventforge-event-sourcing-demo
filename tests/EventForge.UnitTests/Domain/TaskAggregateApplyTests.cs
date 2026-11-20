using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Domain;

public sealed class TaskAggregateApplyTests
{
    private static readonly TaskEvent EvCreated = new TaskCreated(T1);
    private static readonly TaskEvent EvAssigned = new TaskAssigned(Agent);
    private static readonly TaskEvent EvG1 = new CodeGenerated(G1, Art1);
    private static readonly TaskEvent EvG2 = new CodeGenerated(G2, Art2);
    private static readonly TaskEvent EvPassedG1 = new TestsPassed(G1);
    private static readonly TaskEvent EvFailedG1 = new TestsFailed(G1);
    private static readonly TaskEvent EvCompleted = new TaskCompleted();

    private static string Message(int position, string eventType) =>
        $"Invalid event history at position {position} ({eventType}).";

    // ---- folding valid history ----

    [Fact]
    public void An_empty_history_rehydrates_to_the_initial_state() =>
        AssertSameState(TaskState.Initial, TaskAggregate.Rehydrate([]));

    [Fact]
    public void A_full_history_folds_to_the_expected_completed_state() =>
        AssertSameState(
            Completed(),
            TaskAggregate.Rehydrate([EvCreated, EvAssigned, EvG1, EvPassedG1, EvCompleted]));

    [Fact]
    public void Each_event_folds_to_the_expected_state()
    {
        AssertSameState(Created(), TaskAggregate.Apply(TaskState.Initial, EvCreated));
        AssertSameState(Assigned(), TaskAggregate.Apply(Created(), EvAssigned));
        AssertSameState(Generated(G1), TaskAggregate.Apply(Assigned(), EvG1));
        AssertSameState(
            WithOutcome(Generated(G1), TestOutcome.Passed),
            TaskAggregate.Apply(Generated(G1), EvPassedG1));
        AssertSameState(
            WithOutcome(Generated(G1), TestOutcome.Failed),
            TaskAggregate.Apply(Generated(G1), EvFailedG1));
        AssertSameState(
            Completed(),
            TaskAggregate.Apply(WithOutcome(Generated(G1), TestOutcome.Passed), EvCompleted));
    }

    [Fact]
    public void The_latest_test_result_for_the_current_generation_wins()
    {
        var failedThenPassed = TaskAggregate.Rehydrate([EvCreated, EvAssigned, EvG1, EvFailedG1, EvPassedG1]);
        var passedThenFailed = TaskAggregate.Rehydrate([EvCreated, EvAssigned, EvG1, EvPassedG1, EvFailedG1]);
        var passedTwice = TaskAggregate.Rehydrate([EvCreated, EvAssigned, EvG1, EvPassedG1, EvPassedG1]);

        Assert.Equal(TestOutcome.Passed, failedThenPassed.CurrentTestOutcome);
        Assert.Equal(TestOutcome.Failed, passedThenFailed.CurrentTestOutcome);
        Assert.Equal(TestOutcome.Passed, passedTwice.CurrentTestOutcome);
        Assert.Equal(5, passedTwice.Version);
    }

    [Fact]
    public void A_new_generation_clears_the_outcome_and_keeps_every_used_id()
    {
        var state = TaskAggregate.Rehydrate([EvCreated, EvAssigned, EvG1, EvPassedG1, EvG2]);

        Assert.Equal(G2, state.CurrentGeneration);
        Assert.Null(state.CurrentTestOutcome);
        Assert.True(state.UsedGenerations.SetEquals([G1, G2]));
        Assert.Equal(5, state.Version);
    }

    [Fact]
    public void Apply_does_not_change_its_input_state()
    {
        var state = Generated(G1);

        _ = TaskAggregate.Apply(state, EvG2);

        AssertSameState(Generated(G1), state);
    }

    [Fact]
    public void Decided_events_fold_to_the_hand_written_state_at_every_step()
    {
        var steps = new (TaskCommand Command, TaskState Expected)[]
        {
            (new CreateTask(T1), Created()),
            (new AssignTask(T1, Agent), Assigned()),
            (new RecordCodeGenerated(T1, G1, Art1), Generated(G1)),
            (new RecordTestsFailed(T1, G1), WithOutcome(Generated(G1), TestOutcome.Failed)),
            (new RecordTestsPassed(T1, G1), WithOutcome(WithOutcome(Generated(G1), TestOutcome.Failed), TestOutcome.Passed)),
            (new CompleteTask(T1), WithOutcome(WithOutcome(Generated(G1), TestOutcome.Failed), TestOutcome.Passed) with
            {
                Status = WorkflowStatus.Completed,
                Version = 6,
            }),
        };

        var state = TaskState.Initial;
        foreach (var (command, expected) in steps)
        {
            var accepted = Assert.IsType<Accepted>(TaskAggregate.Decide(state, command));
            foreach (var @event in accepted.Events)
            {
                state = TaskAggregate.Apply(state, @event);
            }

            AssertSameState(expected, state);
        }
    }

    // ---- invalid history ----

    public static IEnumerable<object[]> InvalidHistories()
    {
        // First event is not TaskCreated.
        yield return Case([EvAssigned], 1, "TaskAssigned");
        yield return Case([EvG1], 1, "CodeGenerated");
        yield return Case([EvPassedG1], 1, "TestsPassed");
        yield return Case([EvFailedG1], 1, "TestsFailed");
        yield return Case([EvCompleted], 1, "TaskCompleted");

        // Second creation.
        yield return Case([EvCreated, EvCreated], 2, "TaskCreated");

        // Second assignment.
        yield return Case([EvCreated, EvAssigned, new TaskAssigned(new AgentId("agent-2"))], 3, "TaskAssigned");

        // Generation before assignment, and generation id reuse (current, same id with a different artifact, superseded).
        yield return Case([EvCreated, EvG1], 2, "CodeGenerated");
        yield return Case([EvCreated, EvAssigned, EvG1, EvG1], 4, "CodeGenerated");
        yield return Case([EvCreated, EvAssigned, EvG1, new CodeGenerated(G1, Art2)], 4, "CodeGenerated");
        yield return Case([EvCreated, EvAssigned, EvG1, EvG2, EvG1], 5, "CodeGenerated");

        // Test results without a generation, for an unknown or a superseded generation.
        yield return Case([EvCreated, EvPassedG1], 2, "TestsPassed");
        yield return Case([EvCreated, EvAssigned, EvFailedG1], 3, "TestsFailed");
        yield return Case([EvCreated, EvAssigned, EvG1, new TestsPassed(G9)], 4, "TestsPassed");
        yield return Case([EvCreated, EvAssigned, EvG1, new TestsFailed(G9)], 4, "TestsFailed");
        yield return Case([EvCreated, EvAssigned, EvG1, EvG2, EvPassedG1], 5, "TestsPassed");
        yield return Case([EvCreated, EvAssigned, EvG1, EvG2, EvFailedG1], 5, "TestsFailed");

        // Completion without a passed current generation.
        yield return Case([EvCreated, EvCompleted], 2, "TaskCompleted");
        yield return Case([EvCreated, EvAssigned, EvCompleted], 3, "TaskCompleted");
        yield return Case([EvCreated, EvAssigned, EvG1, EvCompleted], 4, "TaskCompleted");
        yield return Case([EvCreated, EvAssigned, EvG1, EvFailedG1, EvCompleted], 5, "TaskCompleted");
        yield return Case([EvCreated, EvAssigned, EvG1, EvPassedG1, EvG2, EvCompleted], 6, "TaskCompleted");

        // Any event after completion.
        TaskEvent[] completed = [EvCreated, EvAssigned, EvG1, EvPassedG1, EvCompleted];
        yield return Case([.. completed, EvCreated], 6, "TaskCreated");
        yield return Case([.. completed, new TaskAssigned(new AgentId("agent-2"))], 6, "TaskAssigned");
        yield return Case([.. completed, EvG2], 6, "CodeGenerated");
        yield return Case([.. completed, EvPassedG1], 6, "TestsPassed");
        yield return Case([.. completed, EvFailedG1], 6, "TestsFailed");
        yield return Case([.. completed, EvCompleted], 6, "TaskCompleted");

        static object[] Case(TaskEvent[] history, int position, string eventType) => [history, position, eventType];
    }

    [Theory]
    [MemberData(nameof(InvalidHistories))]
    public void Invalid_histories_fail_with_only_position_and_event_type(
        TaskEvent[] history,
        int position,
        string eventType)
    {
        var exception = Assert.Throws<InvalidEventHistoryException>(() => TaskAggregate.Rehydrate(history));

        Assert.Equal(Message(position, eventType), exception.Message);
    }

    [Fact]
    public void Diagnostics_never_contain_identifiers_or_payload_values()
    {
        var history = new TaskEvent[]
        {
            new TaskCreated(T1),
            new TaskAssigned(new AgentId("SECRET-AGENT-77")),
            new TaskAssigned(new AgentId("SECRET-AGENT-88")),
        };

        var exception = Assert.Throws<InvalidEventHistoryException>(() => TaskAggregate.Rehydrate(history));

        Assert.DoesNotContain("SECRET", exception.Message);
        Assert.DoesNotContain(T1.ToString(), exception.Message);

        var generated = new TaskEvent[]
        {
            EvCreated,
            EvAssigned,
            new CodeGenerated(new GenerationId("SECRET-GEN"), new ArtifactRef("SECRET-ARTIFACT")),
            new CodeGenerated(new GenerationId("SECRET-GEN"), new ArtifactRef("SECRET-ARTIFACT-2")),
        };

        exception = Assert.Throws<InvalidEventHistoryException>(() => TaskAggregate.Rehydrate(generated));
        Assert.DoesNotContain("SECRET", exception.Message);
    }

    // ---- null and malformed persisted events ----

    [Fact]
    public void A_null_event_is_invalid_history()
    {
        var first = Assert.Throws<InvalidEventHistoryException>(() => TaskAggregate.Apply(TaskState.Initial, null!));
        var inHistory = Assert.Throws<InvalidEventHistoryException>(
            () => TaskAggregate.Rehydrate(new TaskEvent?[] { EvCreated, null }!));

        Assert.Equal(Message(1, "null"), first.Message);
        Assert.Equal(Message(2, "null"), inHistory.Message);
    }

    [Fact]
    public void Events_with_a_null_member_are_invalid_history_and_never_corrupt_state()
    {
        var cases = new (TaskState State, TaskEvent Event, string Type)[]
        {
            (TaskState.Initial, new TaskCreated(null!), "TaskCreated"),
            (Created(), new TaskAssigned(null!), "TaskAssigned"),
            (Assigned(), new CodeGenerated(null!, Art1), "CodeGenerated"),
            (Assigned(), new CodeGenerated(G1, null!), "CodeGenerated"),
            (Generated(G1), new TestsPassed(null!), "TestsPassed"),
            (Generated(G1), new TestsFailed(null!), "TestsFailed"),
        };

        foreach (var (state, @event, type) in cases)
        {
            var exception = Assert.Throws<InvalidEventHistoryException>(() => TaskAggregate.Apply(state, @event));
            Assert.Equal(Message(state.Version + 1, type), exception.Message);
        }
    }

    // ---- null arguments ----

    [Fact]
    public void Null_state_or_history_throws()
    {
        Assert.Throws<ArgumentNullException>(() => TaskAggregate.Apply(null!, EvCreated));
        Assert.Throws<ArgumentNullException>(() => TaskAggregate.Rehydrate(null!));
    }
}
