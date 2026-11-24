namespace EventForge.Workflow.Domain;

/// <summary>
/// A recorded Task fact. The set of events is closed to this assembly. The record is the in-memory canonical
/// fact, independent of the stored schema version (see <see cref="TaskEventContract.CurrentSchemaVersionOf"/>); the
/// event id, stream position, metadata and recording time belong to the storage envelope, not to the payload.
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

/// <summary>
/// Stored as schema v2 <c>{ "generationId", "artifactReference" }</c>; historical v1 <c>{ "generationId", "artifactRef" }</c>
/// is upcast on read. The Domain property keeps the name <c>ArtifactRef</c>.
/// </summary>
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

    /// <summary>Schema version written, and the only version read directly, for every event type except CodeGenerated.</summary>
    public const int InitialSchemaVersion = 1;

    /// <summary>Schema version written for new <c>CodeGenerated</c> events (EVF-42): <c>artifactRef</c> became <c>artifactReference</c>.</summary>
    public const int CodeGeneratedSchemaVersion = 2;

    /// <summary>
    /// The schema version new events of the given type are written with. Versions are per event type: only
    /// <c>CodeGenerated</c> is at version 2. Historical <c>CodeGenerated</c> version 1 events remain readable through the
    /// explicit upcast.
    /// </summary>
    /// <exception cref="ArgumentException">The event type is not a Task event.</exception>
    public static int CurrentSchemaVersionOf(string eventType) => eventType switch
    {
        CodeGenerated => CodeGeneratedSchemaVersion,
        TaskCreated or TaskAssigned or TestsPassed or TestsFailed or TaskCompleted => InitialSchemaVersion,
        _ => throw new ArgumentException("The event type is not a Task event.", nameof(eventType)),
    };

    public const string TaskCreated = "TaskCreated";

    public const string TaskAssigned = "TaskAssigned";

    public const string CodeGenerated = "CodeGenerated";

    public const string TestsPassed = "TestsPassed";

    public const string TestsFailed = "TestsFailed";

    public const string TaskCompleted = "TaskCompleted";
}
