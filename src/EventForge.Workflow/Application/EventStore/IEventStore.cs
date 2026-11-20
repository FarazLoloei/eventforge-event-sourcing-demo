namespace EventForge.Workflow.Application.EventStore;

/// <summary>
/// Append-only storage of ordered event streams with optimistic concurrency. Implemented by Infrastructure.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Loads a stream in ascending version order (an empty stream has version 0).
    /// Throws <see cref="EventStreamCorruptedException"/> when the stored versions are not 1..n.
    /// </summary>
    Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically appends <paramref name="events"/> if the stream is at <paramref name="expectedVersion"/>
    /// (0 for a new stream), assigning versions expectedVersion + 1 onward. A stale version or a lost race
    /// returns <see cref="AppendOutcome.VersionConflict"/>; the store never retries. Throws
    /// <see cref="EventStoreCommitUnknownException"/> when the commit outcome cannot be determined.
    /// </summary>
    Task<AppendResult> AppendAsync(
        StreamKey stream,
        int expectedVersion,
        IReadOnlyList<NewEvent> events,
        CancellationToken cancellationToken);
}
