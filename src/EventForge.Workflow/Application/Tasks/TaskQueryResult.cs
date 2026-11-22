using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// The outcome of a Task query. Expected failures are results; programmer errors, cancellation and ordinary
/// infrastructure failures propagate as exceptions. Failure results carry only safe codes and positions.
/// </summary>
public abstract record TaskQueryResult;

/// <summary>
/// The current state of an existing task, as committed in the <c>task_overview</c> projection (not a Domain
/// <see cref="TaskState"/>: the projection does not hold the aggregate's full state). <c>LastStreamVersion</c> is the
/// version to use as the expected version of the next command.
/// </summary>
public sealed record TaskStateFound(TaskOverviewRow State) : TaskQueryResult;

/// <summary>
/// The projection cannot answer a state query safely: <see cref="TaskOverviewFault.MissingRow"/> means Task events
/// exist but no row does (for example a legacy stream), <see cref="TaskOverviewFault.InvalidRow"/> means the row is not
/// a state any valid history can produce. Never reported as not found and never repaired by a read.
/// </summary>
public sealed record QueryProjectionUnavailable(TaskOverviewFault Fault) : TaskQueryResult;

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
