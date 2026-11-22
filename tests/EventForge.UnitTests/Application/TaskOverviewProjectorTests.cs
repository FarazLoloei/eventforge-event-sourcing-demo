using EventForge.UnitTests.Domain;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;

namespace EventForge.UnitTests.Application;

/// <summary>
/// The pure <c>task_overview</c> reducer. Expected rows are written by hand, and every prefix of each history is also
/// compared with the aggregate's own fold (<see cref="TaskAggregate.Rehydrate"/>) as an independent reference.
/// </summary>
public sealed class TaskOverviewProjectorTests
{
    private static readonly TaskId Id = TaskTestData.T1;
    private static readonly AgentId Agent = TaskTestData.Agent;
    private static readonly GenerationId G1 = TaskTestData.G1;
    private static readonly GenerationId G2 = TaskTestData.G2;
    private static readonly ArtifactRef Art = TaskTestData.Art1;

    private static TaskOverviewRow Row(
        WorkflowStatus status, string? agent, string? generation, TestOutcome? result, int version) =>
        new(Id.Value, status, agent, generation, result, version);

    private static TaskOverviewFault FaultOf(Action action) =>
        Assert.Throws<TaskOverviewProjectionException>(action).Fault;

    [Fact]
    public void TaskCreated_ProducesTheFirstRowAtVersionOne()
    {
        var row = TaskOverviewProjector.Reduce(null, new TaskCreated(Id), 1);

        Assert.Equal(Row(WorkflowStatus.Created, null, null, null, 1), row);
    }

    [Fact]
    public void EachTransition_ProducesTheExpectedRowAndVersion()
    {
        var created = Row(WorkflowStatus.Created, null, null, null, 1);
        var assigned = Row(WorkflowStatus.Assigned, "agent-1", null, null, 2);
        var generated = Row(WorkflowStatus.Assigned, "agent-1", "g1", null, 3);
        var failed = Row(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Failed, 4);
        var regenerated = Row(WorkflowStatus.Assigned, "agent-1", "g2", null, 5);
        var passed = Row(WorkflowStatus.Assigned, "agent-1", "g2", TestOutcome.Passed, 6);
        var completed = Row(WorkflowStatus.Completed, "agent-1", "g2", TestOutcome.Passed, 7);

        Assert.Equal(assigned, TaskOverviewProjector.Reduce(created, new TaskAssigned(Agent), 2));
        Assert.Equal(generated, TaskOverviewProjector.Reduce(assigned, new CodeGenerated(G1, Art), 3));
        Assert.Equal(failed, TaskOverviewProjector.Reduce(generated, new TestsFailed(G1), 4));
        Assert.Equal(regenerated, TaskOverviewProjector.Reduce(failed, new CodeGenerated(G2, Art), 5));
        Assert.Equal(passed, TaskOverviewProjector.Reduce(regenerated, new TestsPassed(G2), 6));
        Assert.Equal(completed, TaskOverviewProjector.Reduce(passed, new TaskCompleted(), 7));
    }

    [Fact]
    public void ANewGeneration_ClearsThePreviousTestResult_EvenAfterAPass()
    {
        var passed = Row(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Passed, 4);

        var row = TaskOverviewProjector.Reduce(passed, new CodeGenerated(G2, Art), 5);

        Assert.Equal(Row(WorkflowStatus.Assigned, "agent-1", "g2", null, 5), row);
    }

    public static TheoryData<string> HistoryNames => new() { "happy path", "recovery path", "regeneration after a pass" };

    private static IReadOnlyList<TaskEvent> History(string name) => name switch
    {
        "happy path" =>
            [new TaskCreated(Id), new TaskAssigned(Agent), new CodeGenerated(G1, Art), new TestsPassed(G1), new TaskCompleted()],
        "recovery path" =>
            [new TaskCreated(Id), new TaskAssigned(Agent), new CodeGenerated(G1, Art), new TestsFailed(G1),
             new CodeGenerated(G2, Art), new TestsPassed(G2), new TaskCompleted()],
        "regeneration after a pass" =>
            [new TaskCreated(Id), new TaskAssigned(Agent), new CodeGenerated(G1, Art), new TestsPassed(G1),
             new CodeGenerated(G2, Art), new TestsFailed(G2)],
        _ => throw new ArgumentException("Unknown history.", nameof(name)),
    };

    [Theory]
    [MemberData(nameof(HistoryNames))]
    public void EveryPrefixOfAHistory_MatchesTheAggregateFold(string name)
    {
        var history = History(name);
        TaskOverviewRow? row = null;

        for (var i = 0; i < history.Count; i++)
        {
            row = TaskOverviewProjector.Reduce(row, history[i], i + 1);

            var state = TaskAggregate.Rehydrate(history.Take(i + 1));
            Assert.Equal(state.Id!.Value, row.TaskId);
            Assert.Equal(state.Status, row.Status);
            Assert.Equal(state.AssignedAgent?.Value, row.AssignedAgentId);
            Assert.Equal(state.CurrentGeneration?.Value, row.CurrentGenerationId);
            Assert.Equal(state.CurrentTestOutcome, row.LatestTestResult);
            Assert.Equal(state.Version, row.LastStreamVersion);
        }
    }

    [Theory]
    [MemberData(nameof(HistoryNames))]
    public void FoldingABatchEqualsFoldingTheEventsOneByOne(string name)
    {
        var history = History(name);
        TaskOverviewRow? stepwise = null;
        for (var i = 0; i < history.Count; i++)
        {
            stepwise = TaskOverviewProjector.Reduce(stepwise, history[i], i + 1);
        }

        Assert.Equal(stepwise, TaskOverviewProjector.ReduceBatch(null, history, 1));
    }

    [Fact]
    public void ABatchContinuesFromAnExistingRow()
    {
        var assigned = Row(WorkflowStatus.Assigned, "agent-1", null, null, 2);

        var row = TaskOverviewProjector.ReduceBatch(assigned, [new CodeGenerated(G1, Art), new TestsPassed(G1)], 3);

        Assert.Equal(Row(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Passed, 4), row);
    }

    [Fact]
    public void ABatchStopsAtTheFirstInvalidEvent_AndThrows()
    {
        var fault = FaultOf(() => TaskOverviewProjector.ReduceBatch(
            null, [new TaskCreated(Id), new TaskAssigned(Agent), new TaskAssigned(Agent)], 1));

        Assert.Equal(TaskOverviewFault.InvalidTransition, fault);
    }

    [Fact]
    public void AnEmptyBatchIsAProgrammerError() =>
        Assert.Throws<ArgumentException>(() => TaskOverviewProjector.ReduceBatch(null, [], 1));

    [Fact]
    public void AContinuingEventWithoutARow_IsRefused_NotBackfilled() =>
        Assert.Equal(TaskOverviewFault.MissingRow, FaultOf(() => TaskOverviewProjector.Reduce(null, new TaskAssigned(Agent), 2)));

    [Fact]
    public void TaskCreated_ForAnExistingRow_IsRefused() =>
        Assert.Equal(
            TaskOverviewFault.RowAlreadyExists,
            FaultOf(() => TaskOverviewProjector.Reduce(Row(WorkflowStatus.Created, null, null, null, 1), new TaskCreated(Id), 1)));

    [Fact]
    public void TaskCreated_AtAnyVersionOtherThanOne_IsRefused() =>
        Assert.Equal(TaskOverviewFault.VersionNotContiguous, FaultOf(() => TaskOverviewProjector.Reduce(null, new TaskCreated(Id), 2)));

    [Theory]
    [InlineData(2)] // duplicate: the row is already at this version
    [InlineData(1)] // stale
    [InlineData(4)] // gap
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonContiguousVersion_IsRefused(int streamVersion)
    {
        var assigned = Row(WorkflowStatus.Assigned, "agent-1", null, null, 2);

        Assert.Equal(
            TaskOverviewFault.VersionNotContiguous,
            FaultOf(() => TaskOverviewProjector.Reduce(assigned, new CodeGenerated(G1, Art), streamVersion)));
    }

    [Fact]
    public void ARowAtTheMaximumVersion_CannotAdvance() =>
        Assert.Equal(
            TaskOverviewFault.VersionNotContiguous,
            FaultOf(() => TaskOverviewProjector.Reduce(
                Row(WorkflowStatus.Assigned, "agent-1", "g1", null, int.MaxValue), new CodeGenerated(G2, Art), int.MinValue)));

    public static TheoryData<string, int> InvalidTransitions => new()
    {
        { "assign when already assigned", 0 },
        { "generate when only created", 1 },
        { "pass without a generation", 2 },
        { "pass another generation", 3 },
        { "fail when created", 4 },
        { "complete without a result", 5 },
        { "complete after a failure", 6 },
        { "assign when completed", 7 },
        { "generate when completed", 8 },
    };

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void AnEventThatCannotFollowTheRow_IsRefused(string scenario, int index)
    {
        var created = Row(WorkflowStatus.Created, null, null, null, 1);
        var assigned = Row(WorkflowStatus.Assigned, "agent-1", null, null, 2);
        var generated = Row(WorkflowStatus.Assigned, "agent-1", "g1", null, 3);
        var failed = Row(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Failed, 4);
        var completed = Row(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Passed, 5);

        (TaskOverviewRow Row, TaskEvent Event)[] cases =
        [
            (assigned, new TaskAssigned(Agent)),
            (created, new CodeGenerated(G1, Art)),
            (assigned, new TestsPassed(G1)),
            (generated, new TestsPassed(G2)),
            (created, new TestsFailed(G1)),
            (generated, new TaskCompleted()),
            (failed, new TaskCompleted()),
            (completed, new TaskAssigned(Agent)),
            (completed, new CodeGenerated(G2, Art)),
        ];

        var (row, @event) = cases[index];

        Assert.True(scenario.Length > 0);
        Assert.Equal(
            TaskOverviewFault.InvalidTransition,
            FaultOf(() => TaskOverviewProjector.Reduce(row, @event, row.LastStreamVersion + 1)));
    }

    [Fact]
    public void ATestResultForASupersededGeneration_IsRefused()
    {
        var regenerated = Row(WorkflowStatus.Assigned, "agent-1", "g2", null, 5);

        Assert.Equal(
            TaskOverviewFault.InvalidTransition,
            FaultOf(() => TaskOverviewProjector.Reduce(regenerated, new TestsFailed(G1), 6)));
    }

    public static TheoryData<int> InconsistentRows => new() { 0, 1, 2, 3, 4, 5, 6 };

    [Theory]
    [MemberData(nameof(InconsistentRows))]
    public void AnInconsistentExistingRow_IsRefusedBeforeAnyTransition(int index)
    {
        TaskOverviewRow[] rows =
        [
            Row(WorkflowStatus.NotCreated, null, null, null, 1),
            Row(WorkflowStatus.Created, "agent-1", null, null, 1),
            Row(WorkflowStatus.Assigned, null, null, null, 2),
            Row(WorkflowStatus.Assigned, "agent-1", null, TestOutcome.Passed, 4),
            Row(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Failed, 5),
            Row(WorkflowStatus.Assigned, "agent-1", null, null, 0),
            new TaskOverviewRow(Guid.Empty, WorkflowStatus.Created, null, null, null, 1),
        ];

        Assert.Equal(
            TaskOverviewFault.InvalidRow,
            FaultOf(() => TaskOverviewProjector.Reduce(rows[index], new TaskAssigned(Agent), rows[index].LastStreamVersion + 1)));
    }

    public static TheoryData<string, int> UnreachableRows => new()
    {
        { "created at version 5", 0 },
        { "created at version 2", 1 },
        { "assigned at version 1", 2 },
        { "assigned without a generation at version 3", 3 },
        { "assigned with a generation at version 2", 4 },
        { "assigned with a test result at version 3", 5 },
        { "completed at version 4", 6 },
        { "completed at version 1", 7 },
    };

    [Theory]
    [MemberData(nameof(UnreachableRows))]
    public void ARowWhoseVersionNoValidHistoryCouldReach_IsRefused(string scenario, int index)
    {
        TaskOverviewRow[] rows =
        [
            Row(WorkflowStatus.Created, null, null, null, 5),
            Row(WorkflowStatus.Created, null, null, null, 2),
            Row(WorkflowStatus.Assigned, "agent-1", null, null, 1),
            Row(WorkflowStatus.Assigned, "agent-1", null, null, 3),
            Row(WorkflowStatus.Assigned, "agent-1", "g1", null, 2),
            Row(WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Failed, 3),
            Row(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Passed, 4),
            Row(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Passed, 1),
        ];
        var row = rows[index];

        Assert.True(scenario.Length > 0);
        Assert.Equal(
            TaskOverviewFault.InvalidRow,
            FaultOf(() => TaskOverviewProjector.Reduce(row, new TaskAssigned(Agent), row.LastStreamVersion + 1)));
    }

    [Theory]
    [InlineData(WorkflowStatus.Assigned, null, null, 2)]
    [InlineData(WorkflowStatus.Assigned, "g1", null, 3)]
    [InlineData(WorkflowStatus.Assigned, "g1", TestOutcome.Failed, 4)]
    [InlineData(WorkflowStatus.Assigned, "g2", TestOutcome.Passed, 9)]
    public void RowsAtOrAboveTheMinimumVersionAreNotRefusedAsUnreachable(
        WorkflowStatus status, string? generation, TestOutcome? result, int version)
    {
        var row = Row(status, "agent-1", generation, result, version);

        // A refusal here must be a transition fault (TaskAssigned cannot follow an assigned row), never InvalidRow.
        Assert.Equal(
            TaskOverviewFault.InvalidTransition,
            FaultOf(() => TaskOverviewProjector.Reduce(row, new TaskAssigned(Agent), version + 1)));
    }

    [Fact]
    public void ACompletedRowAtTheMinimumVersion_IsAcceptedAsAStartingPoint() =>
        Assert.Equal(
            TaskOverviewFault.InvalidTransition,
            FaultOf(() => TaskOverviewProjector.Reduce(
                Row(WorkflowStatus.Completed, "agent-1", "g1", TestOutcome.Passed, 5), new TaskAssigned(Agent), 6)));

    [Fact]
    public void ANullEventIsAProgrammerError() =>
        Assert.Throws<ArgumentNullException>(() => TaskOverviewProjector.Reduce(null, null!, 1));
}
