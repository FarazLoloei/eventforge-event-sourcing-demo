using System.ComponentModel;

namespace EventForge.Api.Tasks;

// Request and response DTOs for the Task command endpoints (docs/api-contracts.md, Section 4). Request members are
// nullable so that a missing value is reported as a validation error instead of silently binding to a default.
// Property names bind case-insensitively (ASP.NET Core default); genuinely unknown members are rejected.

public sealed record CreateTaskRequest(
    [property: Description("Client-supplied task id: a non-empty GUID in canonical D format. Required.")]
    string? TaskId);

public sealed record AssignTaskRequest(
    [property: Description("The task version the client last saw (the 'version' of its last response). Integer >= 0. Required.")]
    int? ExpectedVersion,
    [property: Description("Opaque agent id, at most 100 characters, no control characters. Example: agent-demo-1. Required.")]
    string? AgentId);

public sealed record RecordGenerationRequest(
    [property: Description("The task version the client last saw. Integer >= 0. Required.")]
    int? ExpectedVersion,
    [property: Description("Generation id, unique within the task, at most 100 characters. Example: g1. Required.")]
    string? GenerationId,
    [property: Description("Synthetic artifact reference of the form synthetic://<name>, at most 500 characters. Example: synthetic://artifacts/demo/g1. Required.")]
    string? ArtifactRef);

public sealed record RecordTestResultRequest(
    [property: Description("The task version the client last saw. Integer >= 0. Required.")]
    int? ExpectedVersion,
    [property: Description("The current generation id the result belongs to. Required.")]
    string? GenerationId,
    [property: Description("Test outcome: exactly 'passed' or 'failed'. Required.")]
    string? Outcome);

public sealed record CompleteTaskRequest(
    [property: Description("The task version the client last saw. Integer >= 0. Required.")]
    int? ExpectedVersion);

/// <summary>Every successful command returns exactly the task id and the new stream version.</summary>
public sealed record TaskCommandResponse(
    [property: Description("The task id.")] Guid TaskId,
    [property: Description("The stream version after the command; send it as expectedVersion on the next command.")]
    int Version);

/// <summary>
/// The current task state as committed in the <c>task_overview</c> projection (EVF-38). It mirrors the six projection
/// columns and is not a Domain <c>TaskState</c> rebuilt from the event history; <c>Version</c> is <c>last_stream_version</c>.
/// </summary>
public sealed record TaskStateResponse(
    [property: Description("The task id.")] Guid TaskId,
    [property: Description("created, assigned or completed.")] string Status,
    [property: Description("The assigned agent id, or null before assignment.")] string? AssignedAgentId,
    [property: Description("The current generation id, or null before the first generation.")] string? CurrentGenerationId,
    [property: Description("passed or failed for the current generation, or null while no result is recorded.")] string? CurrentTestOutcome,
    [property: Description("The stream version (number of events); send it as expectedVersion on the next command.")] int Version);

/// <summary>The ordered event history of a task.</summary>
public sealed record TaskHistoryResponse(
    [property: Description("The task id.")] Guid TaskId,
    [property: Description("The stream version (number of events).")] int Version,
    [property: Description("Events in ascending stream version order.")] IReadOnlyList<TaskEventResponse> Events);

/// <summary>One recorded event. Data is built from the validated Domain event, never from raw stored JSON.</summary>
public sealed record TaskEventResponse(
    [property: Description("The stored event id.")] Guid EventId,
    [property: Description("The 1-based position in the task stream.")] int StreamVersion,
    [property: Description("TaskCreated, TaskAssigned, CodeGenerated, TestsPassed, TestsFailed or TaskCompleted.")] string EventType,
    [property: Description("The stored schema version.")] int SchemaVersion,
    [property: Description("When the event was recorded (UTC).")] DateTime RecordedAt,
    [property: Description("Event facts: taskId; agentId; generationId and artifactRef; generationId; generationId; or empty.")]
    IReadOnlyDictionary<string, string> Data);
