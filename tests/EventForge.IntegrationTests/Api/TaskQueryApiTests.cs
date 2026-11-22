using System.Net;
using System.Text;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// EVF-34: the two Task query endpoints over a scripted in-memory event store. No database is contacted. These tests
/// cover the HTTP contract and fail-closed reads; the full HTTP + PostgreSQL workflow belongs to EVF-35.
/// </summary>
public sealed class TaskQueryApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static async Task<(HttpStatusCode Status, string Text, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>The seven-event happy history; event n has a fixed id and is recorded n-1 minutes after the start.</summary>
    private static StoredEvent[] Lifecycle(Guid id, int count, string metadata = "{}")
    {
        (string Type, string Payload)[] all =
        [
            ("TaskCreated", $$"""{"taskId":"{{id}}"}"""),
            ("TaskAssigned", """{"agentId":"agent-demo-1"}"""),
            ("CodeGenerated", """{"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}"""),
            ("TestsFailed", """{"generationId":"g1"}"""),
            ("CodeGenerated", """{"generationId":"g2","artifactRef":"synthetic://artifacts/demo/g2"}"""),
            ("TestsPassed", """{"generationId":"g2"}"""),
            ("TaskCompleted", "{}"),
        ];
        return all.Take(count)
            .Select((e, i) => InMemoryEventStore.Stored(
                id, i + 1, e.Type, 1, e.Payload, Start.AddMinutes(i), metadata, EventId(i + 1)))
            .ToArray();
    }

    private static Guid EventId(int version) => new($"{version:D8}-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    [Fact]
    public async Task A_missing_task_is_404_for_state_and_history_and_nothing_is_appended()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var state = await GetAsync(client, $"/api/tasks/{id}");
        var history = await GetAsync(client, $"/api/tasks/{id}/events");

        foreach (var response in new[] { state, history })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.Status);
            Assert.Equal("task_not_found", response.Body.GetProperty("code").GetString());
        }

        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Theory]
    [InlineData(1, "created", null, null, null, 1)]
    [InlineData(2, "assigned", "agent-demo-1", null, null, 2)]
    [InlineData(3, "assigned", "agent-demo-1", "g1", null, 3)]
    [InlineData(4, "assigned", "agent-demo-1", "g1", "failed", 4)]
    [InlineData(5, "assigned", "agent-demo-1", "g2", null, 5)] // a new generation reset the failed outcome
    [InlineData(6, "assigned", "agent-demo-1", "g2", "passed", 6)]
    [InlineData(7, "completed", "agent-demo-1", "g2", "passed", 7)]
    public async Task State_is_reconstructed_from_the_event_history(
        int events, string status, string? agent, string? generation, string? outcome, int version)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        factory.Store.Seed(id, Lifecycle(id, events));

        var (code, _, body) = await GetAsync(client, $"/api/tasks/{id}");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(
            ["assignedAgentId", "currentGenerationId", "currentTestOutcome", "status", "taskId", "version"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id, body.GetProperty("taskId").GetGuid());
        Assert.Equal(status, body.GetProperty("status").GetString());
        Assert.Equal(agent, body.GetProperty("assignedAgentId").GetString());
        Assert.Equal(generation, body.GetProperty("currentGenerationId").GetString());
        Assert.Equal(outcome, body.GetProperty("currentTestOutcome").GetString());
        Assert.Equal(version, body.GetProperty("version").GetInt32());
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task History_lists_all_six_event_types_in_stream_order_with_stored_ids_and_timestamps()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        factory.Store.Seed(id, Lifecycle(id, 7));

        var (code, _, body) = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(["events", "taskId", "version"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(7, body.GetProperty("version").GetInt32());
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(
            ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsFailed", "CodeGenerated", "TestsPassed", "TaskCompleted"],
            events.Select(e => e.GetProperty("eventType").GetString()));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], events.Select(e => e.GetProperty("streamVersion").GetInt32()));
        Assert.Equal(
            Enumerable.Range(1, 7).Select(EventId),
            events.Select(e => e.GetProperty("eventId").GetGuid()));
        Assert.All(events, e => Assert.Equal(1, e.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal("2026-11-22T10:00:00Z", events[0].GetProperty("recordedAt").GetString());
        Assert.Equal("2026-11-22T10:06:00Z", events[6].GetProperty("recordedAt").GetString());

        Assert.Equal($$"""{"taskId":"{{id}}"}""", events[0].GetProperty("data").GetRawText());
        Assert.Equal("""{"agentId":"agent-demo-1"}""", events[1].GetProperty("data").GetRawText());
        Assert.Equal("""{"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""", events[2].GetProperty("data").GetRawText());
        Assert.Equal("""{"generationId":"g1"}""", events[3].GetProperty("data").GetRawText());
        Assert.Equal("""{"generationId":"g2"}""", events[5].GetProperty("data").GetRawText());
        Assert.Equal("{}", events[6].GetProperty("data").GetRawText());
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task Raw_metadata_and_payloads_are_never_exposed()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        factory.Store.Seed(id, Lifecycle(id, 3, """{"correlationId":"secret-corr","note":"hunter2"}"""));

        var (_, text, body) = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.DoesNotContain("secret-corr", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("metadata", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            body.GetProperty("events").EnumerateArray(),
            e => Assert.Equal(
                ["data", "eventId", "eventType", "recordedAt", "schemaVersion", "streamVersion"],
                e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData("/api/tasks/not-a-guid")]
    [InlineData("/api/tasks/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/tasks/not-a-guid/events")]
    [InlineData("/api/tasks/00000000-0000-0000-0000-000000000000/events")]
    public async Task An_invalid_task_id_is_400_without_touching_the_store(string url)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var (code, _, body) = await GetAsync(client, url);

        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("id", out _));
        Assert.Equal(0, factory.Store.LoadCalls);
    }

    public static TheoryData<string, int?> UnreadableHistories() => new()
    {
        { "unknown", 2 },
        { "schema", 2 },
        { "payload", 2 },
        { "sequence", 1 },
        { "gap", 2 },
        { "stream", 2 },
        { "store", null },
    };

    [Theory]
    [MemberData(nameof(UnreadableHistories))]
    public async Task Unreadable_history_fails_safely_with_no_partial_state_or_events(string kind, int? position)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var created = InMemoryEventStore.Stored(id, 1, "TaskCreated", 1, $$"""{"taskId":"{{id}}"}""");
        const string secret = """{"agentId":"a","secret":"hunter2"}""";
        switch (kind)
        {
            case "unknown":
                factory.Store.Seed(id, created, InMemoryEventStore.Stored(id, 2, "TaskArchived", 1, secret));
                break;
            case "schema":
                factory.Store.Seed(id, created, InMemoryEventStore.Stored(id, 2, "TaskAssigned", 2, secret));
                break;
            case "payload":
                factory.Store.Seed(id, created, InMemoryEventStore.Stored(id, 2, "TaskAssigned", 1, secret));
                break;
            case "sequence":
                factory.Store.Seed(id, InMemoryEventStore.Stored(id, 1, "TaskCompleted", 1, "{}"));
                break;
            case "gap":
                factory.Store.Seed(id, created, InMemoryEventStore.Stored(id, 3, "TaskAssigned", 1, """{"agentId":"a"}"""));
                break;
            case "stream":
                factory.Store.Seed(id, created, InMemoryEventStore.Stored(Guid.NewGuid(), 2, "TaskAssigned", 1, """{"agentId":"a"}"""));
                break;
            default:
                factory.Store.LoadException = new EventStreamCorruptedException(new StreamKey("Task", id), "gap");
                break;
        }

        foreach (var url in new[] { $"/api/tasks/{id}", $"/api/tasks/{id}/events" })
        {
            var (code, text, body) = await GetAsync(client, url);

            Assert.Equal(HttpStatusCode.InternalServerError, code);
            Assert.Equal("invalid_event_history", body.GetProperty("code").GetString());
            Assert.DoesNotContain("hunter2", text);
            Assert.False(body.TryGetProperty("events", out _));
            Assert.False(body.TryGetProperty("assignedAgentId", out _));
            Assert.Equal(
                position,
                body.TryGetProperty("position", out var at) ? at.GetInt32() : null);
        }

        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task An_unexpected_infrastructure_exception_is_a_safe_500()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Store.LoadException = new InvalidOperationException("Host=db.internal;Password=hunter2");
        var id = Guid.NewGuid();

        foreach (var url in new[] { $"/api/tasks/{id}", $"/api/tasks/{id}/events" })
        {
            var (code, text, body) = await GetAsync(client, url);

            Assert.Equal(HttpStatusCode.InternalServerError, code);
            Assert.Equal("internal_error", body.GetProperty("code").GetString());
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain("db.internal", text);
        }
    }

    [Fact]
    public async Task A_task_written_through_the_command_api_can_be_read_back()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsync("/api/tasks", Json($$"""{"taskId":"{{id}}"}"""))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync($"/api/tasks/{id}/assign", Json("""{"expectedVersion":1,"agentId":"agent-demo-1"}"""))).StatusCode);
        var appendsBefore = factory.Store.AppendCalls;

        var (_, _, state) = await GetAsync(client, $"/api/tasks/{id}");
        var (_, _, history) = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.Equal("assigned", state.GetProperty("status").GetString());
        Assert.Equal(2, state.GetProperty("version").GetInt32());
        Assert.Equal(2, history.GetProperty("events").GetArrayLength());
        Assert.Equal(appendsBefore, factory.Store.AppendCalls);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
