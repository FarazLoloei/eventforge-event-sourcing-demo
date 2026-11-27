using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Domain;

namespace EventForge.IntegrationTests.SchemaCompatibility;

/// <summary>
/// EVF-43 fixtures: one synthetic seven-event lifecycle stored in four <c>CodeGenerated</c> encodings. Payloads are
/// hand-written JSON text and every expected value below is a literal; nothing here calls the production mapper or the
/// upcaster, so a defect in either cannot make its own test pass. Event ids, stream identity and positions, metadata and
/// recording times are identical in every variant; only the <c>CodeGenerated</c> schema version and field name differ.
/// </summary>
internal static class VersionedHistoryFixture
{
    public static readonly Guid DefaultTaskId = new("0b9f6f2e-6c1d-4f5e-9a57-3a1d2c4e8b10");

    public const string Agent = "agent-demo-1";
    public const string ArtifactG1 = "synthetic://artifacts/evf43/g1";
    public const string ArtifactG2 = "synthetic://artifacts/evf43/g2";
    public const string Metadata = """{"correlationId":"evf43-fixture"}""";

    public static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    public static Guid EventId(int version) => new($"{version:D8}-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    public static DateTimeOffset RecordedAt(int version) => Start.AddMinutes(version - 1);

    /// <summary>The four encodings: first and second CodeGenerated schema versions (A, B, C, D).</summary>
    public static IEnumerable<object[]> Encodings() =>
    [
        [1, 1], // A: v1-only
        [2, 2], // B: v2-only
        [1, 2], // C: mixed
        [2, 1], // D: reverse mixed
    ];

    /// <summary>The encodings crossed with each aggregate boundary after stream versions 3 to 7.</summary>
    public static IEnumerable<object[]> EncodingsAndBoundaries() =>
        Encodings().SelectMany(e => Enumerable.Range(1, 7).Select(version => new[] { e[0], e[1], version }));

    /// <summary>The hand-written payload text of a stored <c>CodeGenerated</c> event.</summary>
    public static string GeneratedPayload(string generation, string artifact, int schemaVersion) => schemaVersion switch
    {
        1 => $$"""{"generationId":"{{generation}}","artifactRef":"{{artifact}}"}""",
        2 => $$"""{"generationId":"{{generation}}","artifactReference":"{{artifact}}"}""",
        _ => throw new ArgumentOutOfRangeException(nameof(schemaVersion)),
    };

    /// <summary>(event type, schema version, payload text) for the seven positions of the lifecycle.</summary>
    public static (string Type, int SchemaVersion, string Payload)[] Lifecycle(Guid taskId, int firstVersion, int secondVersion) =>
    [
        ("TaskCreated", 1, $$"""{"taskId":"{{taskId}}"}"""),
        ("TaskAssigned", 1, $$"""{"agentId":"{{Agent}}"}"""),
        ("CodeGenerated", firstVersion, GeneratedPayload("g1", ArtifactG1, firstVersion)),
        ("TestsFailed", 1, """{"generationId":"g1"}"""),
        ("CodeGenerated", secondVersion, GeneratedPayload("g2", ArtifactG2, secondVersion)),
        ("TestsPassed", 1, """{"generationId":"g2"}"""),
        ("TaskCompleted", 1, "{}"),
    ];

    public static StoredEvent[] Build(int firstVersion, int secondVersion, Guid? taskId = null)
    {
        var id = taskId ?? DefaultTaskId;
        return Lifecycle(id, firstVersion, secondVersion)
            .Select((e, i) => new StoredEvent(
                EventId(i + 1), "Task", id, i + 1, e.Type, e.SchemaVersion,
                JsonDocument.Parse(e.Payload).RootElement.Clone(), JsonDocument.Parse(Metadata).RootElement.Clone(),
                RecordedAt(i + 1)))
            .ToArray();
    }

    /// <summary>Hand-authored aggregate expectation after <paramref name="Version"/> events.</summary>
    public sealed record ExpectedState(
        int Version,
        WorkflowStatus Status,
        string? Agent,
        string? CurrentGeneration,
        string[] UsedGenerations,
        TestOutcome? Result);

    public static ExpectedState Expected(int version) => version switch
    {
        1 => new(1, WorkflowStatus.Created, null, null, [], null),
        2 => new(2, WorkflowStatus.Assigned, Agent, null, [], null),
        3 => new(3, WorkflowStatus.Assigned, Agent, "g1", ["g1"], null),
        4 => new(4, WorkflowStatus.Assigned, Agent, "g1", ["g1"], TestOutcome.Failed),
        5 => new(5, WorkflowStatus.Assigned, Agent, "g2", ["g1", "g2"], null),
        6 => new(6, WorkflowStatus.Assigned, Agent, "g2", ["g1", "g2"], TestOutcome.Passed),
        7 => new(7, WorkflowStatus.Completed, Agent, "g2", ["g1", "g2"], TestOutcome.Passed),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    /// <summary>The artifact references the Domain events must carry after the given number of events.</summary>
    public static string?[] ExpectedArtifacts(int version) =>
        [version >= 3 ? ArtifactG1 : null, version >= 5 ? ArtifactG2 : null];
}
