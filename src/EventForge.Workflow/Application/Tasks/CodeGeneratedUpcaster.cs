using System.Buffers;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// The transient, in-memory form of a stored <c>CodeGenerated</c> event after normalization to the current contract
/// (schema v2). <see cref="Original"/> is the untouched stored envelope, so event id, stream identity and position,
/// metadata, recording time and the originally stored schema version all stay available. Only <see cref="Payload"/>
/// (the v2 shape) and <see cref="SchemaVersion"/> (the normalized version) describe the converted contract. Never
/// persisted and never returned over HTTP.
/// </summary>
public sealed record NormalizedCodeGenerated
{
    // Only CodeGeneratedUpcaster.Normalize creates instances, so a value of this type has always been validated.
    internal NormalizedCodeGenerated(StoredEvent original, int schemaVersion, JsonElement payload)
    {
        Original = original;
        SchemaVersion = schemaVersion;
        Payload = payload;
    }

    public StoredEvent Original { get; }

    public int SchemaVersion { get; }

    public JsonElement Payload { get; }

    public Guid EventId => Original.EventId;

    public int StreamVersion => Original.StreamVersion;

    public int OriginalSchemaVersion => Original.SchemaVersion;
}

/// <summary>
/// The one explicit upcast step for <c>CodeGenerated</c> (EVF-41): schema v1 <c>{generationId, artifactRef}</c> to
/// schema v2 <c>{generationId, artifactReference}</c>. A rename only; the values are copied exactly.
/// </summary>
/// <remarks>
/// Pure and deterministic: no I/O, clock, randomness or lookup, no mutation of the input, and identical input gives
/// identical output. Both the source and the target payload are validated strictly (exactly the two named string
/// properties, case-sensitive, values accepted by the Domain value types). A v2 event is validated directly and not
/// converted again. Any other version, and any other event type, fails closed with a
/// <see cref="TaskEventMappingException"/> using the existing safe reasons. This type is not yet used by the history
/// fold, the command and query handlers or the projector (EVF-42), and it does not change what is written.
/// </remarks>
public static class CodeGeneratedUpcaster
{
    public const int SourceSchemaVersion = 1;

    public const int TargetSchemaVersion = 2;

    private const string GenerationIdName = "generationId";
    private const string ArtifactRefName = "artifactRef";
    private const string ArtifactReferenceName = "artifactReference";

    /// <exception cref="TaskEventMappingException">The event is not a valid v1 or v2 <c>CodeGenerated</c>.</exception>
    public static NormalizedCodeGenerated Normalize(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (stored.EventType != TaskEventContract.CodeGenerated)
        {
            throw new TaskEventMappingException(InvalidHistoryReason.UnknownEventType);
        }

        switch (stored.SchemaVersion)
        {
            case SourceSchemaVersion:
                var (generationId, artifact) = Read(stored.Payload, ArtifactRefName);
                var converted = BuildV2(generationId, artifact);
                // Validate the target shape too, so a defect in this step can never produce an accepted event.
                Read(converted, ArtifactReferenceName);
                return new NormalizedCodeGenerated(stored, TargetSchemaVersion, converted);

            case TargetSchemaVersion:
                Read(stored.Payload, ArtifactReferenceName);
                return new NormalizedCodeGenerated(stored, TargetSchemaVersion, stored.Payload);

            default:
                throw new TaskEventMappingException(InvalidHistoryReason.UnsupportedSchemaVersion);
        }
    }

    /// <summary>Maps a normalized event to the unchanged Domain event.</summary>
    public static CodeGenerated ToDomainEvent(NormalizedCodeGenerated normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var (generationId, artifact) = Read(normalized.Payload, ArtifactReferenceName);
        return new CodeGenerated(new GenerationId(generationId), new ArtifactRef(artifact));
    }

    /// <summary>
    /// Reads exactly <c>generationId</c> and the named artifact property as strings accepted by the Domain value
    /// types; anything else is malformed. Property names are case-sensitive.
    /// </summary>
    private static (string GenerationId, string Artifact) Read(JsonElement payload, string artifactName)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw Malformed();
        }

        var count = 0;
        foreach (var _ in payload.EnumerateObject())
        {
            count++;
        }

        if (count != 2
            || !TryGetString(payload, GenerationIdName, out var generationId)
            || !TryGetString(payload, artifactName, out var artifact))
        {
            throw Malformed();
        }

        try
        {
            _ = new GenerationId(generationId);
            _ = new ArtifactRef(artifact);
        }
        catch (ArgumentException)
        {
            throw Malformed();
        }

        return (generationId, artifact);
    }

    private static bool TryGetString(JsonElement payload, string name, out string value)
    {
        if (payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static JsonElement BuildV2(string generationId, string artifact)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(GenerationIdName, generationId);
            writer.WriteString(ArtifactReferenceName, artifact);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static TaskEventMappingException Malformed() => new(InvalidHistoryReason.MalformedPayload);
}
