using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// The outcome of handling one Task command. Expected failures are results; programmer errors, cancellation and
/// ordinary infrastructure failures propagate as exceptions. Failure results expose only safe codes, versions and
/// positions, never event payloads or identifiers; a successful result intentionally includes the emitted Domain events.
/// </summary>
public abstract record TaskCommandResult;

/// <summary>The events were committed. <see cref="NewVersion"/> is the stream version after the append.</summary>
public sealed record CommandSucceeded(int NewVersion, IReadOnlyList<TaskEvent> Events) : TaskCommandResult;

/// <summary>A domain rule rejected the command. Nothing was written.</summary>
public sealed record CommandRejected(RejectionCode Code) : TaskCommandResult;

/// <summary>
/// The stream is not at the expected version; nothing was written and nothing is retried.
/// <see cref="CurrentVersion"/> is <c>null</c> when a concurrent writer won the race: reload to learn it.
/// </summary>
public sealed record VersionConflict(int? CurrentVersion) : TaskCommandResult;

/// <summary>The store reported an event id that already exists; the append was rolled back and not retried.</summary>
public sealed record DuplicateEventId : TaskCommandResult;

/// <summary>
/// The stored history cannot be read as a valid schema v1 Task history. <see cref="Position"/> is the 1-based
/// stream position when known. Nothing was written.
/// </summary>
public sealed record InvalidHistory(InvalidHistoryReason Reason, int? Position) : TaskCommandResult;

/// <summary>The commit outcome is unknown. Reload the stream before deciding anything; nothing is retried.</summary>
public sealed record CommitUnknown : TaskCommandResult;

public enum InvalidHistoryReason
{
    StreamCorrupted,
    UnknownEventType,
    UnsupportedSchemaVersion,
    MalformedPayload,
    InvalidSequence,
}
