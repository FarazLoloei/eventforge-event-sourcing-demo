using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

/// <summary>Builders for stored envelopes in the unit tests.</summary>
internal static class StoredEvents
{
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A hand-written envelope; the payload text is exactly what the test passes.</summary>
    public static StoredEvent Raw(
        int version,
        string eventType,
        int schemaVersion,
        string payload,
        Guid? streamId = null,
        string streamType = "Task") =>
        new(Guid.NewGuid(), streamType, streamId ?? T1.Value, version, eventType, schemaVersion, Json(payload), Json("{}"),
            DateTimeOffset.UnixEpoch);

    /// <summary>The envelope the mapper produces for a Domain event at the given stream position.</summary>
    public static StoredEvent Of(int version, TaskEvent @event)
    {
        var newEvent = TaskEventMapper.ToNewEvent(@event, Guid.NewGuid(), Json("{}"));
        return new StoredEvent(
            newEvent.EventId, "Task", T1.Value, version, newEvent.EventType, newEvent.SchemaVersion,
            newEvent.Payload, newEvent.Metadata, DateTimeOffset.UnixEpoch);
    }

    public static StoredEvent[] History(params TaskEvent[] events) =>
        events.Select((e, i) => Of(i + 1, e)).ToArray();
}
