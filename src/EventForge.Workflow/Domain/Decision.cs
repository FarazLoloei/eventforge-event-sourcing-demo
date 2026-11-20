namespace EventForge.Workflow.Domain;

/// <summary>Outcome of deciding a command: new events, or a rejection that appends nothing.</summary>
public abstract record Decision;

public sealed record Accepted(IReadOnlyList<TaskEvent> Events) : Decision;

public sealed record Rejected(RejectionCode Code) : Decision;

/// <summary>Stable reasons a command can be rejected. Adapters map these to responses and safe messages.</summary>
public enum RejectionCode
{
    TaskIdMismatch,
    TaskAlreadyExists,
    TaskNotFound,
    TaskAlreadyCompleted,
    TaskAlreadyAssigned,
    TaskNotAssigned,
    GenerationAlreadyUsed,
    NoGeneration,
    UnknownGeneration,
    SupersededGeneration,
    TestsNotPassed,
}
