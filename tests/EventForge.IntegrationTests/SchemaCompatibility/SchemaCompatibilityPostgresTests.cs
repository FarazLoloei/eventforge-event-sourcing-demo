using System.Net;
using System.Text.Json;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.IntegrationTests.Api;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;
using Npgsql;
using NpgsqlTypes;
using static EventForge.IntegrationTests.SchemaCompatibility.VersionedHistoryFixture;

namespace EventForge.IntegrationTests.SchemaCompatibility;

/// <summary>
/// EVF-43: <c>CodeGenerated</c> schema compatibility against the isolated, disposable PostgreSQL only. Skipped unless
/// explicitly enabled (see <see cref="PostgresTestGuard"/>); a skipped run proves nothing. Historical rows are seeded
/// with the administrative connection from the hand-written fixtures, together with the <c>task_overview</c> row a
/// previous release would have projected. Immutability is shown by comparing envelope fields exactly and the JSONB
/// payload and metadata by semantic PostgreSQL JSONB equality, never by text or bytes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class SchemaCompatibilityPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private sealed record Envelope(
        Guid EventId, string StreamType, Guid StreamId, int Version, string EventType, int SchemaVersion, DateTimeOffset RecordedAt);

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

    private Task<TaskCommandResult> Run(TaskCommand command, int expectedVersion) =>
        new TaskCommandHandler(Store, Guid.NewGuid).HandleAsync(command, expectedVersion, "evf43", CancellationToken.None);

    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Seeds the first <paramref name="count"/> hand-written events and, optionally, the matching projection row.</summary>
    private async Task SeedAsync(Guid id, int first, int second, int count, bool withOverviewRow = true)
    {
        await using var connection = await OpenAdminAsync();
        foreach (var e in Build(first, second, id).Take(count))
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, " +
                "payload, metadata, recorded_at) VALUES (@e, 'Task', @s, @v, @t, @sv, @p, @m, @r)", connection);
            // event_id is the table's global primary key, and every test seeds its own stream in the same shared
            // database, so the fixture's fixed per-position ids would collide. Each seeded row gets a fresh id; the tests
            // compare identities captured from the database before and after each operation, not the fixture's ids.
            insert.Parameters.Add("e", NpgsqlDbType.Uuid).Value = Guid.NewGuid();
            insert.Parameters.Add("s", NpgsqlDbType.Uuid).Value = id;
            insert.Parameters.Add("v", NpgsqlDbType.Integer).Value = e.StreamVersion;
            insert.Parameters.Add("t", NpgsqlDbType.Text).Value = e.EventType;
            insert.Parameters.Add("sv", NpgsqlDbType.Integer).Value = e.SchemaVersion;
            insert.Parameters.Add("p", NpgsqlDbType.Jsonb).Value = e.Payload.GetRawText();
            insert.Parameters.Add("m", NpgsqlDbType.Jsonb).Value = e.Metadata.GetRawText();
            insert.Parameters.Add("r", NpgsqlDbType.TimestampTz).Value = e.RecordedAt;
            await insert.ExecuteNonQueryAsync();
        }

        if (!withOverviewRow)
        {
            return;
        }

        var expected = Expected(count);
        await using var row = new NpgsqlCommand(
            "INSERT INTO eventforge.task_overview (task_id, status, assigned_agent_id, current_generation_id, " +
            "latest_test_result, last_stream_version) VALUES (@id, @s, @a, @g, @r, @v)", connection);
        row.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        row.Parameters.Add("s", NpgsqlDbType.Text).Value = expected.Status.ToString().ToLowerInvariant();
        row.Parameters.Add("a", NpgsqlDbType.Text).Value = (object?)expected.Agent ?? DBNull.Value;
        row.Parameters.Add("g", NpgsqlDbType.Text).Value = (object?)expected.CurrentGeneration ?? DBNull.Value;
        row.Parameters.Add("r", NpgsqlDbType.Text).Value = expected.Result is { } r ? r.ToString().ToLowerInvariant() : DBNull.Value;
        row.Parameters.Add("v", NpgsqlDbType.Integer).Value = expected.Version;
        await row.ExecuteNonQueryAsync();
    }

    private async Task<List<Envelope>> EnvelopesAsync(Guid id, int upTo = int.MaxValue)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "SELECT event_id, stream_type, stream_id, stream_version, event_type, schema_version, recorded_at " +
            "FROM eventforge.events WHERE stream_type = 'Task' AND stream_id = @id AND stream_version <= @n ORDER BY stream_version",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("n", NpgsqlDbType.Integer).Value = upTo;
        var rows = new List<Envelope>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Envelope(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetString(4),
                reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    /// <summary>Semantic JSONB equality, evaluated by PostgreSQL, of the stored payload and metadata against the originals.</summary>
    private async Task<bool> JsonbEqualsAsync(Guid id, int version, string payload, string metadata)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload = @p AND metadata = @m FROM eventforge.events " +
            "WHERE stream_type = 'Task' AND stream_id = @id AND stream_version = @v", connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("v", NpgsqlDbType.Integer).Value = version;
        command.Parameters.Add("p", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.Add("m", NpgsqlDbType.Jsonb).Value = metadata;
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The six committed <c>task_overview</c> columns exactly as stored; SQL NULL is C# null.</summary>
    private sealed record OverviewRow(
        Guid TaskId, string Status, string? AssignedAgentId, string? CurrentGenerationId, string? LatestTestResult, int LastStreamVersion);

    /// <summary>The committed row, or null when no row exists.</summary>
    private async Task<OverviewRow?> ReadOverviewAsync(Guid id)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "SELECT task_id, status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version " +
            "FROM eventforge.task_overview WHERE task_id = @id",
            connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new OverviewRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5));
    }

    /// <summary>Asserts each of the six stored fields against the hand-authored expectation for that stream version.</summary>
    private async Task AssertOverviewAsync(Guid id, int version)
    {
        var expected = Expected(version);

        var row = await ReadOverviewAsync(id);

        Assert.NotNull(row);
        Assert.Equal(id, row.TaskId);
        Assert.Equal(expected.Status.ToString().ToLowerInvariant(), row.Status);
        Assert.Equal(expected.Agent, row.AssignedAgentId);
        Assert.Equal(expected.CurrentGeneration, row.CurrentGenerationId);
        Assert.Equal(expected.Result?.ToString().ToLowerInvariant(), row.LatestTestResult);
        Assert.Equal(expected.Version, row.LastStreamVersion);
    }

    private async Task AssertHistoricalRowsUnchangedAsync(Guid id, int first, int second, List<Envelope> before, int count)
    {
        var after = await EnvelopesAsync(id, count);
        Assert.Equal(before, after); // envelope fields exactly: ids, stream identity, versions, types, schema versions, times

        var lifecycle = Lifecycle(id, first, second);
        for (var i = 0; i < count; i++)
        {
            Assert.True(
                await JsonbEqualsAsync(id, i + 1, lifecycle[i].Payload, Metadata),
                $"The payload or metadata of historical event {i + 1} changed.");
        }
    }

    // ---- Historical v1 history continues under v2 support ---------------------------------------------------------------

    [PostgresFact]
    public async Task Historical_v1_events_are_read_and_continued_with_v2_inside_the_inline_projection_without_being_rewritten()
    {
        var id = Guid.NewGuid();
        await SeedAsync(id, first: 1, second: 1, count: 4); // CodeGenerated g1 stored as v1, projection at version 4
        var before = await EnvelopesAsync(id);
        Assert.Equal([1, 1, 1, 1], before.Select(e => e.SchemaVersion));

        // Reading with v2 support enabled: the historical v1 events fold to the independently specified state.
        var loaded = await Store.LoadStreamAsync(new StreamKey("Task", id), CancellationToken.None);
        var historical = TaskAggregate.Rehydrate(loaded.Events.Select(TaskEventMapper.FromStored));
        Assert.Equal(WorkflowStatus.Assigned, historical.Status);
        Assert.Equal("g1", historical.CurrentGeneration?.Value);
        Assert.Equal(TestOutcome.Failed, historical.CurrentTestOutcome);
        Assert.Equal(4, historical.Version);

        // New CodeGenerated is persisted as v2 and updates task_overview in the same transaction.
        var generated = await Run(new RecordCodeGenerated(new TaskId(id), new GenerationId("g2"), new ArtifactRef(ArtifactG2)), 4);
        Assert.Equal(5, Assert.IsType<CommandSucceeded>(generated).NewVersion);
        await AssertOverviewAsync(id, 5);
        Assert.Equal([1, 1, 1, 1, 2], (await EnvelopesAsync(id)).Select(e => e.SchemaVersion));
        Assert.True(await JsonbEqualsAsync(id, 5, GeneratedPayload("g2", ArtifactG2, 2), """{"correlationId":"evf43"}"""));

        // Other events keep writing v1.
        Assert.IsType<CommandSucceeded>(await Run(new RecordTestsPassed(new TaskId(id), new GenerationId("g2")), 5));
        await AssertOverviewAsync(id, 6);
        Assert.IsType<CommandSucceeded>(await Run(new CompleteTask(new TaskId(id)), 6));
        await AssertOverviewAsync(id, 7);
        Assert.Equal([1, 1, 1, 1, 2, 1, 1], (await EnvelopesAsync(id)).Select(e => e.SchemaVersion));

        // The final mixed v1/v2 stream folds to the independently specified final state.
        var final = await Store.LoadStreamAsync(new StreamKey("Task", id), CancellationToken.None);
        var state = TaskAggregate.Rehydrate(final.Events.Select(TaskEventMapper.FromStored));
        Assert.Equal(7, final.Version);
        Assert.Equal(WorkflowStatus.Completed, state.Status);
        Assert.Equal("g2", state.CurrentGeneration?.Value);
        Assert.Equal(TestOutcome.Passed, state.CurrentTestOutcome);
        Assert.Equal(["g1", "g2"], state.UsedGenerations.Select(g => g.Value).Order(StringComparer.Ordinal));
        Assert.Equal(
            [ArtifactG1, ArtifactG2],
            final.Events.Select(TaskEventMapper.FromStored).OfType<CodeGenerated>().Select(e => e.ArtifactRef.Value));

        // Historical rows 1-4 were never updated or replaced.
        await AssertHistoricalRowsUnchangedAsync(id, 1, 1, before, 4);
    }

    [PostgresTheory]
    [MemberData(nameof(VersionedHistoryFixture.Encodings), MemberType = typeof(VersionedHistoryFixture))]
    public async Task Every_encoding_reads_identically_over_HTTP_and_reads_never_modify_rows(int first, int second)
    {
        var id = Guid.NewGuid();
        await SeedAsync(id, first, second, count: 7);
        var before = await EnvelopesAsync(id);
        using var factory = new ApiFactory(fakeStore: false, connectionString: fixture.AppConnectionString);
        var client = factory.CreateClient();

        var history = await client.GetAsync($"/api/tasks/{id}/events");
        var text = await history.Content.ReadAsStringAsync();
        var state = await client.GetAsync($"/api/tasks/{id}");
        var stateBody = JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var body = JsonDocument.Parse(text).RootElement;
        var events = body.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(7, body.GetProperty("version").GetInt32());
        Assert.Equal([1, 1, first, 1, second, 1, 1], events.Select(e => e.GetProperty("schemaVersion").GetInt32()));
        Assert.Equal($$"""{"generationId":"g1","artifactRef":"{{ArtifactG1}}"}""", events[2].GetProperty("data").GetRawText());
        Assert.Equal($$"""{"generationId":"g2","artifactRef":"{{ArtifactG2}}"}""", events[4].GetProperty("data").GetRawText());
        Assert.DoesNotContain("artifactReference", text);
        Assert.DoesNotContain("evf43-fixture", text);

        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal("completed", stateBody.GetProperty("status").GetString());
        Assert.Equal("g2", stateBody.GetProperty("currentGenerationId").GetString());
        Assert.Equal("passed", stateBody.GetProperty("currentTestOutcome").GetString());
        Assert.Equal(7, stateBody.GetProperty("version").GetInt32());

        await AssertHistoricalRowsUnchangedAsync(id, first, second, before, 7);
        await AssertOverviewAsync(id, 7);
    }

    // ---- Atomicity and fail-closed behavior with v2 events ----------------------------------------------------------------

    [PostgresFact]
    public async Task A_failed_projection_rolls_back_a_new_v2_CodeGenerated_append_entirely()
    {
        var id = Guid.NewGuid();
        await SeedAsync(id, first: 1, second: 1, count: 3, withOverviewRow: false); // stream without a projection row
        var before = await EnvelopesAsync(id);

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Run(new RecordCodeGenerated(new TaskId(id), new GenerationId("g2"), new ArtifactRef(ArtifactG2)), 3));

        Assert.Equal(TaskOverviewFault.MissingRow, exception.Fault);
        Assert.Equal(before, await EnvelopesAsync(id)); // no event 4, no v2 row
        Assert.Null(await ReadOverviewAsync(id)); // the overview row must remain absent
    }

    [PostgresFact]
    public async Task A_batch_with_a_v2_CodeGenerated_and_an_invalid_transition_rolls_back_every_event_and_the_row()
    {
        var id = new TaskId(Guid.NewGuid());
        var metadata = JsonDocument.Parse("{}").RootElement.Clone();
        NewEvent[] batch =
        [
            TaskEventMapper.ToNewEvent(new TaskCreated(id), Guid.NewGuid(), metadata),
            TaskEventMapper.ToNewEvent(new CodeGenerated(new GenerationId("g1"), new ArtifactRef(ArtifactG1)), Guid.NewGuid(), metadata), // not assigned yet
        ];
        Assert.Equal(2, batch[1].SchemaVersion);

        var exception = await Assert.ThrowsAsync<TaskOverviewProjectionException>(() =>
            Store.AppendAsync(new StreamKey("Task", id.Value), 0, batch, CancellationToken.None));

        Assert.Equal(TaskOverviewFault.InvalidTransition, exception.Fault);
        Assert.Empty(await EnvelopesAsync(id.Value));
        Assert.Null(await ReadOverviewAsync(id.Value));
    }

    [PostgresFact]
    public async Task A_stored_unsupported_CodeGenerated_version_refuses_the_command_and_appends_nothing()
    {
        var id = Guid.NewGuid();
        await SeedAsync(id, first: 2, second: 2, count: 3);
        await using (var connection = await OpenAdminAsync())
        {
            // Test setup only (administrative connection): simulate a stored version this release does not support.
            await using var damage = new NpgsqlCommand(
                "UPDATE eventforge.events SET schema_version = 3 WHERE stream_id = @id AND stream_version = 3", connection);
            damage.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
            await damage.ExecuteNonQueryAsync();
        }

        var before = await EnvelopesAsync(id);

        var result = await Run(new RecordTestsPassed(new TaskId(id), new GenerationId("g1")), 3);

        Assert.Equal(new InvalidHistory(InvalidHistoryReason.UnsupportedSchemaVersion, 3), result);
        Assert.Equal(before, await EnvelopesAsync(id));
    }

    [PostgresFact]
    public async Task A_stale_expected_version_is_still_a_conflict_that_appends_nothing_on_a_mixed_history()
    {
        var id = Guid.NewGuid();
        await SeedAsync(id, first: 1, second: 2, count: 5); // CodeGenerated g1 stored as v1, g2 stored as v2
        var before = await EnvelopesAsync(id);
        Assert.Equal([1, 1, 1, 1, 2], before.Select(e => e.SchemaVersion));

        // A stale expected version (4, while the stream is at 5) with a valid new generation id, so a duplicate-generation
        // rejection cannot mask the concurrency result.
        var result = await Run(new RecordCodeGenerated(new TaskId(id), new GenerationId("g3"), new ArtifactRef("synthetic://artifacts/evf43/g3")), 4);

        Assert.Equal(new VersionConflict(5), result);
        Assert.Equal(before, await EnvelopesAsync(id)); // nothing appended, every envelope unchanged
        await AssertHistoricalRowsUnchangedAsync(id, 1, 2, before, 5);
        await AssertOverviewAsync(id, 5);
    }
}
