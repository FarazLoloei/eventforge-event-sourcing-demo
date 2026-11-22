using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// The outcome of a Task query. Expected failures are results; programmer errors, cancellation and ordinary
/// infrastructure failures propagate as exceptions. Failure results carry only safe codes and positions.
/// </summary>
public abstract record TaskQueryResult;

/// <summary>The folded current state of an existing task.</summary>
public sealed record TaskStateFound(TaskState State) : TaskQueryResult;

/// <summary>
/// The validated history of an existing task in ascending stream order. <see cref="Version"/> is the number of events.
/// </summary>
public sealed record TaskHistoryFound(TaskId TaskId, int Version, IReadOnlyList<TaskHistoryEntry> Events) : TaskQueryResult;

/// <summary>One stored event: envelope facts exactly as recorded, plus the validated Domain event they map to.</summary>
public sealed record TaskHistoryEntry(
    Guid EventId,
    int StreamVersion,
    string EventType,
    int SchemaVersion,
    DateTimeOffset RecordedAt,
    TaskEvent Event);

/// <summary>The stream has no events.</summary>
public sealed record QueryTaskNotFound : TaskQueryResult;

/// <summary>
/// The stored history cannot be read as a valid schema v1 Task history. <see cref="Position"/> is the 1-based stream
/// position when known.
/// </summary>
public sealed record QueryInvalidHistory(InvalidHistoryReason Reason, int? Position) : TaskQueryResult;
