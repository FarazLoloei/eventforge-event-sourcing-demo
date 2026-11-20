namespace EventForge.Workflow.Domain;

/// <summary>A request to change a task. Commands are validated by the aggregate and may be rejected.</summary>
public abstract record TaskCommand(TaskId TaskId);

public sealed record CreateTask(TaskId TaskId) : TaskCommand(TaskId);

public sealed record AssignTask(TaskId TaskId, AgentId AgentId) : TaskCommand(TaskId);

public sealed record RecordCodeGenerated(TaskId TaskId, GenerationId GenerationId, ArtifactRef ArtifactRef)
    : TaskCommand(TaskId);

public sealed record RecordTestsPassed(TaskId TaskId, GenerationId GenerationId) : TaskCommand(TaskId);

public sealed record RecordTestsFailed(TaskId TaskId, GenerationId GenerationId) : TaskCommand(TaskId);

public sealed record CompleteTask(TaskId TaskId) : TaskCommand(TaskId);
