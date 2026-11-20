using System.Text.Json;

namespace EventForge.Workflow.Application.EventStore;

/// <summary>A persisted event envelope exactly as recorded by the store.</summary>
public sealed record StoredEvent(
    Guid EventId,
    string StreamType,
    Guid StreamId,
    int StreamVersion,
    string EventType,
    int SchemaVersion,
    JsonElement Payload,
    JsonElement Metadata,
    DateTimeOffset RecordedAt);
