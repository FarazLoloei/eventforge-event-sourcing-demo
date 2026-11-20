using System.Buffers;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>A stored event cannot be mapped to a schema v1 Task event. The message contains only the reason.</summary>
public sealed class TaskEventMappingException(InvalidHistoryReason reason)
    : Exception($"A stored Task event could not be mapped ({reason}).")
{
    public InvalidHistoryReason Reason { get; } = reason;
}

/// <summary>
/// Explicit, pure mapping between Domain events and Event Store envelopes for schema version 1.
/// Payload properties are camelCase and written in a fixed order; a task id is the canonical lowercase "D" GUID.
/// Reading is strict: unknown event types, other schema versions, missing or extra properties and non-string
/// values are rejected, because an additive change requires a new schema version. No upcasting exists yet.
/// </summary>
public static class TaskEventMapper
{
    public static NewEvent ToNewEvent(TaskEvent @event, Guid eventId, JsonElement metadata)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var (eventType, payload) = @event switch
        {
            TaskCreated e => (TaskEventContract.TaskCreated, Payload(("taskId", e.TaskId.Value.ToString()))),
            TaskAssigned e => (TaskEventContract.TaskAssigned, Payload(("agentId", e.AgentId.Value))),
            CodeGenerated e => (
                TaskEventContract.CodeGenerated,
                Payload(("generationId", e.GenerationId.Value), ("artifactRef", e.ArtifactRef.Value))),
            TestsPassed e => (TaskEventContract.TestsPassed, Payload(("generationId", e.GenerationId.Value))),
            TestsFailed e => (TaskEventContract.TestsFailed, Payload(("generationId", e.GenerationId.Value))),
            TaskCompleted => (TaskEventContract.TaskCompleted, Payload()),
            _ => throw new ArgumentException("The event type is not supported.", nameof(@event)),
        };

        return new NewEvent(eventId, eventType, TaskEventContract.CurrentSchemaVersion, payload, metadata);
    }

    /// <exception cref="TaskEventMappingException">The envelope is not a valid schema v1 Task event.</exception>
    public static TaskEvent FromStored(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.StreamType != TaskEventContract.StreamType)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.InvalidSequence);
        }

        if (!IsKnownEventType(stored.EventType))
        {
            throw new TaskEventMappingException(InvalidHistoryReason.UnknownEventType);
        }

        if (stored.SchemaVersion != TaskEventContract.CurrentSchemaVersion)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.UnsupportedSchemaVersion);
        }

        try
        {
            return stored.EventType switch
            {
                TaskEventContract.TaskCreated => ReadCreated(stored),
                TaskEventContract.TaskAssigned => new TaskAssigned(new AgentId(Read(stored.Payload, "agentId")[0])),
                TaskEventContract.CodeGenerated => ReadGenerated(stored.Payload),
                TaskEventContract.TestsPassed => new TestsPassed(new GenerationId(Read(stored.Payload, "generationId")[0])),
                TaskEventContract.TestsFailed => new TestsFailed(new GenerationId(Read(stored.Payload, "generationId")[0])),
                _ => ReadCompleted(stored.Payload),
            };
        }
        catch (ArgumentException)
        {
            // The Domain value types reject blank, overlong or control-character values.
            throw new TaskEventMappingException(InvalidHistoryReason.MalformedPayload);
        }
    }

    private static bool IsKnownEventType(string eventType) =>
        eventType is TaskEventContract.TaskCreated
            or TaskEventContract.TaskAssigned
            or TaskEventContract.CodeGenerated
            or TaskEventContract.TestsPassed
            or TaskEventContract.TestsFailed
            or TaskEventContract.TaskCompleted;

    private static TaskCreated ReadCreated(StoredEvent stored)
    {
        var text = Read(stored.Payload, "taskId")[0];
        if (!Guid.TryParseExact(text, "D", out var id) || id.ToString() != text)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.MalformedPayload);
        }

        if (id != stored.StreamId)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.InvalidSequence);
        }

        return new TaskCreated(new TaskId(id));
    }

    private static CodeGenerated ReadGenerated(JsonElement payload)
    {
        var values = Read(payload, "generationId", "artifactRef");
        return new CodeGenerated(new GenerationId(values[0]), new ArtifactRef(values[1]));
    }

    private static TaskCompleted ReadCompleted(JsonElement payload)
    {
        Read(payload);
        return new TaskCompleted();
    }

    /// <summary>Returns the string values of exactly the named properties; anything else is malformed.</summary>
    private static string[] Read(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.MalformedPayload);
        }

        var count = 0;
        foreach (var _ in payload.EnumerateObject())
        {
            count++;
        }

        if (count != names.Length)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.MalformedPayload);
        }

        var values = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            if (!payload.TryGetProperty(names[i], out var value) || value.ValueKind != JsonValueKind.String)
            {
                throw new TaskEventMappingException(InvalidHistoryReason.MalformedPayload);
            }

            values[i] = value.GetString()!;
        }

        return values;
    }

    private static JsonElement Payload(params (string Name, string Value)[] properties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
