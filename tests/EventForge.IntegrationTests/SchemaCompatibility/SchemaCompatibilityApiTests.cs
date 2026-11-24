using System.Net;
using System.Text;
using System.Text.Json;
using EventForge.IntegrationTests.Api;
using EventForge.Workflow.Application.EventStore;
using static EventForge.IntegrationTests.SchemaCompatibility.VersionedHistoryFixture;

namespace EventForge.IntegrationTests.SchemaCompatibility;

/// <summary>
/// EVF-43: HTTP behavior over the four <c>CodeGenerated</c> encodings, using the in-memory API doubles (no database;
/// see <see cref="SchemaCompatibilityPostgresTests"/> for PostgreSQL). History reports the original stored schema
/// version and the sanitized <c>artifactRef</c>; state stays projection-backed; failures stay explicit and complete.
/// </summary>
public sealed class SchemaCompatibilityApiTests
{
    private static async Task<(HttpStatusCode Status, string Text, JsonElement Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, string? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Task<(HttpStatusCode Status, string Text, JsonElement Body)> GetAsync(HttpClient client, string url) =>
        SendAsync(client, HttpMethod.Get, url);

    private static Task<(HttpStatusCode Status, string Text, JsonElement Body)> PostAsync(HttpClient client, string url, string body) =>
        SendAsync(client, HttpMethod.Post, url, body);

    private static ApiFactory Seeded(Guid id, params StoredEvent[] events)
    {
        var factory = new ApiFactory();
        factory.Store.Seed(id, events);
        return factory;
    }

    // ---- Reads -------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public async Task History_reports_original_schema_versions_sanitized_artifactRef_and_final_version_seven(int first, int second)
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, Build(first, second, id));
        var client = factory.CreateClient();

        var (status, text, body) = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(7, body.GetProperty("version").GetInt32());
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], events.Select(e => e.GetProperty("streamVersion").GetInt32()));
        Assert.Equal(Enumerable.Range(1, 7).Select(EventId), events.Select(e => e.GetProperty("eventId").GetGuid()));
        Assert.Equal(
            ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsFailed", "CodeGenerated", "TestsPassed", "TaskCompleted"],
            events.Select(e => e.GetProperty("eventType").GetString()));

        // The ORIGINAL stored version is reported; only the two CodeGenerated events vary.
        Assert.Equal([1, 1, first, 1, second, 1, 1], events.Select(e => e.GetProperty("schemaVersion").GetInt32()));

        // The sanitized contract keeps artifactRef whatever the stored field name.
        Assert.Equal($$"""{"generationId":"g1","artifactRef":"{{ArtifactG1}}"}""", events[2].GetProperty("data").GetRawText());
        Assert.Equal($$"""{"generationId":"g2","artifactRef":"{{ArtifactG2}}"}""", events[4].GetProperty("data").GetRawText());

        // Nothing stored leaks: neither the v2 field name nor raw metadata.
        Assert.DoesNotContain("artifactReference", text);
        Assert.DoesNotContain("correlationId", text);
        Assert.DoesNotContain("evf43-fixture", text);
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Theory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public async Task State_is_projection_backed_and_identical_for_every_encoding(int first, int second)
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, Build(first, second, id));
        var client = factory.CreateClient();

        var (status, _, body) = await GetAsync(client, $"/api/tasks/{id}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, body.GetProperty("taskId").GetGuid());
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(Agent, body.GetProperty("assignedAgentId").GetString());
        Assert.Equal("g2", body.GetProperty("currentGenerationId").GetString());
        Assert.Equal("passed", body.GetProperty("currentTestOutcome").GetString());
        Assert.Equal(7, body.GetProperty("version").GetInt32());
        Assert.Equal(0, factory.Store.LoadCalls); // the stream was not loaded to answer a state read
        Assert.Equal(1, factory.Overview.Calls);
    }

    // ---- Commands continue a historical stream ------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_historical_stream_continues_and_new_CodeGenerated_is_written_as_v2_while_other_events_stay_v1(int firstVersion)
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, Build(firstVersion, firstVersion, id).Take(4).ToArray());
        var client = factory.CreateClient();

        var generated = await PostAsync(client, $"/api/tasks/{id}/generations",
            $$"""{"expectedVersion":4,"generationId":"g2","artifactRef":"{{ArtifactG2}}"}""");
        Assert.Equal(HttpStatusCode.OK, generated.Status);
        Assert.Equal(5, generated.Body.GetProperty("version").GetInt32());
        var written = Assert.Single(factory.Store.LastAppended);
        Assert.Equal(("CodeGenerated", 2), (written.EventType, written.SchemaVersion));
        Assert.Equal($$"""{"generationId":"g2","artifactReference":"{{ArtifactG2}}"}""", written.Payload.GetRawText());

        var passed = await PostAsync(client, $"/api/tasks/{id}/test-results", """{"expectedVersion":5,"generationId":"g2","outcome":"passed"}""");
        Assert.Equal(HttpStatusCode.OK, passed.Status);
        Assert.Equal(("TestsPassed", 1), (factory.Store.LastAppended[0].EventType, factory.Store.LastAppended[0].SchemaVersion));

        var completed = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":6}""");
        Assert.Equal(HttpStatusCode.OK, completed.Status);
        Assert.Equal(("TaskCompleted", 1), (factory.Store.LastAppended[0].EventType, factory.Store.LastAppended[0].SchemaVersion));

        var history = await GetAsync(client, $"/api/tasks/{id}/events");
        var events = history.Body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal([1, 1, firstVersion, 1, 2, 1, 1], events.Select(e => e.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal(7, history.Body.GetProperty("version").GetInt32());
        Assert.Equal($$"""{"generationId":"g2","artifactRef":"{{ArtifactG2}}"}""", events[4].GetProperty("data").GetRawText());
        Assert.DoesNotContain("artifactReference", history.Text);

        var state = await GetAsync(client, $"/api/tasks/{id}");
        Assert.Equal("completed", state.Body.GetProperty("status").GetString());
        Assert.Equal(7, state.Body.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_rejected_or_stale_command_appends_nothing_on_a_versioned_history(int version)
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, Build(version, version, id).Take(4).ToArray());
        var client = factory.CreateClient();

        var rejected = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":4}""");
        Assert.Equal(HttpStatusCode.Conflict, rejected.Status);
        Assert.Equal("workflow_rule_violation", rejected.Body.GetProperty("code").GetString());

        var stale = await PostAsync(client, $"/api/tasks/{id}/generations",
            $$"""{"expectedVersion":3,"generationId":"g2","artifactRef":"{{ArtifactG2}}"}""");
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("version_conflict", stale.Body.GetProperty("code").GetString());
        Assert.Equal(4, stale.Body.GetProperty("currentVersion").GetInt32());

        var reused = await PostAsync(client, $"/api/tasks/{id}/generations",
            $$"""{"expectedVersion":4,"generationId":"g1","artifactRef":"{{ArtifactG2}}"}""");
        Assert.Equal(HttpStatusCode.Conflict, reused.Status); // a used generation id stays invalid across versions

        Assert.Equal(0, factory.Store.AppendCalls);
        Assert.Equal(4, factory.Store.EventsOf(id).Count);
    }

    // ---- Strict failure behavior over HTTP: explicit, positional, never partial ---------------------------------------

    private static StoredEvent[] WithEventAt(int position, Func<StoredEvent, StoredEvent> change, Guid id)
    {
        var events = Build(1, 2, id);
        events[position - 1] = change(events[position - 1]);
        return events;
    }

    private static StoredEvent Replace(StoredEvent e, string? type = null, int? schemaVersion = null, string? payload = null) => e with
    {
        EventType = type ?? e.EventType,
        SchemaVersion = schemaVersion ?? e.SchemaVersion,
        Payload = payload is null ? e.Payload : JsonDocument.Parse(payload).RootElement.Clone(),
    };

    public static IEnumerable<object[]> InvalidHistories() =>
    [
        // position, event type, schema version, payload (null keeps the fixture payload), expected reason
        [3, "CodeGenerated", 0, null!, "UnsupportedSchemaVersion"],
        [3, "CodeGenerated", -1, null!, "UnsupportedSchemaVersion"],
        [3, "CodeGenerated", 3, null!, "UnsupportedSchemaVersion"],
        [2, "TaskAssigned", 2, null!, "UnsupportedSchemaVersion"],
        [4, "TestsFailed", 2, null!, "UnsupportedSchemaVersion"],
        [7, "TaskCompleted", 2, null!, "UnsupportedSchemaVersion"],
        [4, "TestsRetried", 1, "{}", "UnknownEventType"],
        [3, "CodeGenerated", 1, """{"generationId":"g1","ArtifactRef":"synthetic://x"}""", "MalformedPayload"],
        [5, "CodeGenerated", 2, """{"generationId":"g2","artifactRef":"synthetic://x"}""", "MalformedPayload"],
        [5, "CodeGenerated", 2, """{"generationId":"g2"}""", "MalformedPayload"],
        [3, "CodeGenerated", 1, """{"generationId":"g1","artifactRef":"synthetic://x","extra":"e"}""", "MalformedPayload"],
        [5, "CodeGenerated", 2, """{"generationId":"g2","artifactReference":null}""", "MalformedPayload"],
        [5, "CodeGenerated", 2, """{"generationId":2,"artifactReference":"synthetic://x"}""", "MalformedPayload"],
        [3, "CodeGenerated", 1, """{"generationId":"","artifactRef":"synthetic://x"}""", "MalformedPayload"],
        [5, "CodeGenerated", 2, """{"generationId":"g2","artifactReference":"a\u0001"}""", "MalformedPayload"],
    ];

    [Theory]
    [MemberData(nameof(InvalidHistories))]
    public async Task An_invalid_stored_event_fails_the_whole_history_at_its_position_and_returns_no_events(
        int position, string type, int schemaVersion, string? payload, string reason)
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, WithEventAt(position, e => Replace(e, type, schemaVersion, payload), id));
        var client = factory.CreateClient();

        var (status, text, body) = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("invalid_event_history", body.GetProperty("code").GetString());
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.Equal(position, body.GetProperty("position").GetInt32());
        Assert.False(body.TryGetProperty("events", out _));
        Assert.DoesNotContain("artifactRef", text);
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task A_command_on_an_unsupported_history_is_refused_and_appends_nothing()
    {
        var id = Guid.NewGuid();
        using var factory = Seeded(id, WithEventAt(3, e => Replace(e, schemaVersion: 3), id).Take(4).ToArray());
        var client = factory.CreateClient();

        var result = await PostAsync(client, $"/api/tasks/{id}/test-results", """{"expectedVersion":4,"generationId":"g1","outcome":"passed"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, result.Status);
        Assert.Equal("invalid_event_history", result.Body.GetProperty("code").GetString());
        Assert.Equal("UnsupportedSchemaVersion", result.Body.GetProperty("reason").GetString());
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task A_version_gap_and_a_foreign_stream_identity_are_reported_explicitly()
    {
        var gapId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        using var factory = new ApiFactory();
        factory.Store.Seed(gapId, WithEventAt(4, e => e with { StreamVersion = 5 }, gapId));
        factory.Store.Seed(foreignId, WithEventAt(4, e => e with { StreamId = Guid.NewGuid() }, foreignId));
        var client = factory.CreateClient();

        var gap = await GetAsync(client, $"/api/tasks/{gapId}/events");
        var foreign = await GetAsync(client, $"/api/tasks/{foreignId}/events");

        Assert.Equal(("StreamCorrupted", 4), (gap.Body.GetProperty("reason").GetString(), gap.Body.GetProperty("position").GetInt32()));
        Assert.Equal(("InvalidSequence", 4), (foreign.Body.GetProperty("reason").GetString(), foreign.Body.GetProperty("position").GetInt32()));
        foreach (var response in new[] { gap, foreign })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
            Assert.Equal("invalid_event_history", response.Body.GetProperty("code").GetString());
            Assert.False(response.Body.TryGetProperty("events", out _));
        }
    }
}
