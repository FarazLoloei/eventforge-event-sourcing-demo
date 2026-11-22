using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests;

/// <summary>
/// EVF-38: projection-backed Task state queries against the isolated, disposable PostgreSQL only (skipped unless
/// explicitly enabled, see <see cref="PostgresTestGuard"/>). The real <see cref="PostgreSqlEventStore"/> writes (and
/// projects), the real <see cref="PostgreSqlTaskOverviewReader"/> reads, and the administrator connection is used to
/// seed legacy streams, tamper with rows and observe what is really committed. Each test uses its own task id.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class TaskStateQueryPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly AgentId Agent = new("agent-1");
    private static readonly GenerationId G1 = new("g1");
    private static readonly GenerationId G2 = new("g2");
    private static readonly ArtifactRef Art = new("synthetic://artifacts/it/a");

    private PostgreSqlDatabase? _database;
    private PostgreSqlEventStore? _store;
    private PostgreSqlTaskOverviewReader? _reader;

    private PostgreSqlEventStore Store => _store!;

    private TaskQueryHandler Query => new(Store, _reader!);

    public Task InitializeAsync()
    {
        if (PostgresTestGuard.IsEnabled)
        {
            _database = PostgreSqlDatabase.Create(fixture.AppConnectionString);
            _store = new PostgreSqlEventStore(_database);
            _reader = new PostgreSqlTaskOverviewReader(_database);
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

    private static TaskId NewTask() => new(Guid.NewGuid());

    private static Task<TaskCommandResult> Run(IEventStore store, TaskCommand command, int expectedVersion) =>
        new TaskCommandHandler(store, Guid.NewGuid).HandleAsync(command, expectedVersion, "evf38", CancellationToken.None);

    private static TaskOverviewRow Row(
        TaskId task, WorkflowStatus status, string? agent, string? generation, TestOutcome? result, int version) =>
        new(task.Value, status, agent, generation, result, version);

    private string AdminConnectionString => fixture.AdminConnectionString(fixture.DatabaseName);

    private static async Task AdminAsync(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        await command.ExecuteNonQueryAsync();
    }

    private static string InsertLegacyCreatedEventSql =>
        "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
        "payload, metadata, recorded_at) VALUES (gen_random_uuid(), 'Task', @id, 1, 'TaskCreated', 1, " +
        "jsonb_build_object('taskId', @id::text), '{}'::jsonb, now())";

    /// <summary>An independent reference: the aggregate's own fold of the first <paramref name="count"/> committed events.</summary>
    private async Task<TaskOverviewRow> ReferenceAsync(TaskId task, int? count = null)
    {
        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        var state = TaskAggregate.Rehydrate(loaded.Events.Take(count ?? loaded.Events.Count).Select(TaskEventMapper.FromStored));
        return new TaskOverviewRow(
            state.Id!.Value, state.Status, state.AssignedAgent?.Value, state.CurrentGeneration?.Value,
            state.CurrentTestOutcome, state.Version);
    }

    // ---- Correct state after every transition ---------------------------------------------------------------------

    [PostgresFact]
    public async Task Every_command_is_immediately_readable_as_the_matching_state_and_version()
    {
        var task = NewTask();
        (TaskCommand Command, TaskOverviewRow Expected)[] steps =
        [
            (new CreateTask(task), Row(task, WorkflowStatus.Created, null, null, null, 1)),
            (new AssignTask(task, Agent), Row(task, WorkflowStatus.Assigned, "agent-1", null, null, 2)),
            (new RecordCodeGenerated(task, G1, Art), Row(task, WorkflowStatus.Assigned, "agent-1", "g1", null, 3)),
            (new RecordTestsFailed(task, G1), Row(task, WorkflowStatus.Assigned, "agent-1", "g1", TestOutcome.Failed, 4)),
            (new RecordCodeGenerated(task, G2, Art), Row(task, WorkflowStatus.Assigned, "agent-1", "g2", null, 5)), // result cleared
            (new RecordTestsPassed(task, G2), Row(task, WorkflowStatus.Assigned, "agent-1", "g2", TestOutcome.Passed, 6)),
            (new CompleteTask(task), Row(task, WorkflowStatus.Completed, "agent-1", "g2", TestOutcome.Passed, 7)),
        ];

        for (var i = 0; i < steps.Length; i++)
        {
            Assert.IsType<CommandSucceeded>(await Run(Store, steps[i].Command, i));

            var found = Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None));

            Assert.Equal(steps[i].Expected, found.State);
            Assert.Equal(i + 1, found.State.LastStreamVersion); // the version to send as the next expectedVersion
            Assert.Equal(await ReferenceAsync(task), found.State); // and it equals the aggregate's fold of the events
        }
    }

    [PostgresFact]
    public async Task A_rejected_command_does_not_change_the_state_that_is_read()
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await Run(Store, new AssignTask(task, Agent), 1);

        Assert.IsType<CommandRejected>(await Run(Store, new CompleteTask(task), 2));

        var found = Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None));
        Assert.Equal(Row(task, WorkflowStatus.Assigned, "agent-1", null, null, 2), found.State);
    }

    // ---- Missing task, legacy stream, damaged row ------------------------------------------------------------------

    [PostgresFact]
    public async Task A_task_with_neither_row_nor_events_is_not_found()
    {
        var task = NewTask();

        Assert.Equal(new QueryTaskNotFound(), await Query.GetStateAsync(task, CancellationToken.None));
        Assert.Equal(new TaskOverviewAbsent(false), await _reader!.FindAsync(task, CancellationToken.None));
    }

    [PostgresFact]
    public async Task A_stream_with_events_but_no_row_is_a_projection_fault_and_stays_without_a_row()
    {
        var task = NewTask();
        await AdminAsync(AdminConnectionString, InsertLegacyCreatedEventSql, task.Value);

        Assert.Equal(new TaskOverviewAbsent(true), await _reader!.FindAsync(task, CancellationToken.None));
        Assert.Equal(
            new QueryProjectionUnavailable(TaskOverviewFault.MissingRow),
            await Query.GetStateAsync(task, CancellationToken.None));

        // Reading is not repair: still no row, and a second read gives the same answer.
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM eventforge.task_overview WHERE task_id = @id", connection);
        count.Parameters.Add("id", NpgsqlDbType.Uuid).Value = task.Value;
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        Assert.IsType<QueryProjectionUnavailable>(await Query.GetStateAsync(task, CancellationToken.None));
    }

    [PostgresTheory]
    [InlineData("status = 'completed', current_generation_id = 'g', latest_test_result = 'passed'")] // completed at version 2
    [InlineData("last_stream_version = 5")] // created at version 5
    [InlineData("status = 'assigned', assigned_agent_id = 'x', current_generation_id = 'g', last_stream_version = 2")]
    public async Task An_impossible_row_is_a_projection_fault_and_is_never_returned(string updateSet)
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await Run(Store, new AssignTask(task, Agent), 1);
        await AdminAsync(AdminConnectionString,
            $"UPDATE eventforge.task_overview SET {updateSet} WHERE task_id = @id", task.Value);

        Assert.Equal(
            new QueryProjectionUnavailable(TaskOverviewFault.InvalidRow),
            await Query.GetStateAsync(task, CancellationToken.None));
    }

    // ---- Committed reads --------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Uncommitted_rows_are_not_visible_and_committed_ones_are()
    {
        var task = NewTask();
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteInAsync(connection, transaction, InsertLegacyCreatedEventSql, task.Value);
        await ExecuteInAsync(
            connection, transaction,
            "INSERT INTO eventforge.task_overview (task_id, status, last_stream_version) VALUES (@id, 'created', 1)", task.Value);

        // The writer has not committed: neither its event nor its row exists for any other session.
        Assert.Equal(new QueryTaskNotFound(), await Query.GetStateAsync(task, CancellationToken.None));

        await transaction.CommitAsync();

        var found = Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None));
        Assert.Equal(Row(task, WorkflowStatus.Created, null, null, null, 1), found.State);
    }

    [PostgresFact]
    public async Task An_uncommitted_update_of_an_existing_row_is_not_visible_until_it_commits()
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteInAsync(
            connection, transaction,
            "UPDATE eventforge.task_overview SET status = 'assigned', assigned_agent_id = 'dirty', last_stream_version = 2 " +
            "WHERE task_id = @id", task.Value);

        var before = Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None));
        await transaction.CommitAsync();
        var after = Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None));

        Assert.Equal(Row(task, WorkflowStatus.Created, null, null, null, 1), before.State); // no dirty read
        Assert.Equal(Row(task, WorkflowStatus.Assigned, "dirty", null, null, 2), after.State);
    }

    [PostgresFact]
    public async Task Reads_during_concurrent_commands_only_ever_observe_committed_states_matching_a_history_prefix()
    {
        var task = NewTask();
        var observed = new List<TaskOverviewRow>();
        using var stop = new CancellationTokenSource();

        var reading = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = await Query.GetStateAsync(task, CancellationToken.None);
                if (result is TaskStateFound found)
                {
                    observed.Add(found.State);
                }
                else
                {
                    Assert.IsType<QueryTaskNotFound>(result); // before the first commit; a fault would fail the test
                }
            }
        });

        TaskCommand[] commands =
        [
            new CreateTask(task), new AssignTask(task, Agent), new RecordCodeGenerated(task, G1, Art),
            new RecordTestsFailed(task, G1), new RecordCodeGenerated(task, G2, Art), new RecordTestsPassed(task, G2),
            new CompleteTask(task),
        ];
        for (var i = 0; i < commands.Length; i++)
        {
            Assert.IsType<CommandSucceeded>(await Run(Store, commands[i], i));
            await Task.Delay(15);
        }

        await stop.CancelAsync();
        await reading;

        Assert.NotEmpty(observed);
        foreach (var row in observed.Distinct())
        {
            Assert.Equal(await ReferenceAsync(task, row.LastStreamVersion), row); // a commit boundary, never a mixture
        }

        Assert.Equal(7, (await ReferenceAsync(task)).LastStreamVersion);
        Assert.Equal(await ReferenceAsync(task), Assert.IsType<TaskStateFound>(await Query.GetStateAsync(task, CancellationToken.None)).State);
    }

    // ---- The state read does not touch the event stream -------------------------------------------------------------

    [PostgresFact]
    public async Task A_successful_state_read_does_not_scan_the_events_table_but_a_missing_row_probes_it_once()
    {
        var name = PostgresFixture.NewDatabaseName();
        try
        {
            await fixture.CreateProvisionedDatabaseAsync(name);
            await PostgresFixture.RunMigrationAsync(fixture.ConnectionStringFor("eventforge_migrator", name));
            var app = fixture.ConnectionStringFor("eventforge_app", name);
            var admin = fixture.AdminConnectionString(name);
            var task = NewTask();

            // Disposing a data source closes its connections; a PostgreSQL backend flushes its usage statistics on exit,
            // so each phase below is fully visible in pg_stat_user_tables once its database object is disposed.
            await using (var writer = PostgreSqlDatabase.Create(app))
            {
                var store = new PostgreSqlEventStore(writer);
                Assert.IsType<CommandSucceeded>(await Run(store, new CreateTask(task), 0));
                Assert.IsType<CommandSucceeded>(await Run(store, new AssignTask(task, Agent), 1));
            }

            await Task.Delay(1500);
            var baseline = await EventsScansAsync(admin);

            await using (var readerDatabase = PostgreSqlDatabase.Create(app))
            {
                var reader = new PostgreSqlTaskOverviewReader(readerDatabase);
                for (var i = 0; i < 5; i++)
                {
                    Assert.IsType<TaskOverviewFound>(await reader.FindAsync(task, CancellationToken.None));
                }

                // One probe for a task with no row: the positive control that shows the statistics are being reported.
                Assert.Equal(new TaskOverviewAbsent(false), await reader.FindAsync(NewTask(), CancellationToken.None));
            }

            var scans = baseline;
            for (var attempt = 0; attempt < 100 && scans == baseline; attempt++)
            {
                await Task.Delay(100);
                scans = await EventsScansAsync(admin);
            }

            await Task.Delay(1000);
            scans = await EventsScansAsync(admin);

            Assert.True(scans > baseline, "The probe for a missing row should have been counted.");
            Assert.Equal(baseline + 1, scans); // five successful reads added nothing: only the missing-row probe scanned
        }
        finally
        {
            await fixture.DropDatabaseAsync(name);
        }
    }

    private static async Task<long> EventsScansAsync(string adminConnectionString)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(sum(COALESCE(seq_scan, 0) + COALESCE(idx_scan, 0)), 0)::bigint FROM pg_stat_user_tables " +
            "WHERE schemaname = 'eventforge' AND relname = 'events'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // ---- Cancellation and infrastructure failures -------------------------------------------------------------------

    [PostgresFact]
    public async Task A_cancelled_token_cancels_the_read_without_a_result()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Query.GetStateAsync(NewTask(), cancellation.Token));
    }

    [Fact]
    public async Task An_unreachable_database_propagates_and_is_never_mapped_to_not_found()
    {
        await using var database = PostgreSqlDatabase.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=2;Pooling=false");
        var handler = new TaskQueryHandler(new PostgreSqlEventStore(database), new PostgreSqlTaskOverviewReader(database));

        await Assert.ThrowsAnyAsync<Exception>(() => handler.GetStateAsync(NewTask(), CancellationToken.None));
    }

    private static async Task ExecuteInAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        await command.ExecuteNonQueryAsync();
    }
}
