using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Domain;

public sealed class TaskAggregateDecideTests
{
    private sealed record UnknownCommand(TaskId TaskId) : TaskCommand(TaskId);

    private static Decision Decide(TaskState state, TaskCommand command) => TaskAggregate.Decide(state, command);

    // ---- accepted transitions ----

    [Fact]
    public void Create_on_the_initial_state_emits_TaskCreated() =>
        AssertAccepted(Decide(TaskState.Initial, new CreateTask(T1)), new TaskCreated(T1));

    [Fact]
    public void Assign_a_created_task_emits_TaskAssigned() =>
        AssertAccepted(Decide(Created(), new AssignTask(T1, Agent)), new TaskAssigned(Agent));

    [Fact]
    public void Generate_on_an_assigned_task_emits_CodeGenerated() =>
        AssertAccepted(
            Decide(Assigned(), new RecordCodeGenerated(T1, G1, Art1)),
            new CodeGenerated(G1, Art1));

    [Fact]
    public void A_new_generation_is_accepted_after_a_previous_one()
    {
        AssertAccepted(
            Decide(Generated(G1), new RecordCodeGenerated(T1, G2, Art2)),
            new CodeGenerated(G2, Art2));
        AssertAccepted(
            Decide(WithOutcome(Generated(G1), TestOutcome.Failed), new RecordCodeGenerated(T1, G2, Art2)),
            new CodeGenerated(G2, Art2));
    }

    [Fact]
    public void Test_results_for_the_current_generation_are_accepted_and_may_repeat()
    {
        AssertAccepted(Decide(Generated(G1), new RecordTestsPassed(T1, G1)), new TestsPassed(G1));
        AssertAccepted(Decide(Generated(G1), new RecordTestsFailed(T1, G1)), new TestsFailed(G1));

        var passed = WithOutcome(Generated(G1), TestOutcome.Passed);
        var failed = WithOutcome(Generated(G1), TestOutcome.Failed);
        AssertAccepted(Decide(passed, new RecordTestsPassed(T1, G1)), new TestsPassed(G1));
        AssertAccepted(Decide(passed, new RecordTestsFailed(T1, G1)), new TestsFailed(G1));
        AssertAccepted(Decide(failed, new RecordTestsPassed(T1, G1)), new TestsPassed(G1));
        AssertAccepted(Decide(failed, new RecordTestsFailed(T1, G1)), new TestsFailed(G1));
    }

    [Fact]
    public void Complete_is_accepted_when_the_current_generation_passed() =>
        AssertAccepted(
            Decide(WithOutcome(Generated(G1), TestOutcome.Passed), new CompleteTask(T1)),
            new TaskCompleted());

    [Fact]
    public void Complete_is_accepted_after_a_new_generation_passes()
    {
        var g2Passed = WithOutcome(Generated(G1, G2), TestOutcome.Passed);

        AssertAccepted(Decide(g2Passed, new CompleteTask(T1)), new TaskCompleted());
    }

    // ---- rejections, in precedence order ----

    [Fact]
    public void A_different_task_id_is_rejected_first()
    {
        AssertRejected(Decide(Created(), new CreateTask(T2)), RejectionCode.TaskIdMismatch);
        AssertRejected(Decide(Completed(), new AssignTask(T2, Agent)), RejectionCode.TaskIdMismatch);
        AssertRejected(Decide(Generated(G1), new RecordTestsPassed(T2, G1)), RejectionCode.TaskIdMismatch);
    }

    [Fact]
    public void Create_twice_is_rejected_even_when_completed()
    {
        AssertRejected(Decide(Created(), new CreateTask(T1)), RejectionCode.TaskAlreadyExists);
        AssertRejected(Decide(Completed(), new CreateTask(T1)), RejectionCode.TaskAlreadyExists);
    }

    [Fact]
    public void Every_non_create_command_on_a_missing_task_is_not_found() =>
        Assert.All(
            NonCreateCommands(),
            c => AssertRejected(Decide(TaskState.Initial, c), RejectionCode.TaskNotFound));

    [Fact]
    public void Every_non_create_command_on_a_completed_task_is_rejected() =>
        Assert.All(
            NonCreateCommands(),
            c => AssertRejected(Decide(Completed(), c), RejectionCode.TaskAlreadyCompleted));

    [Fact]
    public void Assign_twice_is_rejected_and_there_is_no_reassignment()
    {
        AssertRejected(Decide(Assigned(), new AssignTask(T1, Agent)), RejectionCode.TaskAlreadyAssigned);
        AssertRejected(Decide(Assigned(), new AssignTask(T1, new AgentId("agent-2"))), RejectionCode.TaskAlreadyAssigned);
    }

    [Fact]
    public void Generate_before_assignment_is_rejected() =>
        AssertRejected(Decide(Created(), new RecordCodeGenerated(T1, G1, Art1)), RejectionCode.TaskNotAssigned);

    [Fact]
    public void Generation_ids_are_unique_across_the_whole_history()
    {
        AssertRejected(
            Decide(Generated(G1), new RecordCodeGenerated(T1, G1, Art2)),
            RejectionCode.GenerationAlreadyUsed);
        AssertRejected(
            Decide(Generated(G1, G2), new RecordCodeGenerated(T1, G1, Art2)),
            RejectionCode.GenerationAlreadyUsed);
    }

    [Fact]
    public void Test_result_without_any_generation_is_rejected()
    {
        AssertRejected(Decide(Created(), new RecordTestsPassed(T1, G1)), RejectionCode.NoGeneration);
        AssertRejected(Decide(Assigned(), new RecordTestsFailed(T1, G1)), RejectionCode.NoGeneration);
    }

    [Fact]
    public void Test_result_for_an_unknown_generation_is_rejected()
    {
        AssertRejected(Decide(Generated(G1), new RecordTestsPassed(T1, G9)), RejectionCode.UnknownGeneration);
        AssertRejected(Decide(Generated(G1), new RecordTestsFailed(T1, G9)), RejectionCode.UnknownGeneration);
    }

    [Fact]
    public void Test_result_for_a_superseded_generation_is_rejected()
    {
        AssertRejected(Decide(Generated(G1, G2), new RecordTestsPassed(T1, G1)), RejectionCode.SupersededGeneration);
        AssertRejected(Decide(Generated(G1, G2), new RecordTestsFailed(T1, G1)), RejectionCode.SupersededGeneration);
    }

    [Fact]
    public void Complete_before_assignment_is_rejected_as_not_assigned() =>
        AssertRejected(Decide(Created(), new CompleteTask(T1)), RejectionCode.TaskNotAssigned);

    [Fact]
    public void Complete_without_a_passed_current_generation_is_rejected()
    {
        AssertRejected(Decide(Assigned(), new CompleteTask(T1)), RejectionCode.TestsNotPassed);
        AssertRejected(Decide(Generated(G1), new CompleteTask(T1)), RejectionCode.TestsNotPassed);
        AssertRejected(
            Decide(WithOutcome(Generated(G1), TestOutcome.Failed), new CompleteTask(T1)),
            RejectionCode.TestsNotPassed);
    }

    [Fact]
    public void A_new_generation_invalidates_an_earlier_pass()
    {
        // g1 passed, then g2 generated (outcome cleared): completion must wait for g2's own pass.
        AssertRejected(Decide(Generated(G1, G2), new CompleteTask(T1)), RejectionCode.TestsNotPassed);
    }

    [Fact]
    public void Rejections_leave_the_input_state_unchanged()
    {
        var state = Generated(G1, G2);
        var rejected = Decide(state, new RecordTestsPassed(T1, G1));

        AssertRejected(rejected, RejectionCode.SupersededGeneration);
        AssertSameState(Generated(G1, G2), state);
    }

    // ---- determinism ----

    [Fact]
    public void Deciding_the_same_input_twice_emits_value_equal_events()
    {
        var state = Generated(G1);
        var command = new RecordCodeGenerated(T1, G2, Art2);

        var first = Assert.IsType<Accepted>(Decide(state, command));
        var second = Assert.IsType<Accepted>(Decide(state, command));

        Assert.Equal(first.Events.Count, second.Events.Count);
        for (var i = 0; i < first.Events.Count; i++)
        {
            Assert.Equal(first.Events[i], second.Events[i]);
        }

        AssertSameState(Generated(G1), state);
    }

    // ---- malformed commands ----

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => Decide(null!, new CreateTask(T1)));
        Assert.Throws<ArgumentNullException>(() => Decide(TaskState.Initial, null!));
    }

    private static TaskCommand[] NullMemberCommands() =>
    [
        new CreateTask(null!),
        new AssignTask(null!, Agent),
        new AssignTask(T1, null!),
        new RecordCodeGenerated(null!, G1, Art1),
        new RecordCodeGenerated(T1, null!, Art1),
        new RecordCodeGenerated(T1, G1, null!),
        new RecordTestsPassed(null!, G1),
        new RecordTestsPassed(T1, null!),
        new RecordTestsFailed(null!, G1),
        new RecordTestsFailed(T1, null!),
        new CompleteTask(null!),
    ];

    [Fact]
    public void Commands_with_a_null_member_throw_for_every_command_type() =>
        Assert.All(
            NullMemberCommands(),
            c => Assert.ThrowsAny<ArgumentException>(() => Decide(Generated(G1), c)));

    [Fact]
    public void ValidateCommand_accepts_well_formed_commands_and_rejects_malformed_ones()
    {
        Assert.All(
            NonCreateCommands().Append(new CreateTask(T1)),
            c => TaskAggregate.ValidateCommand(c));

        Assert.Throws<ArgumentNullException>(() => TaskAggregate.ValidateCommand(null!));
        Assert.Throws<ArgumentException>(() => TaskAggregate.ValidateCommand(new UnknownCommand(T1)));
        Assert.All(
            NullMemberCommands(),
            c => Assert.ThrowsAny<ArgumentException>(() => TaskAggregate.ValidateCommand(c)));
    }

    [Fact]
    public void Malformed_commands_throw_before_business_rules_are_evaluated()
    {
        // Each of these would otherwise be a business rejection.
        Assert.ThrowsAny<ArgumentException>(() => Decide(TaskState.Initial, new AssignTask(T1, null!)));
        Assert.ThrowsAny<ArgumentException>(() => Decide(Completed(), new RecordTestsPassed(T1, null!)));
        Assert.ThrowsAny<ArgumentException>(() => Decide(Created(), new CreateTask(null!)));
    }

    [Fact]
    public void An_unknown_command_type_throws() =>
        Assert.Throws<ArgumentException>(() => Decide(Created(), new UnknownCommand(T1)));

    private static TaskCommand[] NonCreateCommands() =>
    [
        new AssignTask(T1, Agent),
        new RecordCodeGenerated(T1, G2, Art2),
        new RecordTestsPassed(T1, G1),
        new RecordTestsFailed(T1, G1),
        new CompleteTask(T1),
    ];
}
