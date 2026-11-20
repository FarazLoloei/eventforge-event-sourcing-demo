namespace EventForge.Workflow.Domain;

/// <summary>
/// A recorded Task fact. The set of events is closed to this assembly. The record is the in-memory canonical
/// payload for schema version <see cref="TaskEventContract.CurrentSchemaVersion"/>; the event id, stream position,
/// metadata and recording time belong to the storage envelope, not to the payload.
/// </summary>
public abstract record TaskEvent
{
    private protected TaskEvent()
    {
    }
}

/// <summary>Schema v1 payload: <c>{ "taskId" }</c>.</summary>
public sealed record TaskCreated(TaskId TaskId) : TaskEvent;

/// <summary>Schema v1 payload: <c>{ "agentId" }</c>.</summary>
public sealed record TaskAssigned(AgentId AgentId) : TaskEvent;

/// <summary>Schema v1 payload: <c>{ "generationId", "artifactRef" }</c>.</summary>
public sealed record CodeGenerated(GenerationId GenerationId, ArtifactRef ArtifactRef) : TaskEvent;

/// <summary>Schema v1 payload: <c>{ "generationId" }</c>.</summary>
public sealed record TestsPassed(GenerationId GenerationId) : TaskEvent;

/// <summary>Schema v1 payload: <c>{ "generationId" }</c>.</summary>
public sealed record TestsFailed(GenerationId GenerationId) : TaskEvent;

/// <summary>Schema v1 payload: the empty object <c>{}</c>.</summary>
public sealed record TaskCompleted : TaskEvent;

/// <summary>
/// Stable persisted names for the Task stream and its events. JSON property names are camelCase. Serialization and
/// mapping to the Event Store envelope are not part of this contract type.
/// </summary>
public static class TaskEventContract
{
    public const string StreamType = "Task";

    public const int CurrentSchemaVersion = 1;

    public const string TaskCreated = "TaskCreated";

    public const string TaskAssigned = "TaskAssigned";

    public const string CodeGenerated = "CodeGenerated";

    public const string TestsPassed = "TestsPassed";

    public const string TestsFailed = "TestsFailed";

    public const string TaskCompleted = "TaskCompleted";
}
