using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// EVF-33: the five Task command endpoints, hosted in memory with a scripted <see cref="InMemoryEventStore"/>.
/// No database is contacted: the connection string is blank and the store is replaced.
/// </summary>
public sealed class TaskCommandApiTests
{
    private sealed class ApiFactory(string environment = "Development", bool fakeStore = true)
        : WebApplicationFactory<Program>
    {
        public InMemoryEventStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EventForge"] = string.Empty }));
            builder.ConfigureTestServices(services =>
            {
                if (fakeStore)
                {
                    services.RemoveAll<IEventStore>();
                    services.AddSingleton<IEventStore>(Store);
                }
            });
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> PostAsync(
        HttpClient client, string url, string body, HttpStatusCode expected)
    {
        var response = await client.PostAsync(url, Json(body));
        Assert.Equal(expected, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<(JsonElement Body, HttpResponseMessage Response)> PostWithResponseAsync(
        HttpClient client, string url, string body)
    {
        var response = await client.PostAsync(url, Json(body));
        return (await ReadAsync(response), response);
    }

    private static async Task<Guid> CreateAsync(HttpClient client)
    {
        var id = Guid.NewGuid();
        await PostAsync(client, "/api/tasks", $$"""{"taskId":"{{id}}"}""", HttpStatusCode.Created);
        return id;
    }

    private static void AssertProblem(JsonElement body, int status, string code)
    {
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal($"urn:eventforge:problem:{code}", body.GetProperty("type").GetString());
    }

    // ---- success -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_five_commands_run_a_complete_lifecycle_and_return_only_task_id_and_version()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var (created, createdResponse) = await PostWithResponseAsync(client, "/api/tasks", $$"""{"taskId":"{{id}}"}""");
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal($"/api/tasks/{id}", createdResponse.Headers.Location?.ToString());
        AssertOnlyTaskIdAndVersion(created, id, 1);

        var assigned = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"agent-demo-1"}""", HttpStatusCode.OK);
        AssertOnlyTaskIdAndVersion(assigned, id, 2);

        var generated = await PostAsync(client, $"/api/tasks/{id}/generations",
            """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://artifacts/demo/g1"}""", HttpStatusCode.OK);
        AssertOnlyTaskIdAndVersion(generated, id, 3);

        var tested = await PostAsync(client, $"/api/tasks/{id}/test-results",
            """{"expectedVersion":3,"generationId":"g1","outcome":"passed"}""", HttpStatusCode.OK);
        AssertOnlyTaskIdAndVersion(tested, id, 4);

        var completed = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":4}""", HttpStatusCode.OK);
        AssertOnlyTaskIdAndVersion(completed, id, 5);

        Assert.Equal(
            ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsPassed", "TaskCompleted"],
            factory.Store.EventsOf(id).Select(e => e.EventType));
    }

    [Fact]
    public async Task A_failed_test_result_maps_to_TestsFailed()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""", HttpStatusCode.OK);
        await PostAsync(client, $"/api/tasks/{id}/generations",
            """{"expectedVersion":2,"generationId":"g1","artifactRef":"synthetic://a/g1"}""", HttpStatusCode.OK);

        await PostAsync(client, $"/api/tasks/{id}/test-results",
            """{"expectedVersion":3,"generationId":"g1","outcome":"failed"}""", HttpStatusCode.OK);

        Assert.Equal("TestsFailed", factory.Store.EventsOf(id)[^1].EventType);
    }

    [Fact]
    public async Task Property_names_bind_case_insensitively()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);

        await PostAsync(client, $"/api/tasks/{id}/assign", """{"EXPECTEDVERSION":1,"agentid":"agent-demo-1"}""", HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_valid_correlation_id_is_stored_in_event_metadata_only()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks") { Content = Json($$"""{"taskId":"{{id}}"}""") };
        request.Headers.Add("X-Correlation-Id", "demo-corr-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("demo-corr-1", factory.Store.EventsOf(id)[0].Metadata.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("demo-corr-1", await response.Content.ReadAsStringAsync());
    }

    private static void AssertOnlyTaskIdAndVersion(JsonElement body, Guid id, int version)
    {
        Assert.Equal(["taskId", "version"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(id, body.GetProperty("taskId").GetGuid());
        Assert.Equal(version, body.GetProperty("version").GetInt32());
    }

    // ---- validation: nothing reaches the store ---------------------------------------------------------------

    public static TheoryData<string, string, string> InvalidValues() => new()
    {
        { "/api/tasks", "{}", "taskId" },
        { "/api/tasks", """{"taskId":"not-a-guid"}""", "taskId" },
        { "/api/tasks", """{"taskId":"00000000-0000-0000-0000-000000000000"}""", "taskId" },
        { "/api/tasks", """{"taskId":"{9d56311e-acda-47c9-8a1c-f8e5d5605f4c}"}""", "taskId" },
        { "/api/tasks/not-a-guid/complete", """{"expectedVersion":0}""", "id" },
        { "/api/tasks/00000000-0000-0000-0000-000000000000/complete", """{"expectedVersion":0}""", "id" },
        { "/api/tasks/{id}/assign", """{"agentId":"a"}""", "expectedVersion" },
        { "/api/tasks/{id}/assign", """{"expectedVersion":null,"agentId":"a"}""", "expectedVersion" },
        { "/api/tasks/{id}/assign", """{"expectedVersion":-1,"agentId":"a"}""", "expectedVersion" },
        { "/api/tasks/{id}/assign", """{"expectedVersion":1}""", "agentId" },
        { "/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"   "}""", "agentId" },
        { "/api/tasks/{id}/assign", $$"""{"expectedVersion":1,"agentId":"{{new string('a', 101)}}"}""", "agentId" },
        { "/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a\u0001b"}""", "agentId" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"artifactRef":"synthetic://a"}""", "generationId" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1","artifactRef":"https://example.com/x"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1","artifactRef":"/etc/passwd"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1","artifactRef":"synthetic://"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1","artifactRef":"synthetic://a b"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", """{"expectedVersion":1,"generationId":"g1","artifactRef":"synthetic://a\n"}""", "artifactRef" },
        { "/api/tasks/{id}/generations", $$"""{"expectedVersion":1,"generationId":"g1","artifactRef":"synthetic://{{new string('a', 489)}}"}""", "artifactRef" },
        { "/api/tasks/{id}/test-results", """{"expectedVersion":1,"generationId":"g1"}""", "outcome" },
        { "/api/tasks/{id}/test-results", """{"expectedVersion":1,"generationId":"g1","outcome":"Passed"}""", "outcome" },
        { "/api/tasks/{id}/test-results", """{"expectedVersion":1,"generationId":"g1","outcome":"error"}""", "outcome" },
        { "/api/tasks/{id}/test-results", """{"expectedVersion":1,"outcome":"passed"}""", "generationId" },
        { "/api/tasks/{id}/complete", "{}", "expectedVersion" },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task Invalid_values_are_rejected_before_the_handler_runs(string route, string body, string field)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(route.Replace("{id}", Guid.NewGuid().ToString()), Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        AssertProblem(problem, 400, "invalid_request");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"expected an error for '{field}'");
        Assert.Equal(0, factory.Store.LoadCalls);
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"expectedVersion":"1","agentId":"a"}""")]
    [InlineData("""{"expectedVersion":1.5,"agentId":"a"}""")]
    [InlineData("""{"expectedVersion":1,"agentId":7}""")]
    [InlineData("""{"expectedVersion":1,"agentId":"a","unexpectedField":true}""")]
    public async Task Malformed_json_and_unknown_members_are_rejected_before_the_handler_runs(string body)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/tasks/{Guid.NewGuid()}/assign", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertProblem(await ReadAsync(response), 400, "invalid_request");
        Assert.Equal(0, factory.Store.LoadCalls);
    }

    [Fact]
    public async Task A_non_json_content_type_is_unsupported()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var content = new StringContent("""{"expectedVersion":1,"agentId":"a"}""", Encoding.UTF8, "text/plain");

        var response = await client.PostAsync($"/api/tasks/{Guid.NewGuid()}/assign", content);

        // The framework answers 415 itself, before the endpoint runs; the body is not customized.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, factory.Store.LoadCalls);
    }

    [Theory]
    [InlineData("   ")]
    public async Task A_blank_correlation_id_is_rejected(string value)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks")
        {
            Content = Json($$"""{"taskId":"{{Guid.NewGuid()}}"}"""),
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", value);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("errors").TryGetProperty("X-Correlation-Id", out _));
        Assert.Equal(0, factory.Store.LoadCalls);
    }

    [Fact]
    public async Task An_overlong_correlation_id_is_rejected()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks")
        {
            Content = Json($$"""{"taskId":"{{Guid.NewGuid()}}"}"""),
        };
        request.Headers.Add("X-Correlation-Id", new string('c', 101));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Store.LoadCalls);
    }

    // ---- handler results -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_domain_rejection_is_a_409_and_appends_nothing()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""", HttpStatusCode.OK);
        var appendsBefore = factory.Store.AppendCalls;

        var problem = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":2}""", HttpStatusCode.Conflict);

        AssertProblem(problem, 409, "workflow_rule_violation");
        Assert.Equal("TestsNotPassed", problem.GetProperty("rule").GetString());
        Assert.Equal(appendsBefore, factory.Store.AppendCalls);
        Assert.Equal(2, factory.Store.EventsOf(id).Count);
    }

    [Fact]
    public async Task A_stale_expected_version_is_a_409_with_the_current_version_and_wins_over_domain_rules()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""", HttpStatusCode.OK);
        var appendsBefore = factory.Store.AppendCalls;

        // Version 1 is stale (the stream is at 2), and assigning twice would also break a rule: the version check wins.
        var problem = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"b"}""", HttpStatusCode.Conflict);

        AssertProblem(problem, 409, "version_conflict");
        Assert.Equal(2, problem.GetProperty("currentVersion").GetInt32());
        Assert.Equal(appendsBefore, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task A_lost_append_race_is_a_409_without_an_invented_current_version()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        factory.Store.ForcedAppendResult = AppendResult.VersionConflict(null);

        var problem = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""", HttpStatusCode.Conflict);

        AssertProblem(problem, 409, "version_conflict");
        Assert.False(problem.TryGetProperty("currentVersion", out _));
    }

    [Fact]
    public async Task A_duplicate_create_is_a_version_conflict()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);

        var problem = await PostAsync(client, "/api/tasks", $$"""{"taskId":"{{id}}"}""", HttpStatusCode.Conflict);

        AssertProblem(problem, 409, "version_conflict");
        Assert.Equal(1, problem.GetProperty("currentVersion").GetInt32());
        Assert.Single(factory.Store.EventsOf(id));
    }

    [Fact]
    public async Task A_command_on_a_missing_task_is_404_at_version_0_and_a_409_otherwise()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var notFound = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":0}""", HttpStatusCode.NotFound);
        var conflict = await PostAsync(client, $"/api/tasks/{id}/complete", """{"expectedVersion":3}""", HttpStatusCode.Conflict);

        AssertProblem(notFound, 404, "task_not_found");
        AssertProblem(conflict, 409, "version_conflict");
        Assert.Equal(0, conflict.GetProperty("currentVersion").GetInt32());
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task An_unknown_commit_outcome_is_reported_once_and_never_retried()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        factory.Store.AppendException = new EventStoreCommitUnknownException(
            new StreamKey("Task", id), new InvalidOperationException("Host=db;Password=hunter2"));
        var appendsBefore = factory.Store.AppendCalls;

        var response = await client.PostAsync($"/api/tasks/{id}/assign", Json("""{"expectedVersion":1,"agentId":"a"}"""));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(JsonDocument.Parse(text).RootElement, 500, "commit_outcome_unknown");
        Assert.Equal(appendsBefore + 1, factory.Store.AppendCalls);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public async Task A_duplicate_event_id_is_a_safe_500_and_is_not_retried()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);
        factory.Store.ForcedAppendResult = AppendResult.DuplicateEventId();
        var appendsBefore = factory.Store.AppendCalls;

        var problem = await PostAsync(client, $"/api/tasks/{id}/assign", """{"expectedVersion":1,"agentId":"a"}""", HttpStatusCode.InternalServerError);

        AssertProblem(problem, 500, "duplicate_event_id");
        Assert.Equal(appendsBefore + 1, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task Unsupported_stored_history_is_a_safe_500_with_reason_and_position_only()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();
        factory.Store.Seed(id, InMemoryEventStore.Stored(id, 1, "TaskCreated", 2, $$"""{"taskId":"{{id}}","secret":"hunter2"}"""));

        var response = await client.PostAsync($"/api/tasks/{id}/assign", Json("""{"expectedVersion":1,"agentId":"a"}"""));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = JsonDocument.Parse(text).RootElement;
        AssertProblem(problem, 500, "invalid_event_history");
        Assert.Equal("UnsupportedSchemaVersion", problem.GetProperty("reason").GetString());
        Assert.Equal(1, problem.GetProperty("position").GetInt32());
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal(0, factory.Store.AppendCalls);
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_safe_500_without_exception_details()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Store.LoadException = new InvalidOperationException("Host=db.internal;Password=hunter2");

        var response = await client.PostAsync("/api/tasks", Json($$"""{"taskId":"{{Guid.NewGuid()}}"}"""));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(JsonDocument.Parse(text).RootElement, 500, "internal_error");
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("db.internal", text);
    }

    // ---- startup, health, Swagger ----------------------------------------------------------------------------

    [Fact]
    public async Task The_host_starts_without_credentials_health_is_ok_and_a_command_fails_safely()
    {
        using var factory = new ApiFactory(fakeStore: false);
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        var response = await client.PostAsync("/api/tasks", Json($$"""{"taskId":"{{Guid.NewGuid()}}"}"""));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertProblem(JsonDocument.Parse(text).RootElement, 500, "internal_error");
        Assert.DoesNotContain("Connection string", text);
    }

    [Fact]
    public async Task Swagger_ui_and_the_openapi_document_are_available_in_development()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var document = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        var openApi = await ReadAsync(document);
        var paths = openApi.GetProperty("paths");
        Assert.Equal(
            new[] { "/api/tasks", "/api/tasks/{id}/assign", "/api/tasks/{id}/complete", "/api/tasks/{id}/generations", "/api/tasks/{id}/test-results" },
            paths.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var path in paths.EnumerateObject())
        {
            var operation = Assert.Single(path.Value.EnumerateObject());
            Assert.Equal("post", operation.Name);
            Assert.Equal("Task Commands", operation.Value.GetProperty("tags")[0].GetString());
            Assert.True(operation.Value.TryGetProperty("summary", out _));
        }
    }

    [Fact]
    public async Task Swagger_ui_and_the_openapi_document_are_not_mapped_outside_development()
    {
        using var factory = new ApiFactory(environment: "Production");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task The_get_routes_belong_to_a_later_task_and_are_not_mapped()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var id = await CreateAsync(client);

        Assert.True((await client.GetAsync($"/api/tasks/{id}")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True((await client.GetAsync($"/api/tasks/{id}/events")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}
