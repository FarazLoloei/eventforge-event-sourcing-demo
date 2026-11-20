using System.Text.Json;

namespace EventForge.Workflow.Application.EventStore;

/// <summary>
/// An event the caller wants to append. The caller supplies the event id; the store assigns the stream version
/// and the recording time. Payload and metadata must be JSON objects.
/// </summary>
public sealed record NewEvent
{
    public NewEvent(Guid eventId, string eventType, int schemaVersion, JsonElement payload, JsonElement metadata)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("The event id must not be empty.", nameof(eventId));
        }

        EventStoreNames.Validate(eventType, nameof(eventType));

        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The schema version must be positive.");
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The payload must be a JSON object.", nameof(payload));
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The metadata must be a JSON object.", nameof(metadata));
        }

        EventId = eventId;
        EventType = eventType;
        SchemaVersion = schemaVersion;
        Payload = payload;
        Metadata = metadata;
    }

    public Guid EventId { get; }

    public string EventType { get; }

    public int SchemaVersion { get; }

    public JsonElement Payload { get; }

    public JsonElement Metadata { get; }
}
