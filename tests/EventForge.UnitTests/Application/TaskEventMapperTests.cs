using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using static EventForge.UnitTests.Application.StoredEvents;
using static EventForge.UnitTests.Domain.TaskTestData;

namespace EventForge.UnitTests.Application;

public sealed class TaskEventMapperTests
{
    private static readonly Guid Id1 = T1.Value;

    public static IEnumerable<object[]> GoldenEvents() =>
    [
        [new TaskCreated(T1), "TaskCreated", """{"taskId":"11111111-1111-1111-1111-111111111111"}"""],
        [new TaskAssigned(Agent), "TaskAssigned", """{"agentId":"agent-1"}"""],
        [new CodeGenerated(G1, Art1), "CodeGenerated", """{"generationId":"g1","artifactRef":"artifact-1"}"""],
        [new TestsPassed(G1), "TestsPassed", """{"generationId":"g1"}"""],
        [new TestsFailed(G1), "TestsFailed", """{"generationId":"g1"}"""],
        [new TaskCompleted(), "TaskCompleted", "{}"],
    ];

    [Theory]
    [MemberData(nameof(GoldenEvents))]
    public void Events_are_written_with_the_stable_name_schema_1_and_exact_payload(
        TaskEvent @event,
        string eventType,
        string payload)
    {
        var eventId = Guid.NewGuid();
        var written = TaskEventMapper.ToNewEvent(@event, eventId, Json("""{"correlationId":"c-1"}"""));

        Assert.Equal(eventId, written.EventId);
        Assert.Equal(eventType, written.EventType);
        Assert.Equal(1, written.SchemaVersion);
        Assert.Equal(payload, written.Payload.GetRawText());
        Assert.Equal("""{"correlationId":"c-1"}""", written.Metadata.GetRawText());
    }

    [Theory]
    [MemberData(nameof(GoldenEvents))]
    public void Written_events_read_back_to_the_same_event(TaskEvent @event, string eventType, string payload)
    {
        _ = eventType;
        _ = payload;

        var stored = Of(1, @event);

        Assert.Equal(@event, TaskEventMapper.FromStored(stored));
    }

    [Theory]
    [MemberData(nameof(GoldenEvents))]
    public void Hand_written_golden_payloads_read_as_the_expected_event(TaskEvent @event, string eventType, string payload)
    {
        var stored = Raw(1, eventType, 1, payload);

        Assert.Equal(@event, TaskEventMapper.FromStored(stored));
    }

    private static void AssertFails(StoredEvent stored, InvalidHistoryReason reason)
    {
        var exception = Assert.Throws<TaskEventMappingException>(() => TaskEventMapper.FromStored(stored));

        Assert.Equal(reason, exception.Reason);
        Assert.Equal($"A stored Task event could not be mapped ({reason}).", exception.Message);
    }

    [Fact]
    public void An_unknown_event_type_is_rejected()
    {
        AssertFails(Raw(1, "TaskArchived", 1, "{}"), InvalidHistoryReason.UnknownEventType);
        AssertFails(Raw(1, "taskcreated", 1, "{}"), InvalidHistoryReason.UnknownEventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void An_unsupported_schema_version_is_rejected(int schemaVersion) =>
        AssertFails(Raw(1, "TaskCompleted", schemaVersion, "{}"), InvalidHistoryReason.UnsupportedSchemaVersion);

    [Fact]
    public void A_foreign_stream_type_is_rejected() =>
        AssertFails(Raw(1, "TaskCompleted", 1, "{}", streamType: "Other"), InvalidHistoryReason.InvalidSequence);

    [Theory]
    [InlineData("TaskCompleted", "[]")]
    [InlineData("TaskCompleted", "\"x\"")]
    [InlineData("TaskCompleted", "null")]
    [InlineData("TaskCompleted", """{"x":"y"}""")]
    [InlineData("TaskAssigned", "{}")]
    [InlineData("TaskAssigned", """{"agentId":"a","extra":"x"}""")]
    [InlineData("TaskAssigned", """{"AgentId":"a"}""")]
    [InlineData("TaskAssigned", """{"agentId":1}""")]
    [InlineData("TaskAssigned", """{"agentId":null}""")]
    [InlineData("TaskAssigned", """{"agentId":["a"]}""")]
    [InlineData("TaskAssigned", """{"agentId":""}""")]
    [InlineData("TaskAssigned", """{"agentId":"  "}""")]
    [InlineData("TaskAssigned", """{"agentId":"a\u0001b"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1"}""")]
    [InlineData("CodeGenerated", """{"artifactRef":"a"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","artifactRef":"a","x":"y"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","artifactRef":""}""")]
    [InlineData("TestsPassed", """{"generationId":2}""")]
    [InlineData("TestsFailed", """{"generation":"g1"}""")]
    [InlineData("TaskCreated", """{"taskId":"not-a-guid"}""")]
    [InlineData("TaskCreated", """{"taskId":"11111111111111111111111111111111"}""")]
    [InlineData("TaskCreated", """{"taskId":"11111111-1111-1111-1111-11111111111A"}""")]
    [InlineData("TaskCreated", """{"taskId":1}""")]
    // Property names are case-sensitive.
    [InlineData("TaskCreated", """{"TaskId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("CodeGenerated", """{"GenerationId":"g1","artifactRef":"a"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","ARTIFACTREF":"a"}""")]
    [InlineData("TestsPassed", """{"GENERATIONID":"g1"}""")]
    // Null values fail closed for every field.
    [InlineData("TaskCreated", """{"taskId":null}""")]
    [InlineData("CodeGenerated", """{"generationId":null,"artifactRef":"a"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","artifactRef":null}""")]
    [InlineData("TestsPassed", """{"generationId":null}""")]
    [InlineData("TestsFailed", """{"generationId":null}""")]
    [InlineData("TaskCompleted", """{"x":null}""")]
    // Duplicate properties visible to the mapper are rejected, even when the values are identical.
    [InlineData("TaskAssigned", """{"agentId":"a","agentId":"b"}""")]
    [InlineData("TaskAssigned", """{"agentId":"a","agentId":"a"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","generationId":"g2"}""")]
    [InlineData("CodeGenerated", """{"generationId":"g1","artifactRef":"a","artifactRef":"b"}""")]
    [InlineData("TestsPassed", """{"generationId":"g1","generationId":"g1"}""")]
    [InlineData("TaskCreated", """{"taskId":"11111111-1111-1111-1111-111111111111","taskId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("TaskCompleted", """{"a":"b","a":"b"}""")]
    // The task id must be a canonical lowercase "D" GUID: no braces, padding or other formats.
    [InlineData("TaskCreated", """{"taskId":"{11111111-1111-1111-1111-111111111111}"}""")]
    [InlineData("TaskCreated", """{"taskId":" 11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("TaskCreated", """{"taskId":"11111111-1111-1111-1111-111111111111 "}""")]
    [InlineData("TaskCreated", """{"taskId":""}""")]
    public void A_malformed_payload_is_rejected(string eventType, string payload) =>
        AssertFails(Raw(1, eventType, 1, payload), InvalidHistoryReason.MalformedPayload);

    [Fact]
    public void Duplicate_properties_are_visible_in_text_parsed_payloads()
    {
        // Guards the duplicate-property tests above: the parsed payload really contains both entries.
        var payload = Json("""{"agentId":"a","agentId":"b"}""");

        Assert.Equal(2, payload.EnumerateObject().Count());
    }

    [Fact]
    public void TaskCompleted_accepts_exactly_an_empty_object()
    {
        Assert.Equal(new TaskCompleted(), TaskEventMapper.FromStored(Raw(1, "TaskCompleted", 1, "{}")));
        Assert.Equal(new TaskCompleted(), TaskEventMapper.FromStored(Raw(1, "TaskCompleted", 1, "{ }")));
    }

    [Fact]
    public void An_empty_guid_task_id_is_not_the_stream_id()
    {
        AssertFails(
            Raw(1, "TaskCreated", 1, """{"taskId":"00000000-0000-0000-0000-000000000000"}"""),
            InvalidHistoryReason.InvalidSequence);
    }

    [Fact]
    public void Overlong_values_are_rejected()
    {
        AssertFails(
            Raw(1, "TaskAssigned", 1, $$"""{"agentId":"{{new string('a', 101)}}"}"""),
            InvalidHistoryReason.MalformedPayload);
        AssertFails(
            Raw(1, "CodeGenerated", 1, $$"""{"generationId":"g1","artifactRef":"{{new string('a', 501)}}"}"""),
            InvalidHistoryReason.MalformedPayload);
    }

    [Fact]
    public void A_created_event_for_a_different_task_is_an_invalid_sequence() =>
        AssertFails(
            Raw(1, "TaskCreated", 1, """{"taskId":"22222222-2222-2222-2222-222222222222"}"""),
            InvalidHistoryReason.InvalidSequence);

    [Fact]
    public void Rejections_name_only_the_reason_never_the_payload()
    {
        var exception = Assert.Throws<TaskEventMappingException>(
            () => TaskEventMapper.FromStored(Raw(1, "TaskAssigned", 1, """{"agentId":"SECRET-AGENT","extra":"SECRET"}""")));

        Assert.DoesNotContain("SECRET", exception.Message);
        Assert.DoesNotContain(Id1.ToString(), exception.Message);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => TaskEventMapper.FromStored(null!));
        Assert.Throws<ArgumentNullException>(() => TaskEventMapper.ToNewEvent(null!, Guid.NewGuid(), Json("{}")));
    }

    [Fact]
    public void An_empty_event_id_is_refused_by_the_envelope()
    {
        Assert.Throws<ArgumentException>(() => TaskEventMapper.ToNewEvent(new TaskCompleted(), Guid.Empty, Json("{}")));
    }
}
