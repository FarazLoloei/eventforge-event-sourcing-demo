namespace EventForge.Workflow.Domain;

/// <summary>
/// Pure, deterministic Task decision and fold logic. No I/O, clock, randomness or ambient state.
/// <see cref="Decide"/> validates intent against the current state; <see cref="Apply"/> folds recorded facts and
/// checks only that they form a valid schema v1 history, without calling <see cref="Decide"/>.
/// </summary>
public static class TaskAggregate
{
    /// <summary>
    /// Decides a command. Malformed commands (null, null members, unknown type) are programmer errors and throw
    /// <see cref="ArgumentException"/> before any business rule is evaluated. Business rule violations return
    /// <see cref="Rejected"/> and produce no events.
    /// </summary>
    public static Decision Decide(TaskState state, TaskCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);

        if (state.Id is not null && command.TaskId != state.Id)
        {
            return Reject(RejectionCode.TaskIdMismatch);
        }

        if (command is CreateTask create)
        {
            return state.Status != WorkflowStatus.NotCreated
                ? Reject(RejectionCode.TaskAlreadyExists)
                : Accept(new TaskCreated(create.TaskId));
        }

        if (state.Status == WorkflowStatus.NotCreated)
        {
            return Reject(RejectionCode.TaskNotFound);
        }

        if (state.Status == WorkflowStatus.Completed)
        {
            return Reject(RejectionCode.TaskAlreadyCompleted);
        }

        return command switch
        {
            AssignTask c => state.Status == WorkflowStatus.Assigned
                ? Reject(RejectionCode.TaskAlreadyAssigned)
                : Accept(new TaskAssigned(c.AgentId)),

            RecordCodeGenerated c =>
                state.Status != WorkflowStatus.Assigned ? Reject(RejectionCode.TaskNotAssigned) :
                state.UsedGenerations.Contains(c.GenerationId) ? Reject(RejectionCode.GenerationAlreadyUsed) :
                Accept(new CodeGenerated(c.GenerationId, c.ArtifactRef)),

            RecordTestsPassed c => DecideTestResult(state, c.GenerationId, new TestsPassed(c.GenerationId)),

            RecordTestsFailed c => DecideTestResult(state, c.GenerationId, new TestsFailed(c.GenerationId)),

            CompleteTask =>
                state.Status != WorkflowStatus.Assigned ? Reject(RejectionCode.TaskNotAssigned) :
                state.CurrentTestOutcome != TestOutcome.Passed ? Reject(RejectionCode.TestsNotPassed) :
                Accept(new TaskCompleted()),

            _ => throw new ArgumentException("The command type is not supported.", nameof(command)),
        };
    }

    /// <summary>
    /// Folds one recorded event. Throws <see cref="InvalidEventHistoryException"/> (position and event type only)
    /// if the event is null, malformed, or cannot follow the history folded so far.
    /// </summary>
    public static TaskState Apply(TaskState state, TaskEvent @event)
    {
        ArgumentNullException.ThrowIfNull(state);

        var position = state.Version + 1;

        switch (@event)
        {
            case TaskCreated e when e.TaskId is not null && state.Status == WorkflowStatus.NotCreated:
                return state with { Id = e.TaskId, Status = WorkflowStatus.Created, Version = position };

            case TaskAssigned e when e.AgentId is not null && state.Status == WorkflowStatus.Created:
                return state with
                {
                    AssignedAgent = e.AgentId,
                    Status = WorkflowStatus.Assigned,
                    Version = position,
                };

            case CodeGenerated e when e.GenerationId is not null
                && e.ArtifactRef is not null
                && state.Status == WorkflowStatus.Assigned
                && !state.UsedGenerations.Contains(e.GenerationId):
                return state with
                {
                    UsedGenerations = state.UsedGenerations.Add(e.GenerationId),
                    CurrentGeneration = e.GenerationId,
                    CurrentTestOutcome = null,
                    Version = position,
                };

            case TestsPassed e when IsCurrentGeneration(state, e.GenerationId):
                return state with { CurrentTestOutcome = TestOutcome.Passed, Version = position };

            case TestsFailed e when IsCurrentGeneration(state, e.GenerationId):
                return state with { CurrentTestOutcome = TestOutcome.Failed, Version = position };

            case TaskCompleted
                when state.Status == WorkflowStatus.Assigned && state.CurrentTestOutcome == TestOutcome.Passed:
                return state with { Status = WorkflowStatus.Completed, Version = position };

            default:
                throw new InvalidEventHistoryException(
                    $"Invalid event history at position {position} ({@event?.GetType().Name ?? "null"}).");
        }
    }

    /// <summary>Folds an ordered history from <see cref="TaskState.Initial"/>. An empty history yields the initial state.</summary>
    public static TaskState Rehydrate(IEnumerable<TaskEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var state = TaskState.Initial;
        foreach (var @event in history)
        {
            state = Apply(state, @event);
        }

        return state;
    }

    private static Decision DecideTestResult(TaskState state, GenerationId generationId, TaskEvent result) =>
        state.CurrentGeneration is null ? Reject(RejectionCode.NoGeneration) :
        !state.UsedGenerations.Contains(generationId) ? Reject(RejectionCode.UnknownGeneration) :
        generationId != state.CurrentGeneration ? Reject(RejectionCode.SupersededGeneration) :
        Accept(result);

    private static bool IsCurrentGeneration(TaskState state, GenerationId? generationId) =>
        generationId is not null
        && state.Status == WorkflowStatus.Assigned
        && generationId == state.CurrentGeneration;

    private static void ValidateCommand(TaskCommand command)
    {
        var valid = command switch
        {
            CreateTask c => c.TaskId is not null,
            AssignTask c => c.TaskId is not null && c.AgentId is not null,
            RecordCodeGenerated c => c.TaskId is not null && c.GenerationId is not null && c.ArtifactRef is not null,
            RecordTestsPassed c => c.TaskId is not null && c.GenerationId is not null,
            RecordTestsFailed c => c.TaskId is not null && c.GenerationId is not null,
            CompleteTask c => c.TaskId is not null,
            _ => throw new ArgumentException("The command type is not supported.", nameof(command)),
        };

        if (!valid)
        {
            throw new ArgumentException("The command has a missing required value.", nameof(command));
        }
    }

    private static Accepted Accept(TaskEvent @event) => new([@event]);

    private static Rejected Reject(RejectionCode code) => new(code);
}
