namespace EventForge.Workflow.Application.EventStore;

/// <summary>Stored history for a stream is not contiguous from version 1. Messages never include payloads.</summary>
public sealed class EventStreamCorruptedException(StreamKey stream, string reason)
    : Exception($"Event stream '{stream}' has invalid history: {reason}")
{
    public StreamKey Stream { get; } = stream;
}

/// <summary>
/// The connection failed or the operation was cancelled while committing, so the append may or may not have
/// been persisted. The caller must reload the stream before deciding what to do; the store does not retry.
/// </summary>
public sealed class EventStoreCommitUnknownException(StreamKey stream, Exception innerException)
    : Exception($"The commit outcome for event stream '{stream}' is unknown; reload the stream.", innerException)
{
    public StreamKey Stream { get; } = stream;
}
