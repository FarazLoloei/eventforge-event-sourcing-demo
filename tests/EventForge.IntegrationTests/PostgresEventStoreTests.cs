using System.Text.Json;
using System.Text.Json.Nodes;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;
using Npgsql;

namespace EventForge.IntegrationTests;

/// <summary>Event store behaviour against a real, isolated PostgreSQL 17. Skipped unless explicitly enabled.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class PostgresEventStoreTests(PostgresFixture fixture) : IAsyncLifetime
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

    private static StreamKey NewStream(string type = "Task") => new(type, Guid.NewGuid());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static NewEvent Event(string type = "TaskCreated", string payload = """{"n":1}""", string metadata = """{"actor":"test"}""") =>
        new(Guid.NewGuid(), type, 1, Json(payload), Json(metadata));

    private static bool SameJson(JsonElement a, JsonElement b) =>
        JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));

    [PostgresFact]
    public async Task LoadStream_OfUnknownStream_IsEmptyAtVersionZero()
    {
        var loaded = await Store.LoadStreamAsync(NewStream(), CancellationToken.None);

        Assert.Equal(0, loaded.Version);
        Assert.Empty(loaded.Events);
    }

    [PostgresFact]
    public async Task Append_ToNewStream_WithExpectedVersionZero_PersistsFromVersionOne()
    {
        var stream = NewStream();
        var created = Event();

        var result = await Store.AppendAsync(stream, 0, [created], CancellationToken.None);

        Assert.Equal(AppendOutcome.Appended, result.Outcome);
        Assert.Equal(1, result.CurrentVersion);
        var stored = Assert.Single(result.Events);
        Assert.Equal(1, stored.StreamVersion);
        Assert.Equal(created.EventId, stored.EventId);
        Assert.NotEqual(default, stored.RecordedAt);
        Assert.True(stored.RecordedAt < DateTimeOffset.UtcNow.AddMinutes(5), "recorded_at should be a plausible server time");

        var loaded = await Store.LoadStreamAsync(stream, CancellationToken.None);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(stored.RecordedAt, Assert.Single(loaded.Events).RecordedAt);
    }

    [PostgresFact]
    public async Task Load_ReturnsEventsInVersionOrder_AndVersionsIncrementAcrossAppends()
    {
        var stream = NewStream();
        var first = Event("TaskCreated");
        var second = Event("TaskAssigned");
        var third = Event("CodeGenerated");

        Assert.True((await Store.AppendAsync(stream, 0, [first], CancellationToken.None)).IsAppended);
        var next = await Store.AppendAsync(stream, 1, [second, third], CancellationToken.None);
        Assert.Equal(3, next.CurrentVersion);

        var loaded = await Store.LoadStreamAsync(stream, CancellationToken.None);
        Assert.Equal(3, loaded.Version);
        Assert.Equal(new[] { 1, 2, 3 }, loaded.Events.Select(e => e.StreamVersion));
        Assert.Equal(new[] { first.EventId, second.EventId, third.EventId }, loaded.Events.Select(e => e.EventId));
        Assert.All(loaded.Events, e => Assert.Equal(stream.StreamId, e.StreamId));
    }

    [PostgresFact]
    public async Task Append_OfSeveralEvents_AssignsContiguousVersionsAndSharesOneTransactionTime()
    {
        var stream = NewStream();

        var result = await Store.AppendAsync(stream, 0, [Event(), Event("TaskAssigned"), Event("CodeGenerated")], CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, result.Events.Select(e => e.StreamVersion));
        Assert.Single(result.Events.Select(e => e.RecordedAt).Distinct());
        Assert.Equal(3, (await Store.LoadStreamAsync(stream, CancellationToken.None)).Version);
    }

    [PostgresFact]
    public async Task PayloadAndMetadata_RoundTripAsJsonb()
    {
        var stream = NewStream();
        var payload = """{"generationId":"g1","nested":{"list":[1,2.5,"x",null,true],"unicode":"café ✓"},"empty":{}}""";
        var metadata = """{"correlationId":"demo-001","actorType":"SimulatedAgent"}""";

        var appended = await Store.AppendAsync(stream, 0, [Event("CodeGenerated", payload, metadata)], CancellationToken.None);
        var loaded = Assert.Single((await Store.LoadStreamAsync(stream, CancellationToken.None)).Events);

        // JSONB normalises key order and whitespace, so compare structure, not text.
        Assert.True(SameJson(Json(payload), loaded.Payload));
        Assert.True(SameJson(Json(metadata), loaded.Metadata));
        Assert.True(SameJson(appended.Events[0].Payload, loaded.Payload));
        Assert.Equal("CodeGenerated", loaded.EventType);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [PostgresFact]
    public async Task Streams_AreIndependent()
    {
        var one = NewStream();
        var other = NewStream();
        var sameIdOtherType = new StreamKey("Other", one.StreamId);

        Assert.True((await Store.AppendAsync(one, 0, [Event(), Event()], CancellationToken.None)).IsAppended);
        Assert.True((await Store.AppendAsync(other, 0, [Event()], CancellationToken.None)).IsAppended);
        Assert.True((await Store.AppendAsync(sameIdOtherType, 0, [Event()], CancellationToken.None)).IsAppended);

        Assert.Equal(2, (await Store.LoadStreamAsync(one, CancellationToken.None)).Version);
        Assert.Equal(1, (await Store.LoadStreamAsync(other, CancellationToken.None)).Version);
        Assert.Equal(1, (await Store.LoadStreamAsync(sameIdOtherType, CancellationToken.None)).Version);
    }

    [PostgresFact]
    public async Task Append_WithStaleOrFutureExpectedVersion_ConflictsAndAppendsNothing()
    {
        var stream = NewStream();
        Assert.True((await Store.AppendAsync(stream, 0, [Event()], CancellationToken.None)).IsAppended);

        var stale = await Store.AppendAsync(stream, 0, [Event()], CancellationToken.None);
        var future = await Store.AppendAsync(stream, 5, [Event()], CancellationToken.None);

        Assert.Equal(AppendOutcome.VersionConflict, stale.Outcome);
        Assert.Equal(1, stale.CurrentVersion); // observed inside the append transaction
        Assert.Empty(stale.Events);
        Assert.Equal(AppendOutcome.VersionConflict, future.Outcome);
        Assert.Equal(1, (await Store.LoadStreamAsync(stream, CancellationToken.None)).Version);
    }

    [PostgresFact]
    public async Task ConcurrentWritersAtTheSameVersion_ExactlyOneWins()
    {
        const int writers = 8;
        var stream = NewStream();
        var start = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, writers).Select(async i =>
        {
            await start.Task;
            return await Store.AppendAsync(stream, 0, [Event("TaskCreated", "{\"writer\":" + i + "}"), Event("TaskAssigned")], CancellationToken.None);
        }).ToList();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Outcome == AppendOutcome.Appended);
        Assert.Equal(writers - 1, results.Count(r => r.Outcome == AppendOutcome.VersionConflict));
        var loaded = await Store.LoadStreamAsync(stream, CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, loaded.Events.Select(e => e.StreamVersion)); // the winner's whole batch, nothing else
    }

    [PostgresFact]
    public async Task Append_WithAnExistingEventId_ReportsDuplicateAndRollsBackTheWholeBatch()
    {
        var existing = Event();
        Assert.True((await Store.AppendAsync(NewStream(), 0, [existing], CancellationToken.None)).IsAppended);

        var target = NewStream();
        var fresh = Event("TaskAssigned");
        var result = await Store.AppendAsync(target, 0, [fresh, existing], CancellationToken.None);

        Assert.Equal(AppendOutcome.DuplicateEventId, result.Outcome);
        Assert.Null(result.CurrentVersion);
        Assert.Equal(0, (await Store.LoadStreamAsync(target, CancellationToken.None)).Version); // `fresh` was rolled back too
        Assert.True((await Store.AppendAsync(target, 0, [fresh], CancellationToken.None)).IsAppended); // and is reusable
    }

    [PostgresFact]
    public async Task GappedHistory_IsRejectedByLoadAndNeverExtendedByAppend()
    {
        var gapped = NewStream();
        var wrongStart = NewStream();
        await InsertRawAsync(gapped, 1, 3);
        await InsertRawAsync(wrongStart, 2);

        foreach (var stream in new[] { gapped, wrongStart })
        {
            await Assert.ThrowsAsync<EventStreamCorruptedException>(() => Store.LoadStreamAsync(stream, CancellationToken.None));
            await Assert.ThrowsAsync<EventStreamCorruptedException>(() =>
                Store.AppendAsync(stream, 3, [Event()], CancellationToken.None));
        }

        Assert.Equal(2L, await CountAsync(gapped));
        Assert.Equal(1L, await CountAsync(wrongStart));
    }

    // Inserts rows with the given versions as the superuser, bypassing the store, to simulate damaged history.
    private async Task InsertRawAsync(StreamKey stream, params int[] versions)
    {
        var rows = string.Join(", ", versions.Select(v =>
            $"('{Guid.NewGuid()}', '{stream.StreamType}', '{stream.StreamId}', {v}, 'TaskCreated', 1, '{{}}', '{{}}', now())"));
        await fixture.ExecuteAsAdminAsync(fixture.DatabaseName,
            "INSERT INTO eventforge.events (event_id, stream_type, stream_id, stream_version, event_type, schema_version, payload, metadata, recorded_at) VALUES " + rows);
    }

    private async Task<long> CountAsync(StreamKey stream)
    {
        await using var connection = new NpgsqlConnection(fixture.AdminConnectionString(fixture.DatabaseName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM eventforge.events WHERE stream_id = @id", connection);
        command.Parameters.AddWithValue("id", stream.StreamId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
