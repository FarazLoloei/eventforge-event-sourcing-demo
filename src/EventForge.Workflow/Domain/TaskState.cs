using System.Collections.Immutable;

namespace EventForge.Workflow.Domain;

public enum WorkflowStatus
{
    NotCreated,
    Created,
    Assigned,
    Completed,
}

public enum TestOutcome
{
    Passed,
    Failed,
}

/// <summary>
/// Immutable Task aggregate state, folded from events. Contract only; the fold itself is not part of this type.
/// <see cref="CurrentTestOutcome"/> is absent until a result is recorded for <see cref="CurrentGeneration"/> and is
/// cleared whenever a new generation is applied. Earlier outcomes remain only in the immutable event history.
/// <see cref="Version"/> is the number of applied events. Note that <see cref="UsedGenerations"/> compares by
/// reference in record equality, so compare its contents explicitly.
/// </summary>
public sealed record TaskState
{
    public static TaskState Initial { get; } = new();

    public TaskId? Id { get; init; }

    public WorkflowStatus Status { get; init; } = WorkflowStatus.NotCreated;

    public AgentId? AssignedAgent { get; init; }

    /// <summary>Every generation id recorded for the task, including superseded ones.</summary>
    public ImmutableHashSet<GenerationId> UsedGenerations { get; init; } = ImmutableHashSet<GenerationId>.Empty;

    public GenerationId? CurrentGeneration { get; init; }

    public TestOutcome? CurrentTestOutcome { get; init; }

    public int Version { get; init; }
}
