using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.IntegrationTests.SchemaCompatibility.VersionedHistoryFixture;

namespace EventForge.IntegrationTests.SchemaCompatibility;

/// <summary>
/// EVF-43: historical <c>CodeGenerated</c> v1 and current v2 histories fold to the same, independently specified
/// aggregate state and <c>task_overview</c> row at every boundary. Runs without a database. Expected values come from
/// <see cref="VersionedHistoryFixture"/> literals, never from the mapper or upcaster under test.
/// </summary>
public sealed class SchemaCompatibilityDomainTests
{
    private static List<TaskEvent> Map(int first, int second, int count) =>
        Build(first, second).Take(count).Select(TaskEventMapper.FromStored).ToList();

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.EncodingsAndBoundaries), MemberType = typeof(VersionedHistoryFixture))]
    public void Aggregate_state_matches_the_independent_expectation_for_every_encoding_and_boundary(
        int first, int second, int version)
    {
        var expected = Expected(version);

        var state = TaskAggregate.Rehydrate(Map(first, second, version));

        Assert.Equal(expected.Version, state.Version);
        Assert.Equal(expected.Status, state.Status);
        Assert.Equal(expected.Agent, state.AssignedAgent?.Value);
        Assert.Equal(expected.CurrentGeneration, state.CurrentGeneration?.Value);
        Assert.Equal(expected.Result, state.CurrentTestOutcome);
        Assert.Equal(expected.UsedGenerations.Order(StringComparer.Ordinal), state.UsedGenerations.Select(g => g.Value).Order(StringComparer.Ordinal));
        Assert.Equal(new TaskId(DefaultTaskId), state.Id);
    }

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public void Each_CodeGenerated_carries_the_exact_artifact_reference_whatever_the_stored_version(int first, int second)
    {
        var events = Map(first, second, 7);

        var generated = events.OfType<CodeGenerated>().ToList();
        Assert.Equal(2, generated.Count);
        Assert.Equal(("g1", ArtifactG1), (generated[0].GenerationId.Value, generated[0].ArtifactRef.Value));
        Assert.Equal(("g2", ArtifactG2), (generated[1].GenerationId.Value, generated[1].ArtifactRef.Value));
    }

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.EncodingsAndBoundaries), MemberType = typeof(VersionedHistoryFixture))]
    public void All_six_task_overview_fields_match_the_independent_expectation(int first, int second, int version)
    {
        var expected = Expected(version);

        var row = TaskOverviewProjector.ReduceBatch(null, Map(first, second, version), 1);

        Assert.Equal(DefaultTaskId, row.TaskId);
        Assert.Equal(expected.Status, row.Status);
        Assert.Equal(expected.Agent, row.AssignedAgentId);
        Assert.Equal(expected.CurrentGeneration, row.CurrentGenerationId);
        Assert.Equal(expected.Result, row.LatestTestResult);
        Assert.Equal(expected.Version, row.LastStreamVersion);
        Assert.True(TaskOverviewProjector.IsPlausible(row));
    }

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public void Event_by_event_projection_ends_at_version_seven_with_the_same_row_as_the_batch(int first, int second)
    {
        var events = Map(first, second, 7);
        TaskOverviewRow? row = null;
        for (var i = 0; i < events.Count; i++)
        {
            row = TaskOverviewProjector.Reduce(row, events[i], i + 1);
            Assert.Equal(Expected(i + 1).Version, row.LastStreamVersion);
            Assert.Equal(Expected(i + 1).CurrentGeneration, row.CurrentGenerationId);
            Assert.Equal(Expected(i + 1).Result, row.LatestTestResult);
        }

        Assert.Equal(7, row!.LastStreamVersion);
        Assert.Equal(TaskOverviewProjector.ReduceBatch(null, events, 1), row);
    }

    [Fact]
    public void All_four_encodings_fold_to_identical_domain_events()
    {
        var baseline = Map(1, 1, 7);

        foreach (var (first, second) in new[] { (2, 2), (1, 2), (2, 1) })
        {
            Assert.Equal(baseline, Map(first, second, 7));
        }
    }

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public void Fixtures_share_identity_metadata_and_timestamps_and_only_CodeGenerated_differs(int first, int second)
    {
        var stored = Build(first, second);
        var baseline = Build(1, 1);

        for (var i = 0; i < stored.Length; i++)
        {
            Assert.Equal(baseline[i].EventId, stored[i].EventId);
            Assert.Equal(baseline[i].StreamId, stored[i].StreamId);
            Assert.Equal(baseline[i].StreamVersion, stored[i].StreamVersion);
            Assert.Equal(baseline[i].Metadata.GetRawText(), stored[i].Metadata.GetRawText());
            Assert.Equal(baseline[i].RecordedAt, stored[i].RecordedAt);
            Assert.Equal(baseline[i].EventType, stored[i].EventType);
        }

        Assert.Equal([1, 1, first, 1, second, 1, 1], stored.Select(e => e.SchemaVersion));
        Assert.All(
            stored.Where(e => e.EventType != "CodeGenerated"),
            e => Assert.Equal(1, e.SchemaVersion));
    }

    // ---- Strict failure behavior at the mapper boundary --------------------------------------------------------------

    private static void AssertRejected(string eventType, int schemaVersion, string payload, InvalidHistoryReason reason)
    {
        var stored = Build(1, 1)[0] with
        {
            StreamVersion = 3,
            EventType = eventType,
            SchemaVersion = schemaVersion,
            Payload = System.Text.Json.JsonDocument.Parse(payload).RootElement.Clone(),
        };

        var exception = Assert.Throws<TaskEventMappingException>(() => TaskEventMapper.FromStored(stored));
        Assert.Equal(reason, exception.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Unsupported_CodeGenerated_versions_are_rejected(int version) =>
        AssertRejected("CodeGenerated", version, GeneratedPayload("g1", ArtifactG1, 2), InvalidHistoryReason.UnsupportedSchemaVersion);

    [Theory]
    [InlineData("TaskCreated", """{"taskId":"0b9f6f2e-6c1d-4f5e-9a57-3a1d2c4e8b10"}""")]
    [InlineData("TaskAssigned", """{"agentId":"agent-demo-1"}""")]
    [InlineData("TestsPassed", """{"generationId":"g1"}""")]
    [InlineData("TestsFailed", """{"generationId":"g1"}""")]
    [InlineData("TaskCompleted", "{}")]
    public void Schema_v2_is_rejected_for_each_of_the_other_five_event_types(string eventType, string payload) =>
        AssertRejected(eventType, 2, payload, InvalidHistoryReason.UnsupportedSchemaVersion);

    [Fact]
    public void An_unknown_event_type_is_rejected_at_any_schema_version()
    {
        AssertRejected("CodeRegenerated", 1, "{}", InvalidHistoryReason.UnknownEventType);
        AssertRejected("CodeRegenerated", 2, "{}", InvalidHistoryReason.UnknownEventType);
    }

    public static IEnumerable<object[]> MalformedGenerated() =>
    [
        // wrong case
        [1, """{"generationId":"g1","ArtifactRef":"synthetic://x"}"""],
        [2, """{"generationId":"g1","ArtifactReference":"synthetic://x"}"""],
        [2, """{"GenerationId":"g1","artifactReference":"synthetic://x"}"""],
        // missing
        [1, """{"generationId":"g1"}"""],
        [2, """{"generationId":"g1"}"""],
        [2, """{"artifactReference":"synthetic://x"}"""],
        // extra, and the other version's field name
        [1, """{"generationId":"g1","artifactRef":"synthetic://x","note":"n"}"""],
        [2, """{"generationId":"g1","artifactReference":"synthetic://x","note":"n"}"""],
        [2, """{"generationId":"g1","artifactRef":"synthetic://x"}"""],
        [1, """{"generationId":"g1","artifactReference":"synthetic://x"}"""],
        [2, """{"generationId":"g1","artifactRef":"synthetic://x","artifactReference":"synthetic://x"}"""],
        // null and incorrect JSON types
        [1, """{"generationId":null,"artifactRef":"synthetic://x"}"""],
        [2, """{"generationId":"g1","artifactReference":null}"""],
        [2, """{"generationId":"g1","artifactReference":1}"""],
        [2, """{"generationId":"g1","artifactReference":["synthetic://x"]}"""],
        [1, """{"generationId":true,"artifactRef":"synthetic://x"}"""],
        [2, """["generationId"]"""],
        // invalid Domain values
        [1, """{"generationId":"","artifactRef":"synthetic://x"}"""],
        [2, """{"generationId":"g1","artifactReference":"   "}"""],
        [2, """{"generationId":"g1","artifactReference":"a\u0001b"}"""],
    ];

    [Theory]
    [MemberData(nameof(MalformedGenerated))]
    public void Malformed_CodeGenerated_payloads_are_rejected_for_v1_and_v2(int version, string payload) =>
        AssertRejected("CodeGenerated", version, payload, InvalidHistoryReason.MalformedPayload);

    [Fact]
    public void A_missing_future_upcast_step_is_an_unsupported_version_not_a_silent_default()
    {
        // No v3 contract and no v2-to-v3 step exist; a reader meeting version 3 must fail rather than guess.
        AssertRejected("CodeGenerated", 3, GeneratedPayload("g1", ArtifactG1, 2), InvalidHistoryReason.UnsupportedSchemaVersion);
    }
}
