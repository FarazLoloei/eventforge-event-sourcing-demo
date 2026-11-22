using System.Text.Json;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests;

/// <summary>
/// EVF-37: the inline <c>task_overview</c> projection inside the event-store transaction, against the isolated,
/// disposable PostgreSQL only. Skipped unless explicitly enabled (see <see cref="PostgresTestGuard"/>). Rows and event
/// counts are read back with the administrative connection, so the assertions show what was really committed.
/// Each test uses its own random task id.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class TaskOverviewProjectionPostgresTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static TaskId NewTask() => new(Guid.NewGuid());

    private static TaskCommandHandler Handler(IEventStore store) => new(store, Guid.NewGuid);

    private static Task<TaskCommandResult> Run(IEventStore store, TaskCommand command, int expectedVersion) =>
        Handler(store).HandleAsync(command, expectedVersion, "evf37", CancellationToken.None);

    private static readonly AgentId Agent = new("agent-1");
    private static readonly GenerationId G1 = new("g1");
    private static readonly GenerationId G2 = new("g2");
    private static readonly ArtifactRef Art = new("synthetic://artifacts/it/a");

    private NpgsqlConnection OpenAdmin()
    {
        var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        connection.Open();
        return connection;
    }

    private Task<long> EventCountAsync(Guid id, string streamType = "Task") =>
        EventCountAtAsync(fixture.AdminConnectionString(fixture.DatabaseName), id, streamType);

    private Task<string?> OverviewAsync(Guid id) =>
        OverviewAtAsync(fixture.AdminConnectionString(fixture.DatabaseName), id);

    private static async Task<long> EventCountAtAsync(string connectionString, Guid id, string streamType = "Task")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM eventforge.events WHERE stream_type = @t AND stream_id = @id", connection);
        command.Parameters.Add("t", NpgsqlDbType.Text).Value = streamType;
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The committed row as "status|agent|generation|result|version" ("-" for null), or null when absent.</summary>
    private static async Task<string?> OverviewAtAsync(string connectionString, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT concat_ws('|', status, COALESCE(assigned_agent_id, '-'), COALESCE(current_generation_id, '-'), " +
            "COALESCE(latest_test_result, '-'), last_stream_version) FROM eventforge.task_overview WHERE task_id = @id",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task AdminAsync(string sql, Guid id)
    {
        await using var connection = OpenAdmin();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An independent reference: the aggregate's own fold of the committed events, in the same text form.</summary>
    private async Task<string> ReferenceAsync(TaskId task)
    {
        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", task.Value), CancellationToken.None);
        var state = TaskAggregate.Rehydrate(loaded.Events.Select(TaskEventMapper.FromStored));
        return string.Join('|',
            state.Status.ToString().ToLowerInvariant(),
            state.AssignedAgent?.Value ?? "-",
            state.CurrentGeneration?.Value ?? "-",
            state.CurrentTestOutcome?.ToString().ToLowerInvariant() ?? "-",
            state.Version);
    }

    // ---- Correct projection after every transition ---------------------------------------------------------------

    [PostgresFact]
    public async Task Every_transition_commits_the_matching_row_and_version_with_its_event()
    {
        var task = NewTask();

        (TaskCommand Command, string Expected)[] steps =
        [
            (new CreateTask(task), "created|-|-|-|1"),
            (new AssignTask(task, Agent), "assigned|agent-1|-|-|2"),
            (new RecordCodeGenerated(task, G1, Art), "assigned|agent-1|g1|-|3"),
            (new RecordTestsFailed(task, G1), "assigned|agent-1|g1|failed|4"),
            (new RecordCodeGenerated(task, G2, Art), "assigned|agent-1|g2|-|5"), // the old result is cleared
            (new RecordTestsPassed(task, G2), "assigned|agent-1|g2|passed|6"),
            (new CompleteTask(task), "completed|agent-1|g2|passed|7"),
        ];

        for (var i = 0; i < steps.Length; i++)
        {
            var result = await Run(Store, steps[i].Command, i);

            Assert.Equal(i + 1, Assert.IsType<CommandSucceeded>(result).NewVersion);
            Assert.Equal(i + 1, await EventCountAsync(task.Value));
            Assert.Equal(steps[i].Expected, await OverviewAsync(task.Value));
            Assert.Equal(await ReferenceAsync(task), await OverviewAsync(task.Value));
        }
    }

    [PostgresFact]
    public async Task A_rejected_command_changes_neither_events_nor_the_row()
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await Run(Store, new AssignTask(task, Agent), 1);

        var result = await Run(Store, new CompleteTask(task), 2);

        Assert.Equal(new CommandRejected(RejectionCode.TestsNotPassed), result);
        Assert.Equal(2, await EventCountAsync(task.Value));
        Assert.Equal("assigned|agent-1|-|-|2", await OverviewAsync(task.Value));
    }

    [PostgresFact]
    public async Task A_batch_of_several_events_updates_the_row_once_to_the_final_state()
    {
        var task = NewTask();
        var metadata = JsonDocument.Parse("{}").RootElement.Clone();
        var stream = new StreamKey("Task", task.Value);
        NewEvent[] first =
        [
            TaskEventMapper.ToNewEvent(new TaskCreated(task), Guid.NewGuid(), metadata),
            TaskEventMapper.ToNewEvent(new TaskAssigned(Agent), Guid.NewGuid(), metadata),
            TaskEventMapper.ToNewEvent(new CodeGenerated(G1, Art), Guid.NewGuid(), metadata),
        ];
        NewEvent[] second =
        [
            TaskEventMapper.ToNewEvent(new TestsPassed(G1), Guid.NewGuid(), metadata),
            TaskEventMapper.ToNewEvent(new TaskCompleted(), Guid.NewGuid(), metadata),
        ];

        var one = await Store.AppendAsync(stream, 0, first, CancellationToken.None);
        Assert.Equal(3, one.CurrentVersion);
        Assert.Equal("assigned|agent-1|g1|-|3", await OverviewAsync(task.Value));

        var two = await Store.AppendAsync(stream, 3, second, CancellationToken.None);
        Assert.Equal(5, two.CurrentVersion);
        Assert.Equal("completed|agent-1|g1|passed|5", await OverviewAsync(task.Value));
        Assert.Equal(await ReferenceAsync(task), await OverviewAsync(task.Value));
    }

    [PostgresFact]
    public async Task A_batch_with_an_invalid_event_in_the_middle_rolls_back_every_event_and_the_row()
    {
        var task = NewTask();
        var metadata = JsonDocument.Parse("{}").RootElement.Clone();
        NewEvent[] batch =
        [
            TaskEventMapper.ToNewEvent(new TaskCreated(task), Guid.NewGuid(), metadata),
            TaskEventMapper.ToNewEvent(new TaskCompleted(), Guid.NewGuid(), metadata), // cannot follow a created task
        ];

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Store.AppendAsync(new StreamKey("Task", task.Value), 0, batch, CancellationToken.None));

        Assert.Equal(TaskOverviewFault.InvalidTransition, exception.Fault);
        Assert.Equal(0, await EventCountAsync(task.Value));
        Assert.Null(await OverviewAsync(task.Value));
    }

    // ---- Version guards and unusable projection state ------------------------------------------------------------------

    [PostgresFact]
    public async Task An_existing_stream_without_a_row_is_refused_and_nothing_is_backfilled()
    {
        var task = NewTask();
        await AdminAsync(
            "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
            "payload, metadata, recorded_at) VALUES (gen_random_uuid(), 'Task', @id, 1, 'TaskCreated', 1, " +
            "jsonb_build_object('taskId', @id::text), '{}'::jsonb, now())", task.Value);

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Run(Store, new AssignTask(task, Agent), 1));

        Assert.Equal(TaskOverviewFault.MissingRow, exception.Fault);
        Assert.Equal(1, await EventCountAsync(task.Value)); // the legacy event is untouched, no event 2
        Assert.Null(await OverviewAsync(task.Value)); // no automatic backfill
    }

    [PostgresTheory]
    [InlineData("behind")] // the stream moved on without the row: the row is one event behind the new event
    [InlineData("ahead")] // an event is missing from the stream: the row is already at the version the new event takes
    public async Task A_plausible_row_that_does_not_follow_the_stream_is_refused_and_both_writes_roll_back(string drift)
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await Run(Store, new AssignTask(task, Agent), 1);
        await Run(Store, new RecordCodeGenerated(task, G1, Art), 2); // row: assigned|agent-1|g1|-|3
        if (drift == "behind")
        {
            await AdminAsync(
                "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
                "payload, metadata, recorded_at) VALUES (gen_random_uuid(), 'Task', @id, 4, 'TestsPassed', 1, " +
                "jsonb_build_object('generationId', 'g1'), '{}'::jsonb, now())", task.Value);
        }
        else
        {
            await AdminAsync("DELETE FROM eventforge.events WHERE stream_id = @id AND stream_version = 3", task.Value);
        }

        var expectedVersion = drift == "behind" ? 4 : 2;
        var events = await EventCountAsync(task.Value);
        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Run(Store, new RecordCodeGenerated(task, G2, Art), expectedVersion));

        Assert.Equal(TaskOverviewFault.VersionNotContiguous, exception.Fault);
        Assert.Equal(events, await EventCountAsync(task.Value));
        Assert.Equal("assigned|agent-1|g1|-|3", await OverviewAsync(task.Value));
    }

    [PostgresFact]
    public async Task Creating_a_task_whose_row_already_exists_is_refused_and_appends_nothing()
    {
        var task = NewTask();
        await AdminAsync(
            "INSERT INTO eventforge.task_overview (task_id, status, last_stream_version) VALUES (@id, 'created', 1)", task.Value);

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Run(Store, new CreateTask(task), 0));

        Assert.Equal(TaskOverviewFault.RowAlreadyExists, exception.Fault);
        Assert.Equal(0, await EventCountAsync(task.Value));
        Assert.Equal("created|-|-|-|1", await OverviewAsync(task.Value));
    }

    [PostgresTheory]
    // The row satisfies every PostgreSQL CHECK constraint, but no valid Task history can produce it.
    [InlineData("status = 'completed', assigned_agent_id = 'x', current_generation_id = 'g', latest_test_result = 'passed'", "completed|x|g|passed|1")]
    [InlineData("last_stream_version = 5", "created|-|-|-|5")]
    [InlineData("status = 'assigned', assigned_agent_id = 'x'", "assigned|x|-|-|1")]
    [InlineData("status = 'assigned', assigned_agent_id = 'x', current_generation_id = 'g', last_stream_version = 2", "assigned|x|g|-|2")]
    [InlineData("status = 'assigned', assigned_agent_id = 'x', current_generation_id = 'g', latest_test_result = 'failed', last_stream_version = 3", "assigned|x|g|failed|3")]
    [InlineData("status = 'completed', assigned_agent_id = 'x', current_generation_id = 'g', latest_test_result = 'passed', last_stream_version = 4", "completed|x|g|passed|4")]
    public async Task An_impossible_existing_row_fails_the_next_append_and_rolls_back_event_and_projection(
        string updateSet, string rowAfterUpdate)
    {
        var task = NewTask();
        await Run(Store, new CreateTask(task), 0);
        await AdminAsync($"UPDATE eventforge.task_overview SET {updateSet} WHERE task_id = @id", task.Value);
        Assert.Equal(rowAfterUpdate, await OverviewAsync(task.Value)); // the database really accepted the impossible row

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Run(Store, new AssignTask(task, Agent), 1));

        Assert.Equal(TaskOverviewFault.InvalidRow, exception.Fault);
        Assert.Equal(1, await EventCountAsync(task.Value)); // the new event was rolled back
        Assert.Equal(rowAfterUpdate, await OverviewAsync(task.Value)); // and the row was not touched
    }

    // ---- Atomic rollback: a real failure inside the append transaction ------------------------------------------------
    //
    // A trigger on task_overview, created by the administrator in a scratch database, raises after the event inserts
    // have run in the same transaction. Its message reports how many events were visible inside that transaction, which
    // proves the events existed there before the failure. Afterwards nothing may be visible to anyone.

    private const string FailureFunctionSql = """
        CREATE FUNCTION eventforge.fail_projection() RETURNS trigger LANGUAGE plpgsql AS $f$
        BEGIN
          IF TG_ARGV[0] = 'sleep' THEN
            PERFORM pg_sleep(30);
          END IF;
          RAISE EXCEPTION 'forced projection failure; events visible in transaction: %',
            (SELECT count(*) FROM eventforge.events WHERE stream_id = NEW.task_id);
        END
        $f$
        """;

    private static Task InstallFailureAsync(string adminConnectionString, string timing, string argument) =>
        ExecuteAsync(adminConnectionString,
            FailureFunctionSql + "; " +
            $"CREATE TRIGGER task_overview_fail {timing} INSERT OR UPDATE ON eventforge.task_overview " +
            $"FOR EACH ROW EXECUTE FUNCTION eventforge.fail_projection('{argument}')");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await PostgresFixture.ExecuteAsync(connection, sql);
    }

    private async Task WithScratchStoreAsync(Func<PostgreSqlEventStore, string, Task> test)
    {
        var name = PostgresFixture.NewDatabaseName();
        PostgreSqlDatabase? database = null;
        try
        {
            await fixture.CreateProvisionedDatabaseAsync(name);
            await PostgresFixture.RunMigrationAsync(fixture.ConnectionStringFor("eventforge_migrator", name));
            database = PostgreSqlDatabase.Create(fixture.ConnectionStringFor("eventforge_app", name));
            await test(new PostgreSqlEventStore(database), fixture.AdminConnectionString(name));
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync();
            }

            await fixture.DropDatabaseAsync(name);
        }
    }

    [PostgresTheory]
    [InlineData("BEFORE")] // fails when the row is about to be written
    [InlineData("AFTER")] // fails after the row was written as well
    public async Task A_projection_failure_for_a_new_task_leaves_neither_event_nor_row(string timing)
    {
        await WithScratchStoreAsync(async (store, admin) =>
        {
            var task = NewTask();
            await InstallFailureAsync(admin, timing, "fail");

            var exception = await Assert.ThrowsAsync<PostgresException>(() => Run(store, new CreateTask(task), 0));

            Assert.Equal("P0001", exception.SqlState);
            Assert.Contains("events visible in transaction: 1", exception.MessageText);
            Assert.Equal(0, await EventCountAtAsync(admin, task.Value));
            Assert.Null(await OverviewAtAsync(admin, task.Value));
        });
    }

    [PostgresTheory]
    [InlineData("BEFORE")]
    [InlineData("AFTER")]
    public async Task A_projection_failure_for_an_existing_task_keeps_the_previous_event_stream_and_row(string timing)
    {
        await WithScratchStoreAsync(async (store, admin) =>
        {
            var task = NewTask();
            Assert.IsType<CommandSucceeded>(await Run(store, new CreateTask(task), 0));
            await InstallFailureAsync(admin, timing, "fail");

            var exception = await Assert.ThrowsAsync<PostgresException>(() => Run(store, new AssignTask(task, Agent), 1));

            Assert.Contains("events visible in transaction: 2", exception.MessageText);
            Assert.Equal(1, await EventCountAtAsync(admin, task.Value));
            Assert.Equal("created|-|-|-|1", await OverviewAtAsync(admin, task.Value));

            // The rolled-back attempt left nothing behind: once the fault is gone the same command succeeds.
            await ExecuteAsync(admin, "DROP TRIGGER task_overview_fail ON eventforge.task_overview");
            Assert.IsType<CommandSucceeded>(await Run(store, new AssignTask(task, Agent), 1));
            Assert.Equal("assigned|agent-1|-|-|2", await OverviewAtAsync(admin, task.Value));
        });
    }

    [PostgresFact]
    public async Task A_cancellation_while_the_projection_write_is_running_rolls_back_both_writes()
    {
        await WithScratchStoreAsync(async (store, admin) =>
        {
            var task = NewTask();
            await InstallFailureAsync(admin, "AFTER", "sleep"); // the row insert blocks for 30 s inside the transaction
            using var cancellation = new CancellationTokenSource();
            var started = System.Diagnostics.Stopwatch.StartNew();

            var append = Handler(store).HandleAsync(new CreateTask(task), 0, "evf37", cancellation.Token);

            // Cancel explicitly, and only once the projection INSERT is provably executing in PostgreSQL, so the
            // cancellation cannot land before the events were written or after the commit.
            await WaitUntilAsync(admin, "SELECT count(*) FROM pg_stat_activity WHERE state = 'active' " +
                "AND query LIKE 'INSERT INTO eventforge.task_overview%' AND pid <> pg_backend_pid()");
            Assert.False(append.IsCompleted);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => append);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.IsNotType<EventStoreCommitUnknownException>(exception); // a rollback, not an unknown commit outcome
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(25), "The 30 s sleep must have been interrupted, not awaited.");
            Assert.Equal(0, await EventCountAtAsync(admin, task.Value));
            Assert.Null(await OverviewAtAsync(admin, task.Value));

            // Nothing is left locked or half-written: without the fault the same command now succeeds.
            await ExecuteAsync(admin, "DROP TRIGGER task_overview_fail ON eventforge.task_overview");
            Assert.IsType<CommandSucceeded>(await Run(store, new CreateTask(task), 0));
            Assert.Equal("created|-|-|-|1", await OverviewAtAsync(admin, task.Value));
        });
    }

    private static async Task WaitUntilAsync(string adminConnectionString, string countSql)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand(countSql, connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The expected statement never became active in PostgreSQL.");
    }

    [PostgresFact]
    public async Task A_token_cancelled_before_the_append_writes_nothing()
    {
        var task = NewTask();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(Store).HandleAsync(new CreateTask(task), 0, "evf37", cancellation.Token));

        Assert.Equal(0, await EventCountAsync(task.Value));
        Assert.Null(await OverviewAsync(task.Value));
    }

    // ---- Task streams cannot be written without their projection ------------------------------------------------------

    [PostgresFact]
    public async Task A_directly_constructed_store_projects_a_plain_Task_append()
    {
        var task = NewTask();
        var created = TaskEventMapper.ToNewEvent(new TaskCreated(task), Guid.NewGuid(), JsonDocument.Parse("{}").RootElement.Clone());

        var result = await new PostgreSqlEventStore(_database!).AppendAsync(
            new StreamKey("Task", task.Value), 0, [created], CancellationToken.None);

        Assert.True(result.IsAppended);
        Assert.Equal("created|-|-|-|1", await OverviewAsync(task.Value));
    }

    [PostgresTheory]
    [InlineData("TaskCreated", """{"n":1}""")] // a known type with an invalid payload
    [InlineData("Whatever", """{}""")] // an unknown event type
    public async Task A_Task_stream_accepts_only_events_it_can_project_and_writes_nothing_otherwise(string eventType, string payload)
    {
        var task = NewTask();
        var arbitrary = new NewEvent(
            Guid.NewGuid(), eventType, 1, JsonDocument.Parse(payload).RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone());

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Store.AppendAsync(new StreamKey("Task", task.Value), 0, [arbitrary], CancellationToken.None));

        Assert.Equal(TaskOverviewFault.InvalidEvent, exception.Fault);
        Assert.Equal(0, await EventCountAsync(task.Value));
        Assert.Null(await OverviewAsync(task.Value));
    }

    [PostgresFact]
    public async Task A_direct_Task_append_to_a_stream_without_a_row_is_refused_and_writes_no_event()
    {
        var task = NewTask();
        await AdminAsync(
            "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
            "payload, metadata, recorded_at) VALUES (gen_random_uuid(), 'Task', @id, 1, 'TaskCreated', 1, " +
            "jsonb_build_object('taskId', @id::text), '{}'::jsonb, now())", task.Value);
        var assigned = TaskEventMapper.ToNewEvent(new TaskAssigned(Agent), Guid.NewGuid(), JsonDocument.Parse("{}").RootElement.Clone());

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Store.AppendAsync(new StreamKey("Task", task.Value), 1, [assigned], CancellationToken.None));

        Assert.Equal(TaskOverviewFault.MissingRow, exception.Fault);
        Assert.Equal(1, await EventCountAsync(task.Value));
        Assert.Null(await OverviewAsync(task.Value));
    }

    // ---- Concurrency ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Racing_writers_on_the_same_expected_version_have_one_winner_and_the_row_follows_the_winner()
    {
        for (var round = 0; round < 10; round++)
        {
            var task = NewTask();
            await Run(Store, new CreateTask(task), 0);
            await Run(Store, new AssignTask(task, Agent), 1);

            var results = await Task.WhenAll(
                Task.Run(() => Run(Store, new RecordCodeGenerated(task, G1, Art), 2)),
                Task.Run(() => Run(Store, new RecordCodeGenerated(task, G2, Art), 2)));

            Assert.Single(results, r => r is CommandSucceeded);
            Assert.Single(results, r => r is VersionConflict);
            var winner = results[0] is CommandSucceeded ? "g1" : "g2";
            Assert.Equal(3, await EventCountAsync(task.Value));
            Assert.Equal($"assigned|agent-1|{winner}|-|3", await OverviewAsync(task.Value));
            Assert.Equal(await ReferenceAsync(task), await OverviewAsync(task.Value));
        }
    }

    [PostgresFact]
    public async Task Racing_creators_of_the_same_task_have_one_winner_and_one_row()
    {
        for (var round = 0; round < 10; round++)
        {
            var task = NewTask();

            var results = await Task.WhenAll(
                Task.Run(() => Run(Store, new CreateTask(task), 0)),
                Task.Run(() => Run(Store, new CreateTask(task), 0)));

            Assert.Single(results, r => r is CommandSucceeded);
            Assert.Single(results, r => r is VersionConflict);
            Assert.Equal(1, await EventCountAsync(task.Value));
            Assert.Equal("created|-|-|-|1", await OverviewAsync(task.Value));
        }
    }

    // ---- Generic store -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_non_task_stream_through_the_production_store_is_appended_without_any_projection()
    {
        var id = Guid.NewGuid();
        var payload = JsonDocument.Parse("""{"n":1}""").RootElement.Clone();
        var e = new NewEvent(Guid.NewGuid(), "Something", 1, payload, JsonDocument.Parse("{}").RootElement.Clone());

        var result = await Store.AppendAsync(new StreamKey("Other", id), 0, [e], CancellationToken.None);

        Assert.True(result.IsAppended);
        Assert.Equal(1, await EventCountAsync(id, "Other"));
        Assert.Null(await OverviewAsync(id));
    }

    [PostgresFact]
    public async Task Stale_versions_and_duplicate_event_ids_are_decided_before_the_projection_is_reached()
    {
        await WithScratchStoreAsync(async (store, admin) =>
        {
            var fixedEventId = Guid.NewGuid();
            var first = NewTask();
            var second = NewTask();
            var sameIdHandler = new TaskCommandHandler(store, () => fixedEventId);
            Assert.IsType<CommandSucceeded>(await sameIdHandler.HandleAsync(new CreateTask(first), 0, null, CancellationToken.None));
            await InstallFailureAsync(admin, "BEFORE", "fail"); // any projection from here on would raise

            var stale = await Run(store, new AssignTask(first, Agent), 0);
            var duplicate = await sameIdHandler.HandleAsync(new CreateTask(second), 0, null, CancellationToken.None);

            Assert.IsType<VersionConflict>(stale);
            Assert.IsType<DuplicateEventId>(duplicate);
            Assert.Equal(0, await EventCountAtAsync(admin, second.Value));
            Assert.Equal("created|-|-|-|1", await OverviewAtAsync(admin, first.Value));
        });
    }
}

/// <summary>The construction surface of the store: no configuration can leave Task appends unprojected.</summary>
public sealed class PostgreSqlEventStoreConstructionTests
{
    [Fact]
    public void The_store_has_one_public_constructor_that_takes_only_the_database()
    {
        var constructor = Assert.Single(typeof(PostgreSqlEventStore).GetConstructors());

        Assert.Equal([typeof(PostgreSqlDatabase)], constructor.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void The_projector_is_not_part_of_the_public_api_surface()
    {
        var exported = typeof(PostgreSqlEventStore).Assembly.GetExportedTypes().Select(t => t.Name);

        Assert.DoesNotContain(exported, name => name.Contains("Projector", StringComparison.Ordinal));
    }

    [Fact]
    public void The_store_exposes_no_member_that_takes_a_projector_or_skips_projection()
    {
        var members = typeof(PostgreSqlEventStore).GetMembers()
            .Where(m => m.DeclaringType == typeof(PostgreSqlEventStore))
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal([".ctor", "AppendAsync", "LoadStreamAsync"], members);
    }
}
