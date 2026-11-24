using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Application.StoredEvents;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

public sealed class CodeGeneratedUpcasterTests
{
    private const string V1 = """{"generationId":"g2","artifactRef":"synthetic://artifacts/g2"}""";
    private const string V2 = """{"generationId":"g2","artifactReference":"synthetic://artifacts/g2"}""";

    private static StoredEvent Generated(int schemaVersion, string payload) =>
        Raw(3, "CodeGenerated", schemaVersion, payload);

    private static void AssertFails(StoredEvent stored, InvalidHistoryReason reason)
    {
        var exception = Assert.Throws<TaskEventMappingException>(() => CodeGeneratedUpcaster.Normalize(stored));
        Assert.Equal(reason, exception.Reason);
    }

    [Fact]
    public void A_valid_v1_event_is_converted_to_the_canonical_v2_payload()
    {
        var normalized = CodeGeneratedUpcaster.Normalize(Generated(1, V1));

        Assert.Equal(2, normalized.SchemaVersion);
        Assert.Equal(1, normalized.OriginalSchemaVersion);
        Assert.Equal(V2, normalized.Payload.GetRawText());
    }

    [Fact]
    public void A_valid_v2_event_is_validated_and_kept_without_conversion()
    {
        var stored = Generated(2, V2);

        var normalized = CodeGeneratedUpcaster.Normalize(stored);

        Assert.Equal(2, normalized.SchemaVersion);
        Assert.Equal(2, normalized.OriginalSchemaVersion);
        Assert.Equal(V2, normalized.Payload.GetRawText());
    }

    [Fact]
    public void V1_and_v2_normalize_to_the_same_payload_and_the_same_domain_event()
    {
        var fromV1 = CodeGeneratedUpcaster.Normalize(Generated(1, V1));
        var fromV2 = CodeGeneratedUpcaster.Normalize(Generated(2, V2));

        Assert.Equal(fromV1.Payload.GetRawText(), fromV2.Payload.GetRawText());
        var expected = new CodeGenerated(new GenerationId("g2"), new ArtifactRef("synthetic://artifacts/g2"));
        Assert.Equal(expected, CodeGeneratedUpcaster.ToDomainEvent(fromV1));
        Assert.Equal(expected, CodeGeneratedUpcaster.ToDomainEvent(fromV2));
    }

    [Fact]
    public void Conversion_is_deterministic()
    {
        var stored = Generated(1, V1);

        var first = CodeGeneratedUpcaster.Normalize(stored);
        var second = CodeGeneratedUpcaster.Normalize(stored);

        Assert.Equal(first.Payload.GetRawText(), second.Payload.GetRawText());
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
    }

    [Fact]
    public void The_stored_envelope_payload_and_metadata_are_not_changed_and_identity_is_preserved()
    {
        var stored = new StoredEvent(
            Guid.NewGuid(), "Task", T1.Value, 3, "CodeGenerated", 1, Json(V1), Json("""{"correlationId":"c-1"}"""),
            new DateTimeOffset(2026, 11, 22, 10, 2, 0, TimeSpan.Zero));
        var snapshot = stored with { };

        var normalized = CodeGeneratedUpcaster.Normalize(stored);

        Assert.Equal(snapshot, stored);
        Assert.Equal(V1, stored.Payload.GetRawText());
        Assert.Equal("""{"correlationId":"c-1"}""", stored.Metadata.GetRawText());
        Assert.Same(stored, normalized.Original);
        Assert.Equal(stored.EventId, normalized.EventId);
        Assert.Equal(3, normalized.StreamVersion);
        Assert.Equal(1, normalized.Original.SchemaVersion);
    }

    [Fact]
    public void Values_are_copied_exactly()
    {
        const string payload = """{"generationId":"g-ü 1","artifactRef":"synthetic://artifacts/Mixed.Case_1/x-y"}""";

        var domain = CodeGeneratedUpcaster.ToDomainEvent(CodeGeneratedUpcaster.Normalize(Generated(1, payload)));

        Assert.Equal("g-ü 1", domain.GenerationId.Value);
        Assert.Equal("synthetic://artifacts/Mixed.Case_1/x-y", domain.ArtifactRef.Value);
    }

    [Fact]
    public void Property_order_in_the_source_does_not_matter_and_output_order_is_fixed()
    {
        var normalized = CodeGeneratedUpcaster.Normalize(
            Generated(1, """{"artifactRef":"synthetic://artifacts/g2","generationId":"g2"}"""));

        Assert.Equal(V2, normalized.Payload.GetRawText());
    }

    public static IEnumerable<object[]> MalformedV1Payloads() =>
    [
        ["""{"generationId":"g2"}"""],                                                     // missing artifactRef
        ["""{"artifactRef":"a"}"""],                                                       // missing generationId
        ["{}"],
        ["""{"generationId":"g2","artifactRef":"a","extra":"x"}"""],                       // extra
        ["""{"generationId":"g2","artifactRef":"a","artifactReference":"a"}"""],           // both names
        ["""{"generationId":"g2","artifactReference":"a"}"""],                             // v2 name in v1
        ["""{"generationId":"g2","ArtifactRef":"a"}"""],                                   // wrong casing
        ["""{"GenerationId":"g2","artifactRef":"a"}"""],
        ["""{"generationId":"g2","artifactRef":"a","artifactRef":"b"}"""],                 // duplicate key
        ["""{"generationId":null,"artifactRef":"a"}"""],                                   // null
        ["""{"generationId":"g2","artifactRef":null}"""],
        ["""{"generationId":1,"artifactRef":"a"}"""],                                      // wrong types
        ["""{"generationId":"g2","artifactRef":true}"""],
        ["""{"generationId":"g2","artifactRef":{"v":"a"}}"""],
        ["""{"generationId":"g2","artifactRef":["a"]}"""],
        ["""{"generationId":"","artifactRef":"a"}"""],                                     // invalid Domain values
        ["""{"generationId":"   ","artifactRef":"a"}"""],
        ["""{"generationId":"g2","artifactRef":""}"""],
        ["""{"generationId":"g\u0001","artifactRef":"a"}"""],
        ["""{"generationId":"g2","artifactRef":"a\nb"}"""],
        [$$"""{"generationId":"{{new string('g', 101)}}","artifactRef":"a"}"""],
        [$$"""{"generationId":"g2","artifactRef":"{{new string('a', 501)}}"}"""],
        ["[]"],                                                                            // not an object
        ["\"text\""],
        ["null"],
    ];

    [Theory]
    [MemberData(nameof(MalformedV1Payloads))]
    public void Malformed_v1_payloads_are_rejected(string payload) =>
        AssertFails(Generated(1, payload), InvalidHistoryReason.MalformedPayload);

    [Theory]
    [InlineData("""{"generationId":"g2"}""")]
    [InlineData("""{"artifactReference":"a"}""")]
    [InlineData("""{"generationId":"g2","artifactReference":"a","extra":"x"}""")]
    [InlineData("""{"generationId":"g2","artifactRef":"a"}""")]                          // v1 name in v2
    [InlineData("""{"generationId":"g2","artifactRef":"a","artifactReference":"a"}""")]  // both names
    [InlineData("""{"generationId":"g2","ArtifactReference":"a"}""")]                    // wrong casing
    [InlineData("""{"generationId":"g2","artifactReference":null}""")]
    [InlineData("""{"generationId":"g2","artifactReference":5}""")]
    [InlineData("""{"generationId":"g2","artifactReference":""}""")]
    [InlineData("""{"generationId":"g2","artifactReference":"a\u0007"}""")]
    [InlineData("""{"generationId":"g2","artifactReference":"a","artifactReference":"b"}""")]
    [InlineData("[]")]
    public void Malformed_v2_payloads_are_rejected(string payload) =>
        AssertFails(Generated(2, payload), InvalidHistoryReason.MalformedPayload);

    [Fact]
    public void A_v1_event_with_the_v2_property_name_is_rejected() =>
        AssertFails(Generated(1, V2), InvalidHistoryReason.MalformedPayload);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Unsupported_schema_versions_are_rejected(int version) =>
        AssertFails(Generated(version, V2), InvalidHistoryReason.UnsupportedSchemaVersion);

    [Theory]
    [InlineData("TaskCreated")]
    [InlineData("TestsPassed")]
    [InlineData("SomethingElse")]
    public void Other_event_types_are_not_handled_by_this_upcaster(string eventType) =>
        AssertFails(Raw(3, eventType, 1, V1), InvalidHistoryReason.UnknownEventType);

    [Fact]
    public void A_failure_returns_no_result_and_leaves_the_input_unchanged()
    {
        var stored = Generated(1, """{"generationId":"g2"}""");
        var snapshot = stored with { };

        Assert.Throws<TaskEventMappingException>(() => CodeGeneratedUpcaster.Normalize(stored));

        Assert.Equal(snapshot, stored);
        Assert.Equal("""{"generationId":"g2"}""", stored.Payload.GetRawText());
    }

    [Fact]
    public void The_current_runtime_mapper_still_reads_v1_only()
    {
        // EVF-41 does not wire the upcaster in: runtime behavior stays schema v1 until EVF-42.
        var v1 = TaskEventMapper.FromStored(Generated(1, V1));
        Assert.IsType<CodeGenerated>(v1);

        var exception = Assert.Throws<TaskEventMappingException>(() => TaskEventMapper.FromStored(Generated(2, V2)));
        Assert.Equal(InvalidHistoryReason.UnsupportedSchemaVersion, exception.Reason);
    }
}
