using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// EVF-35: the real HTTP -> Application -> PostgreSQL Event Store boundary. The API is hosted in memory with the real
/// <c>PostgreSqlEventStore</c> against the isolated, disposable PostgreSQL (skipped unless explicitly enabled, see
/// <see cref="PostgresTestGuard"/>). Rows are read back with the administrative connection to prove what was stored.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class TaskWorkflowHttpPostgresTests(PostgresFixture fixture)
{
    private sealed record Row(Guid EventId, int Version, string EventType, int SchemaVersion, DateTimeOffset RecordedAt, string Metadata);

    private ApiFactory NewApi() => new(fakeStore: false, connectionString: fixture.AppConnectionString);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, string Text, JsonElement Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, string? body = null, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Task<(HttpStatusCode Status, string Text, JsonElement Body)> PostAsync(
        HttpClient client, string url, string body, string? correlationId = null) =>
        SendAsync(client, HttpMethod.Post, url, body, correlationId);

    private static Task<(HttpStatusCode Status, string Text, JsonElement Body)> GetAsync(HttpClient client, string url) =>
        SendAsync(client, HttpMethod.Get, url);

    private async Task<List<Row>> RowsAsync(Guid taskId)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id, stream_version, event_type, schema_version, recorded_at, metadata::text " +
            "FROM eventforge.events WHERE stream_type = 'Task' AND stream_id = @id ORDER BY stream_version",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = taskId;
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>Writes a row as the administrator to simulate stored history the API must refuse to read.</summary>
    private async Task SeedAsync(Guid taskId, int version, string eventType, int schemaVersion, string payloadJson)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
            "payload, metadata, recorded_at) VALUES (@e, 'Task', @s, @v, @t, @sv, @p, '{}'::jsonb, now())",
            connection);
        command.Parameters.Add("e", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        command.Parameters.Add("s", NpgsqlDbType.Uuid).Value = taskId;
        command.Parameters.Add("v", NpgsqlDbType.Integer).Value = version;
        command.Parameters.Add("t", NpgsqlDbType.Text).Value = eventType;
        command.Parameters.Add("sv", NpgsqlDbType.Integer).Value = schemaVersion;
        command.Parameters.Add("p", NpgsqlDbType.Jsonb).Value = payloadJson;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> CreateAndAssignAsync(HttpClient client)
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/tasks", $$"""{"taskId":"{{id}}"}""")).Status);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"agent-demo-1"}""")).Status);
        return id;
    }

    [PostgresFact]
    public async Task The_full_workflow_is_persisted_and_read_back_through_a_fresh_host()
    {
        using var writer = NewApi();
        var client = writer.CreateClient();
        var id = Guid.NewGuid();

        (string Url, string Body, string EventType)[] steps =
        [
            ("/api/tasks", $$"""{"taskId":"{{id}}"}""", "TaskCreated"),
            ($"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"agent-demo-1"}""", "TaskAssigned"),
            ($"/api/tasks/{id}/generations", """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""", "CodeGenerated"),
            ($"/api/tasks/{id}/test-results", """{"expectedVersion":3,"generationId":"g1","outcome":"failed"}""", "TestsFailed"),
            ($"/api/tasks/{id}/generations", """{"expectedVersion":4,"generationId":"g2","artifactRef":"synthetic://artifacts/demo/g2"}""", "CodeGenerated"),
            ($"/api/tasks/{id}/test-results", """{"expectedVersion":5,"generationId":"g2","outcome":"passed"}""", "TestsPassed"),
            ($"/api/tasks/{id}/complete", """{"expectedVersion":6}""", "TaskCompleted"),
        ];

        for (var i = 0; i < steps.Length; i++)
        {
            var (status, text, body) = await PostAsync(client, steps[i].Url, steps[i].Body, correlationId: $"evf35-corr-{i + 1}");

            Assert.Equal(i == 0 ? HttpStatusCode.Created : HttpStatusCode.OK, status);
            Assert.Equal(["taskId", "version"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(i + 1, body.GetProperty("version").GetInt32());
            Assert.DoesNotContain("evf35-corr", text);

            // Exactly one new event per command, contiguous versions, in the expected order.
            var rows = await RowsAsync(id);
            Assert.Equal(i + 1, rows.Count);
            Assert.Equal(Enumerable.Range(1, i + 1), rows.Select(r => r.Version));
            Assert.Equal(steps[i].EventType, rows[i].EventType);
            Assert.Contains($"evf35-corr-{i + 1}", rows[i].Metadata);

            if (i == 3)
            {
                // Completion is refused while the current generation has failed its tests.
                var refused = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":4}""");
                Assert.Equal(HttpStatusCode.Conflict, refused.Status);
                Assert.Equal("TestsNotPassed", refused.Body.GetProperty("rule").GetString());
                Assert.Equal(4, (await RowsAsync(id)).Count);
                Assert.Equal("failed", (await GetAsync(client, $"/api/tasks/{id}")).Body.GetProperty("currentTestOutcome").GetString());
            }

            if (i == 4)
            {
                // A new generation clears the previous outcome.
                var state = (await GetAsync(client, $"/api/tasks/{id}")).Body;
                Assert.Equal("g2", state.GetProperty("currentGenerationId").GetString());
                Assert.Equal(JsonValueKind.Null, state.GetProperty("currentTestOutcome").ValueKind);
            }
        }

        // A brand-new host and connection pool: everything below is rebuilt from committed PostgreSQL history.
        using var reader = NewApi();
        var readClient = reader.CreateClient();

        var finalState = (await GetAsync(readClient, $"/api/tasks/{id}")).Body;
        Assert.Equal("completed", finalState.GetProperty("status").GetString());
        Assert.Equal("agent-demo-1", finalState.GetProperty("assignedAgentId").GetString());
        Assert.Equal("g2", finalState.GetProperty("currentGenerationId").GetString());
        Assert.Equal("passed", finalState.GetProperty("currentTestOutcome").GetString());
        Assert.Equal(7, finalState.GetProperty("version").GetInt32());

        var (_, historyText, history) = await GetAsync(readClient, $"/api/tasks/{id}/events");
        var events = history.GetProperty("events").EnumerateArray().ToList();
        var stored = await RowsAsync(id);
        Assert.Equal(7, history.GetProperty("version").GetInt32());
        Assert.Equal(steps.Select(s => s.EventType), events.Select(e => e.GetProperty("eventType").GetString()));
        Assert.Equal(stored.Select(r => r.EventId), events.Select(e => e.GetProperty("eventId").GetGuid()));
        Assert.Equal(stored.Select(r => r.Version), events.Select(e => e.GetProperty("streamVersion").GetInt32()));
        Assert.Equal(stored.Select(r => r.SchemaVersion), events.Select(e => e.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal(
            stored.Select(r => r.RecordedAt),
            events.Select(e => DateTimeOffset.Parse(e.GetProperty("recordedAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.DoesNotContain("evf35-corr", historyText);
        Assert.DoesNotContain("metadata", historyText, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Rejected_requests_leave_the_stream_unchanged_and_queries_report_a_missing_task()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = await CreateAndAssignAsync(client);

        var invalid = await PostAsync(client, $"/api/tasks/{id}/generations", """{"expectedVersion":2,"generationId":"g1","artifactRef":"https://example.com/x"}""");
        var rule = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":2}""");
        var stale = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"other"}""");
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Equal(HttpStatusCode.Conflict, rule.Status);
        Assert.Equal("workflow_rule_violation", rule.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("version_conflict", stale.Body.GetProperty("code").GetString());
        Assert.Equal(2, stale.Body.GetProperty("currentVersion").GetInt32());
        Assert.Equal(2, (await RowsAsync(id)).Count);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/tasks/{missing}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, $"/api/tasks/{missing}/events")).Status);
    }

    [PostgresFact]
    public async Task Concurrent_writers_with_the_same_expected_version_cannot_both_append()
    {
        using var api = NewApi();
        var first = api.CreateClient();
        var second = api.CreateClient();

        for (var round = 0; round < 10; round++)
        {
            var id = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(first, "/api/tasks", $$"""{"taskId":"{{id}}"}""")).Status);
            using var gate = new ManualResetEventSlim(false);

            Task<(HttpStatusCode Status, string Text, JsonElement Body)> Assign(HttpClient c, string agent) => Task.Run(() =>
            {
                gate.Wait();
                return PostAsync(c, $"/api/tasks/{id}/assign", $$"""{"expectedVersion":1,"agentId":"{{agent}}"}""");
            });

            var a = Assign(first, "agent-a");
            var b = Assign(second, "agent-b");
            gate.Set();
            var results = await Task.WhenAll(a, b);

            Assert.Single(results, r => r.Status == HttpStatusCode.OK);
            var loser = Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
            Assert.Equal("version_conflict", loser.Body.GetProperty("code").GetString());
            var rows = await RowsAsync(id);
            Assert.Equal([1, 2], rows.Select(r => r.Version));
            Assert.Equal(["TaskCreated", "TaskAssigned"], rows.Select(r => r.EventType));
        }
    }

    [PostgresFact]
    public async Task Unsupported_stored_history_fails_safely_for_queries_and_commands_and_nothing_is_appended()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(id, 1, "TaskCreated", 2, $$"""{"taskId":"{{id}}","secret":"hunter2"}""");

        // History and commands read the event stream, so they fail strictly. (EVF-38: the state read uses the projection;
        // this stream has no overview row, so it is refused too, but as a projection fault, never as 404.)
        foreach (var response in new[]
                 {
                     await GetAsync(client, $"/api/tasks/{id}/events"),
                     await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}"""),
                 })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
            Assert.Equal("invalid_event_history", response.Body.GetProperty("code").GetString());
            Assert.Equal("UnsupportedSchemaVersion", response.Body.GetProperty("reason").GetString());
            Assert.Equal(1, response.Body.GetProperty("position").GetInt32());
            Assert.DoesNotContain("hunter2", response.Text);
        }

        var state = await GetAsync(client, $"/api/tasks/{id}");
        Assert.Equal(HttpStatusCode.InternalServerError, state.Status);
        Assert.Equal("internal_error", state.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("hunter2", state.Text);

        Assert.Single(await RowsAsync(id));
    }

    [PostgresFact]
    public async Task State_is_served_from_the_projection_and_history_stays_strict_when_they_disagree()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = await CreateAndAssignAsync(client); // events 2, row "assigned|agent-demo-1|-|-|2"
        var generated = await PostAsync(
            client, $"/api/tasks/{id}/generations",
            """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""");
        Assert.Equal(HttpStatusCode.OK, generated.Status);

        // A plausible row that no longer matches the events. A state read cannot detect this; it reports the row.
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName)))
        {
            await admin.OpenAsync();
            await using var tamper = new NpgsqlCommand(
                "UPDATE eventforge.task_overview SET assigned_agent_id = 'projected-only' WHERE task_id = @id", admin);
            tamper.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
            await tamper.ExecuteNonQueryAsync();
        }

        var state = await GetAsync(client, $"/api/tasks/{id}");
        var history = await GetAsync(client, $"/api/tasks/{id}/events");

        Assert.Equal("projected-only", state.Body.GetProperty("assignedAgentId").GetString());
        Assert.Equal(3, state.Body.GetProperty("version").GetInt32());
        Assert.Equal(3, history.Body.GetProperty("events").GetArrayLength());
        Assert.Equal("agent-demo-1", history.Body.GetProperty("events")[1].GetProperty("data").GetProperty("agentId").GetString());
    }

    [PostgresFact]
    public async Task A_legacy_stream_without_a_row_is_a_safe_500_for_state_never_404_and_is_not_backfilled()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = Guid.NewGuid();
        await SeedAsync(id, 1, "TaskCreated", 1, $$"""{"taskId":"{{id}}"}""");

        var state = await GetAsync(client, $"/api/tasks/{id}");
        var history = await GetAsync(client, $"/api/tasks/{id}/events");
        var command = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, state.Status);
        Assert.Equal("internal_error", state.Body.GetProperty("code").GetString());
        Assert.False(state.Body.TryGetProperty("assignedAgentId", out _));
        Assert.Equal(HttpStatusCode.OK, history.Status); // the events remain authoritative and readable
        Assert.Equal(HttpStatusCode.InternalServerError, command.Status); // EVF-37: the write path refuses it too
        Assert.Single(await RowsAsync(id));
        Assert.Null(await OverviewAsync(id)); // nothing was backfilled by a read or a refused write
    }

    [PostgresFact]
    public async Task An_impossible_overview_row_is_a_safe_500_for_state()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = await CreateAndAssignAsync(client);
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName)))
        {
            await admin.OpenAsync();
            await using var tamper = new NpgsqlCommand(
                "UPDATE eventforge.task_overview SET status = 'completed', current_generation_id = 'g', " +
                "latest_test_result = 'passed', last_stream_version = 4 WHERE task_id = @id", admin);
            tamper.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
            await tamper.ExecuteNonQueryAsync();
        }

        var state = await GetAsync(client, $"/api/tasks/{id}");

        Assert.Equal(HttpStatusCode.InternalServerError, state.Status);
        Assert.Equal("internal_error", state.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("completed", state.Text, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task A_database_authentication_failure_is_a_safe_500_that_leaks_no_credentials_or_sql()
    {
        var wrong = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Password = "wrong-secret-pw" }.ConnectionString;
        using var api = new ApiFactory(fakeStore: false, connectionString: wrong);
        var client = api.CreateClient();
        var id = Guid.NewGuid();

        var created = await PostAsync(client, "/api/tasks", $$"""{"taskId":"{{id}}"}""");
        var read = await GetAsync(client, $"/api/tasks/{id}");

        foreach (var response in new[] { created, read })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
            Assert.Equal("internal_error", response.Body.GetProperty("code").GetString());
            Assert.DoesNotContain("wrong-secret-pw", response.Text);
            Assert.DoesNotContain("eventforge_app", response.Text);
            Assert.DoesNotContain("127.0.0.1", response.Text);
            Assert.DoesNotContain("SELECT", response.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await RowsAsync(id));
    }

    private async Task<string?> OverviewAsync(Guid taskId)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT concat_ws('|', status, COALESCE(assigned_agent_id, '-'), COALESCE(current_generation_id, '-'), " +
            "COALESCE(latest_test_result, '-'), last_stream_version) FROM eventforge.task_overview WHERE task_id = @id",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = taskId;
        return (string?)await command.ExecuteScalarAsync();
    }

    [PostgresFact]
    public async Task The_production_composition_updates_the_overview_in_step_with_every_http_command()
    {
        // EVF-37: the real composition root (not a hand-built store) must maintain task_overview.
        using var api = NewApi();
        var client = api.CreateClient();
        var id = Guid.NewGuid();

        (string Url, string Body, string Overview)[] steps =
        [
            ("/api/tasks", $$"""{"taskId":"{{id}}"}""", "created|-|-|-|1"),
            ($"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"agent-demo-1"}""", "assigned|agent-demo-1|-|-|2"),
            ($"/api/tasks/{id}/generations", """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""", "assigned|agent-demo-1|g1|-|3"),
            ($"/api/tasks/{id}/test-results", """{"expectedVersion":3,"generationId":"g1","outcome":"failed"}""", "assigned|agent-demo-1|g1|failed|4"),
            ($"/api/tasks/{id}/generations", """{"expectedVersion":4,"generationId":"g2","artifactRef":"synthetic://artifacts/demo/g2"}""", "assigned|agent-demo-1|g2|-|5"),
            ($"/api/tasks/{id}/test-results", """{"expectedVersion":5,"generationId":"g2","outcome":"passed"}""", "assigned|agent-demo-1|g2|passed|6"),
            ($"/api/tasks/{id}/complete", """{"expectedVersion":6}""", "completed|agent-demo-1|g2|passed|7"),
        ];

        foreach (var (url, body, overview) in steps)
        {
            var response = await PostAsync(client, url, body);
            Assert.True(response.Status is HttpStatusCode.Created or HttpStatusCode.OK, response.Text);
            Assert.Equal(overview, await OverviewAsync(id));
        }

        // A refused command writes neither an event nor a projection change.
        var refused = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":7,"agentId":"agent-demo-2"}""");
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(7, (await RowsAsync(id)).Count);
        Assert.Equal("completed|agent-demo-1|g2|passed|7", await OverviewAsync(id));
    }

    [PostgresFact]
    public async Task A_refused_projection_is_a_safe_500_and_leaves_neither_event_nor_projection_change()
    {
        using var api = NewApi();
        var client = api.CreateClient();
        var id = await CreateAndAssignAsync(client); // events at version 2, row at version 2

        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName)))
        {
            await admin.OpenAsync();
            await using var tamper = new NpgsqlCommand(
                "UPDATE eventforge.task_overview SET last_stream_version = 9 WHERE task_id = @id", admin);
            tamper.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
            await tamper.ExecuteNonQueryAsync();
        }

        var response = await PostAsync(
            client, $"/api/tasks/{id}/generations",
            """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Equal("internal_error", response.Body.GetProperty("code").GetString());
        Assert.DoesNotContain("VersionNotContiguous", response.Text);
        Assert.DoesNotContain("task_overview", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await RowsAsync(id)).Count);
        Assert.Equal("assigned|agent-demo-1|-|-|9", await OverviewAsync(id));
    }
}
