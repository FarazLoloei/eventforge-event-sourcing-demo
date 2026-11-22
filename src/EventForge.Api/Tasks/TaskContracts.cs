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
