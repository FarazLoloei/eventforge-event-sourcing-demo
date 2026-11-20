namespace EventForge.Workflow.Application.EventStore;

/// <summary>Identifies one event stream (for example stream type "Task" and a task identifier).</summary>
public sealed record StreamKey
{
    public StreamKey(string streamType, Guid streamId)
    {
        EventStoreNames.Validate(streamType, nameof(streamType));
        if (streamId == Guid.Empty)
        {
            throw new ArgumentException("The stream id must not be empty.", nameof(streamId));
        }

        StreamType = streamType;
        StreamId = streamId;
    }

    public string StreamType { get; }

    public Guid StreamId { get; }

    public override string ToString() => $"{StreamType}/{StreamId}";
}
