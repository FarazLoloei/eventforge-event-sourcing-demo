namespace EventForge.Workflow.Application.EventStore;

public enum AppendOutcome
{
    /// <summary>All events were committed.</summary>
    Appended,

    /// <summary>The stream was not at the expected version; nothing was appended.</summary>
    VersionConflict,

    /// <summary>An event id already exists in the store; nothing was appended.</summary>
    DuplicateEventId,
}

/// <summary>
/// The outcome of an append. Conflicts are results, not exceptions. <see cref="CurrentVersion"/> is the new
/// stream version after <see cref="AppendOutcome.Appended"/>, and the version observed inside the append
/// transaction for a stale expected version. It is <c>null</c> when a concurrent writer won the race
/// (the winner's version is not read back) and for duplicate event ids: reload the stream to learn it.
/// </summary>
public sealed record AppendResult
{
    private AppendResult(AppendOutcome outcome, int? currentVersion, IReadOnlyList<StoredEvent> events)
    {
        Outcome = outcome;
        CurrentVersion = currentVersion;
        Events = events;
    }

    public AppendOutcome Outcome { get; }

    public int? CurrentVersion { get; }

    /// <summary>The persisted envelopes (including the recording time) when appended; otherwise empty.</summary>
    public IReadOnlyList<StoredEvent> Events { get; }

    public bool IsAppended => Outcome == AppendOutcome.Appended;

    public static AppendResult Appended(int currentVersion, IReadOnlyList<StoredEvent> events) =>
        new(AppendOutcome.Appended, currentVersion, events);

    public static AppendResult VersionConflict(int? currentVersion) =>
        new(AppendOutcome.VersionConflict, currentVersion, []);

    public static AppendResult DuplicateEventId() =>
        new(AppendOutcome.DuplicateEventId, null, []);
}
