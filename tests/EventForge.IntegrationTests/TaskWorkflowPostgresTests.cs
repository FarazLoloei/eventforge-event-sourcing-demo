using System.Text.Json;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests;

/// <summary>
/// The Task command handler over the real <see cref="PostgreSqlEventStore"/> in an isolated, disposable PostgreSQL.
/// Skipped unless explicitly enabled (see <see cref="PostgresTestGuard"/>). Each test uses its own random task id.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class TaskWorkflowPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private PostgreSqlDatabase? _database;
    private PostgreSqlEventStore? _store;

    private PostgreSqlEventStore Store => _store!;

    public Task InitializeAsync()
    {
        if (PostgresTestGuard.IsEnabled)
        {
            _database = PostgreSqlDatabase.Create(fixture.AppConnectionString);
            _store = new PostgreSqlEventStore(_database);
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private TaskCommandHandler NewHandler() => new(Store, Guid.NewGuid);

    private static TaskId NewTask() => new(Guid.NewGuid());

    private static Task<TaskCommandResult> Run(TaskCommandHandler handler, TaskCommand command, int expectedVersion) =>
        handler.HandleAsync(command, expectedVersion, "it-run", CancellationToken.None);

    private async Task<long> CountRowsAsync(TaskId task)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM eventforge.events WHERE stream_type = 'Task' AND stream_id = @id", connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = task.Value;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Writes a row as the administrator, to simulate history the handler must refuse to read.</summary>
    private async Task SeedAsync(TaskId task, int version, string eventType, int schemaVersion, string payloadJson)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
            "payload, metadata, recorded_at) VALUES (@e, 'Task', @s, @v, @t, @sv, @p, '{}'::jsonb, now())",
            connection);
        command.Parameters.Add("e", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
        command.Parameters.Add("s", NpgsqlDbType.Uuid).Value = task.Value;
        command.Parameters.Add("v", NpgsqlDbType.Integer).Value = version;
        command.Parameters.Add("t", NpgsqlDbType.Text).Value = eventType;
        command.Parameters.Add("sv", NpgsqlDbType.Integer).Value = schemaVersion;
        command.Parameters.Add("p", NpgsqlDbType.Jsonb).Value = payloadJson;
        await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task A_full_lifecycle_is_persisted_as_schema_v1_events_and_reads_back()
    {
        var task = NewTask();
        var handler = NewHandler();
        var g1 = new GenerationId("g1");
        var agent = new AgentId("agent-1");
        var artifact = new ArtifactRef("synthetic://artifacts/it/g1");

        var steps = new (TaskCommand Command, int ExpectedVersion)[]
        {
            (new CreateTask(task), 0),
            (new AssignTask(task, agent), 1),
            (new RecordCodeGenerated(task, g1, artifact), 2),
            (new RecordTestsPassed(task, g1), 3),
            (new CompleteTask(task), 4),
        };

        foreach (var (command, expectedVersion) in steps)
        {
            var succeeded = Assert.IsType<CommandSucceeded>(await Run(handler, command, expectedVersion));
            Assert.Equal(expectedVersion + 1, succeeded.NewVersion);
        }

        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        Assert.Equal(5, loaded.Version);
        Assert.Equal(
            ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsPassed", "TaskCompleted"],
            loaded.Events.Select(e => e.EventType));
        Assert.All(loaded.Events, e => Assert.Equal(1, e.SchemaVersion));
        Assert.Equal("it-run", loaded.Events[0].Metadata.GetProperty("correlationId").GetString());

        // PostgreSQL JSONB normalises the payload, so compare values, not text.
        var generated = loaded.Events[2].Payload;
        Assert.Equal("g1", generated.GetProperty("generationId").GetString());
        Assert.Equal("synthetic://artifacts/it/g1", generated.GetProperty("artifactRef").GetString());

        var state = TaskAggregate.Rehydrate(loaded.Events.Select(TaskEventMapper.FromStored));
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(g1, state.CurrentGeneration);
        Assert.Equal(TestOutcome.Passed, state.CurrentTestOutcome);
        Assert.Equal(5, state.Version);
    }

    [PostgresFact]
    public async Task Recovery_after_a_failed_generation_completes_only_once_the_new_generation_passes()
    {
        var task = NewTask();
        var handler = NewHandler();
        var agent = new AgentId("agent-1");
        var g1 = new GenerationId("g1");
        var g2 = new GenerationId("g2");

        Assert.IsType<CommandSucceeded>(await Run(handler, new CreateTask(task), 0));
        Assert.IsType<CommandSucceeded>(await Run(handler, new AssignTask(task, agent), 1));
        Assert.IsType<CommandSucceeded>(await Run(handler, new RecordCodeGenerated(task, g1, new ArtifactRef("a1")), 2));
        Assert.IsType<CommandSucceeded>(await Run(handler, new RecordTestsFailed(task, g1), 3));
        Assert.Equal(4, await CountRowsAsync(task));

        // Completion is refused while the latest outcome for the current generation is a failure.
        var rejected = await Run(handler, new CompleteTask(task), 4);

        Assert.Equal(new CommandRejected(RejectionCode.TestsNotPassed), rejected);
        Assert.Equal(4, await CountRowsAsync(task));
        var afterRejection = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        Assert.Equal(4, afterRejection.Version);

        Assert.IsType<CommandSucceeded>(await Run(handler, new RecordCodeGenerated(task, g2, new ArtifactRef("a2")), 4));
        Assert.IsType<CommandSucceeded>(await Run(handler, new RecordTestsPassed(task, g2), 5));
        var completed = Assert.IsType<CommandSucceeded>(await Run(handler, new CompleteTask(task), 6));
        Assert.Equal(7, completed.NewVersion);

        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        Assert.Equal(7, await CountRowsAsync(task));
        Assert.Equal(7, loaded.Version);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], loaded.Events.Select(e => e.StreamVersion));
        Assert.Equal(
            ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsFailed", "CodeGenerated", "TestsPassed", "TaskCompleted"],
            loaded.Events.Select(e => e.EventType));

        // Expected state written by hand, not derived from the aggregate under test.
        var state = TaskAggregate.Rehydrate(loaded.Events.Select(TaskEventMapper.FromStored));
        Assert.Equal(task, state.Id);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal(agent, state.AssignedAgent);
        Assert.Equal(g2, state.CurrentGeneration);
        Assert.Equal(TestOutcome.Passed, state.CurrentTestOutcome);
        Assert.Equal(2, state.UsedGenerations.Count);
        Assert.True(state.UsedGenerations.SetEquals([g1, g2]));
        Assert.Equal(7, state.Version);
    }

    private static (TaskCommand[] Setup, TaskCommand Rejected) RejectionScenario(string name, TaskId task)
    {
        var agent = new AgentId("agent-1");
        var g1 = new GenerationId("g1");
        var g2 = new GenerationId("g2");
        TaskCommand[] begun = [new CreateTask(task), new AssignTask(task, agent), new RecordCodeGenerated(task, g1, new ArtifactRef("a1"))];

        return name switch
        {
            "generation-id-reused" => (begun, new RecordCodeGenerated(task, g1, new ArtifactRef("a-other"))),
            "superseded-generation" =>
                ([.. begun, new RecordCodeGenerated(task, g2, new ArtifactRef("a2"))], new RecordTestsPassed(task, g1)),
            "complete-after-superseded-pass" =>
                ([.. begun, new RecordTestsPassed(task, g1), new RecordCodeGenerated(task, g2, new ArtifactRef("a2"))],
                    new CompleteTask(task)),
            "task-completed" =>
                ([.. begun, new RecordTestsPassed(task, g1), new CompleteTask(task)],
                    new RecordCodeGenerated(task, g2, new ArtifactRef("a2"))),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [PostgresTheory]
    [InlineData("generation-id-reused", RejectionCode.GenerationAlreadyUsed)]
    [InlineData("superseded-generation", RejectionCode.SupersededGeneration)]
    [InlineData("complete-after-superseded-pass", RejectionCode.TestsNotPassed)]
    [InlineData("task-completed", RejectionCode.TaskAlreadyCompleted)]
    public async Task A_rejected_command_persists_no_row_and_leaves_the_stream_version_unchanged(
        string scenario,
        RejectionCode expected)
    {
        var task = NewTask();
        var handler = NewHandler();
        var (setup, rejected) = RejectionScenario(scenario, task);
        for (var i = 0; i < setup.Length; i++)
        {
            Assert.IsType<CommandSucceeded>(await Run(handler, setup[i], i));
        }

        Assert.Equal(setup.Length, await CountRowsAsync(task));

        var result = await Run(handler, rejected, setup.Length);

        Assert.Equal(new CommandRejected(expected), result);
        Assert.Equal(setup.Length, await CountRowsAsync(task));
        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        Assert.Equal(setup.Length, loaded.Version);
    }

    [PostgresFact]
    public async Task A_domain_rejection_appends_nothing()
    {
        var task = NewTask();
        var handler = NewHandler();
        await Run(handler, new CreateTask(task), 0);
        await Run(handler, new AssignTask(task, new AgentId("agent-1")), 1);

        var result = await Run(handler, new AssignTask(task, new AgentId("agent-2")), 2);

        Assert.Equal(new CommandRejected(RejectionCode.TaskAlreadyAssigned), result);
        Assert.Equal(2, await CountRowsAsync(task));
    }

    [PostgresFact]
    public async Task A_stale_expected_version_is_a_conflict_and_appends_nothing()
    {
        var task = NewTask();
        var handler = NewHandler();
        await Run(handler, new CreateTask(task), 0);

        var result = await Run(handler, new AssignTask(task, new AgentId("agent-1")), 0);

        Assert.Equal(new VersionConflict(1), result);
        Assert.Equal(1, await CountRowsAsync(task));
    }

    [PostgresFact]
    public async Task Racing_writers_on_the_same_expected_version_have_exactly_one_winner_and_a_contiguous_stream()
    {
        for (var round = 0; round < 10; round++)
        {
            var task = NewTask();
            await Run(NewHandler(), new CreateTask(task), 0);
            await Run(NewHandler(), new AssignTask(task, new AgentId("agent-1")), 1);

            var first = NewHandler();
            var second = NewHandler();
            var results = await Task.WhenAll(
                Task.Run(() => Run(first, new RecordCodeGenerated(task, new GenerationId("g1"), new ArtifactRef("a1")), 2)),
                Task.Run(() => Run(second, new RecordCodeGenerated(task, new GenerationId("g2"), new ArtifactRef("a2")), 2)));

            Assert.Single(results, r => r is CommandSucceeded);
            Assert.Single(results, r => r is VersionConflict);
            Assert.Equal(3, await CountRowsAsync(task));

            var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
            Assert.Equal([1, 2, 3], loaded.Events.Select(e => e.StreamVersion));
        }
    }

    [PostgresFact]
    public async Task A_duplicate_event_id_is_reported_and_the_second_stream_stays_empty()
    {
        var fixedId = Guid.NewGuid();
        var handler = new TaskCommandHandler(Store, () => fixedId);
        var first = NewTask();
        var second = NewTask();

        Assert.IsType<CommandSucceeded>(await Run(handler, new CreateTask(first), 0));
        var result = await Run(handler, new CreateTask(second), 0);

        Assert.Equal(new DuplicateEventId(), result);
        Assert.Equal(1, await CountRowsAsync(first));
        Assert.Equal(0, await CountRowsAsync(second));
    }

    [PostgresTheory]
    [InlineData("TaskArchived", 1, "{}", InvalidHistoryReason.UnknownEventType)]
    [InlineData("TaskCreated", 2, "{}", InvalidHistoryReason.UnsupportedSchemaVersion)]
    [InlineData("TaskCompleted", 1, """{"unexpected":"value"}""", InvalidHistoryReason.MalformedPayload)]
    [InlineData("TaskAssigned", 1, """{"agentId":"agent-1"}""", InvalidHistoryReason.InvalidSequence)]
    public async Task Unreadable_stored_history_is_refused_and_nothing_is_appended(
        string eventType,
        int schemaVersion,
        string payload,
        InvalidHistoryReason reason)
    {
        var task = NewTask();
        await SeedAsync(task, 1, eventType, schemaVersion, payload);

        var result = await Run(NewHandler(), new CompleteTask(task), 1);

        Assert.Equal(new InvalidHistory(reason, 1), result);
        Assert.Equal(1, await CountRowsAsync(task));
    }

    [PostgresFact]
    public async Task Duplicate_json_keys_are_collapsed_by_jsonb_before_the_mapper_can_see_them()
    {
        // Documents a limit, not a safeguard: PostgreSQL JSONB keeps only the last of duplicate keys, so a
        // duplicate-key payload can no longer be detected after storage. The mapper's duplicate-property check
        // applies only to payloads that still contain the duplicates (unit-tested with text-parsed payloads).
        var task = NewTask();
        await SeedAsync(task, 1, "TaskAssigned", 1, """{"agentId":"first","agentId":"last"}""");

        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);

        var payload = Assert.Single(loaded.Events).Payload;
        Assert.Single(payload.EnumerateObject());
        Assert.Equal("last", payload.GetProperty("agentId").GetString());
    }

    [PostgresFact]
    public async Task A_non_contiguous_stored_stream_is_refused_as_corrupted()
    {
        var task = NewTask();
        await SeedAsync(task, 2, "TaskCreated", 1, $$"""{"taskId":"{{task.Value}}"}""");

        var result = await Run(NewHandler(), new AssignTask(task, new AgentId("agent-1")), 0);

        Assert.Equal(new InvalidHistory(InvalidHistoryReason.StreamCorrupted, null), result);
        Assert.Equal(1, await CountRowsAsync(task));
    }
}
