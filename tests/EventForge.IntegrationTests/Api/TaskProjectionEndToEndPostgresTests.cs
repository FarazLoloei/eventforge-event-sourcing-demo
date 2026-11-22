using System.Net;
using System.Text;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// EVF-39: the whole Task 6 path in one place, through real HTTP and the production dependency injection against the
/// isolated, disposable PostgreSQL (skipped unless explicitly enabled, see <see cref="PostgresTestGuard"/>). After every
/// command it checks the response, the stored event, the committed <c>task_overview</c> row, the stream head, and both
/// query endpoints. Only <c>IEventStore</c> is wrapped (a counting pass-through to the real store) so the test can prove
/// that state reads never load the stream.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class TaskProjectionEndToEndPostgresTests(PostgresFixture fixture)
{
    /// <summary>Delegates to the real PostgreSQL store and counts calls. Adds no behaviour.</summary>
    private sealed class CountingEventStore : IEventStore
    {
        public IEventStore Inner { get; set; } = null!;

        public int LoadCalls { get; private set; }

        public int AppendCalls { get; private set; }

        public Task<LoadedStream> LoadStreamAsync(StreamKey stream, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Inner.LoadStreamAsync(stream, cancellationToken);
        }

        public Task<AppendResult> AppendAsync(
            StreamKey stream, int expectedVersion, IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
        {
            AppendCalls++;
            return Inner.AppendAsync(stream, expectedVersion, events, cancellationToken);
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, string Text, JsonElement Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, string? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(long Events, int Head, string? Overview, int? OverviewVersion)> StoredAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM eventforge.events WHERE stream_type = 'Task' AND stream_id = @id), " +
            "(SELECT COALESCE(max(stream_version), 0) FROM eventforge.events WHERE stream_type = 'Task' AND stream_id = @id), " +
            "(SELECT concat_ws('|', status, COALESCE(assigned_agent_id, '-'), COALESCE(current_generation_id, '-'), " +
            "COALESCE(latest_test_result, '-'), last_stream_version) FROM eventforge.task_overview WHERE task_id = @id), " +
            "(SELECT last_stream_version FROM eventforge.task_overview WHERE task_id = @id)",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }

    [PostgresFact]
    public async Task Every_command_commits_event_and_projection_together_and_both_queries_agree_with_them()
    {
        var counting = new CountingEventStore();
        using var api = new ApiFactory(fakeStore: false, connectionString: fixture.AppConnectionString);
        using var host = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventStore>();
            services.AddSingleton<IEventStore>(provider =>
            {
                counting.Inner = new EventForge.Infrastructure.Persistence.PostgreSql.PostgreSqlEventStore(
                    provider.GetRequiredService<EventForge.Infrastructure.Persistence.PostgreSql.PostgreSqlDatabase>());
                return counting;
            });
        }));
        var client = host.CreateClient();
        var id = Guid.NewGuid();

        (string Name, string Url, string Body, string EventType, string Overview,
            string Status, string? Agent, string? Generation, string? Outcome)[] steps =
        [
            ("create", "/api/tasks", $$"""{"taskId":"{{id}}"}""", "TaskCreated",
                "created|-|-|-|1", "created", null, null, null),
            ("assign", $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"agent-demo-1"}""", "TaskAssigned",
                "assigned|agent-demo-1|-|-|2", "assigned", "agent-demo-1", null, null),
            ("generate g1", $"/api/tasks/{id}/generations",
                """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""", "CodeGenerated",
                "assigned|agent-demo-1|g1|-|3", "assigned", "agent-demo-1", "g1", null),
            ("fail g1", $"/api/tasks/{id}/test-results",
                """{"expectedVersion":3,"generationId":"g1","outcome":"failed"}""", "TestsFailed",
                "assigned|agent-demo-1|g1|failed|4", "assigned", "agent-demo-1", "g1", "failed"),
            ("generate g2", $"/api/tasks/{id}/generations",
                """{"expectedVersion":4,"generationId":"g2","artifactRef":"synthetic://artifacts/demo/g2"}""", "CodeGenerated",
                "assigned|agent-demo-1|g2|-|5", "assigned", "agent-demo-1", "g2", null), // the failed outcome is cleared
            ("pass g2", $"/api/tasks/{id}/test-results",
                """{"expectedVersion":5,"generationId":"g2","outcome":"passed"}""", "TestsPassed",
                "assigned|agent-demo-1|g2|passed|6", "assigned", "agent-demo-1", "g2", "passed"),
            ("complete", $"/api/tasks/{id}/complete", """{"expectedVersion":6}""", "TaskCompleted",
                "completed|agent-demo-1|g2|passed|7", "completed", "agent-demo-1", "g2", "passed"),
        ];
        var expectedEventTypes = new List<string>();

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var version = i + 1;
            expectedEventTypes.Add(step.EventType);

            var command = await SendAsync(client, HttpMethod.Post, step.Url, step.Body);

            // 1. Command response contract.
            Assert.True(
                command.Status == (i == 0 ? HttpStatusCode.Created : HttpStatusCode.OK), $"{step.Name}: {command.Text}");
            Assert.Equal(["taskId", "version"], command.Body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(id, command.Body.GetProperty("taskId").GetGuid());
            Assert.Equal(version, command.Body.GetProperty("version").GetInt32());

            // 2. Committed storage: one new event, the projection row, and last_stream_version == the stream head.
            var stored = await StoredAsync(id);
            Assert.Equal(version, stored.Events);
            Assert.Equal(version, stored.Head);
            Assert.Equal(step.Overview, stored.Overview);
            Assert.Equal(stored.Head, stored.OverviewVersion);

            // 3. State query: the committed projection, exact six-field contract, without loading the stream.
            var loadsBeforeState = counting.LoadCalls;
            var state = await SendAsync(client, HttpMethod.Get, $"/api/tasks/{id}");
            Assert.Equal(loadsBeforeState, counting.LoadCalls);
            Assert.Equal(HttpStatusCode.OK, state.Status);
            Assert.Equal(
                ["assignedAgentId", "currentGenerationId", "currentTestOutcome", "status", "taskId", "version"],
                state.Body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(id, state.Body.GetProperty("taskId").GetGuid());
            Assert.Equal(step.Status, state.Body.GetProperty("status").GetString());
            Assert.Equal(step.Agent, state.Body.GetProperty("assignedAgentId").GetString());
            Assert.Equal(step.Generation, state.Body.GetProperty("currentGenerationId").GetString());
            Assert.Equal(step.Outcome, state.Body.GetProperty("currentTestOutcome").GetString());
            Assert.Equal(stored.Head, state.Body.GetProperty("version").GetInt32());

            // 4. History query: the authoritative stream, strictly read from the event store (it does load the stream).
            var loadsBeforeHistory = counting.LoadCalls;
            var history = await SendAsync(client, HttpMethod.Get, $"/api/tasks/{id}/events");
            Assert.Equal(loadsBeforeHistory + 1, counting.LoadCalls);
            Assert.Equal(HttpStatusCode.OK, history.Status);
            Assert.Equal(version, history.Body.GetProperty("version").GetInt32());
            Assert.Equal(
                expectedEventTypes,
                history.Body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventType").GetString()));
            Assert.Equal(
                Enumerable.Range(1, version),
                history.Body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("streamVersion").GetInt32()));

            if (i == 4)
            {
                // The second generation cleared the previous outcome in the response and in the stored row.
                Assert.Equal(JsonValueKind.Null, state.Body.GetProperty("currentTestOutcome").ValueKind);
                Assert.EndsWith("|g2|-|5", stored.Overview);
            }
        }

        // Exactly one append per command; every command loaded the stream once (the strict write path), and nothing else
        // did except the seven history reads: no state read ever loaded the stream.
        Assert.Equal(steps.Length, counting.AppendCalls);
        Assert.Equal(steps.Length /* commands */ + steps.Length /* history reads */, counting.LoadCalls);
    }

    [PostgresFact]
    public async Task The_api_never_runs_migrations_it_fails_safely_when_they_have_not_been_run()
    {
        var name = PostgresFixture.NewDatabaseName();
        try
        {
            await fixture.CreateProvisionedDatabaseAsync(name); // provisioned (roles, schema, grants) but no migration run
            using var api = new ApiFactory(fakeStore: false, connectionString: fixture.ConnectionStringFor("eventforge_app", name));
            var client = api.CreateClient();
            var id = Guid.NewGuid();

            var created = await SendAsync(client, HttpMethod.Post, "/api/tasks", $$"""{"taskId":"{{id}}"}""");
            var state = await SendAsync(client, HttpMethod.Get, $"/api/tasks/{id}");
            var history = await SendAsync(client, HttpMethod.Get, $"/api/tasks/{id}/events");

            foreach (var response in new[] { created, state, history })
            {
                Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
                Assert.Equal("internal_error", response.Body.GetProperty("code").GetString());
                Assert.DoesNotContain("relation", response.Text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("eventforge.", response.Text, StringComparison.OrdinalIgnoreCase);
            }

            // Starting the host and serving requests created no table: schema changes are an explicit migrator action.
            await using var admin = new NpgsqlConnection(fixture.AdminConnectionString(name));
            await admin.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = 'eventforge' AND c.relkind IN ('r', 'v', 'm', 'f', 'p')", admin);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.DropDatabaseAsync(name);
        }
    }
}
