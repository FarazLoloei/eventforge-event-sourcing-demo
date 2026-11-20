using System.Text.Json;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;

namespace EventForge.IntegrationTests;

/// <summary>Contract, argument and test-guard checks. None of these connects to PostgreSQL.</summary>
public sealed class EventStoreContractTests
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

    [Theory]
    [InlineData("Task")]
    [InlineData("task.v1-x_y")]
    public void StreamKey_AcceptsValidTypes(string type) =>
        Assert.Equal(type, new StreamKey(type, Guid.NewGuid()).StreamType);

    [Theory]
    [InlineData("")]
    [InlineData(" Task")]
    [InlineData("Task\n")]
    [InlineData("1Task")]
    [InlineData("Ta sk")]
    public void StreamKey_RejectsInvalidTypes(string type) =>
        Assert.Throws<ArgumentException>(() => new StreamKey(type, Guid.NewGuid()));

    [Fact]
    public void StreamKey_RejectsTooLongTypeAndEmptyId()
    {
        Assert.Throws<ArgumentException>(() => new StreamKey("T" + new string('a', 100), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new StreamKey("Task", Guid.Empty));
    }

    [Fact]
    public void NewEvent_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => new NewEvent(Guid.Empty, "TaskCreated", 1, EmptyObject, EmptyObject));
        Assert.Throws<ArgumentException>(() => new NewEvent(Guid.NewGuid(), "", 1, EmptyObject, EmptyObject));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewEvent(Guid.NewGuid(), "TaskCreated", 0, EmptyObject, EmptyObject));
        Assert.Throws<ArgumentException>(() => new NewEvent(Guid.NewGuid(), "TaskCreated", 1, EmptyArray, EmptyObject));
        Assert.Throws<ArgumentException>(() => new NewEvent(Guid.NewGuid(), "TaskCreated", 1, EmptyObject, default));
    }

    [Fact]
    public void AppendResult_ReportsUnknownVersionForRaceConflicts()
    {
        Assert.Null(AppendResult.VersionConflict(null).CurrentVersion);
        Assert.Equal(AppendOutcome.DuplicateEventId, AppendResult.DuplicateEventId().Outcome);
        Assert.False(AppendResult.VersionConflict(3).IsAppended);
    }

    [Fact]
    public async Task Append_RejectsInvalidArgumentsBeforeTouchingTheDatabase()
    {
        // The data source is built but never connects: validation fails first.
        await using var database = PostgreSqlDatabase.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        var store = new PostgreSqlEventStore(database);
        var stream = new StreamKey("Task", Guid.NewGuid());
        var one = new NewEvent(Guid.NewGuid(), "TaskCreated", 1, EmptyObject, EmptyObject);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(stream, 0, [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendAsync(stream, -1, [one], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendAsync(stream, int.MaxValue, [one], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(stream, 0, [one, one], CancellationToken.None));
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=x")]
    [InlineData("Host=localhost;Database=postgres;Username=postgres;Password=x")]
    public void Guard_AcceptsLoopbackPostgresOnAnOrdinaryPort(string connection) =>
        Assert.NotNull(PostgresTestGuard.ValidateTarget(connection));

    [Theory]
    [InlineData(null)]
    [InlineData("not a connection string")]
    [InlineData("Host=127.0.0.1;Port=5434;Database=postgres;Username=postgres;Password=x")]
    [InlineData("Host=127.0.0.1;Port=5535;Database=postgres;Username=postgres;Password=x")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=eventforge;Username=postgres;Password=x")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=postgres;Username=eventforge_app;Password=x")]
    [InlineData("Host=db.example.test;Port=55432;Database=postgres;Username=postgres;Password=x")]
    [InlineData("Host=127.0.0.1,db.example.test;Port=55432;Database=postgres;Username=postgres;Password=x")]
    public void Guard_RefusesUnsafeTargets(string? connection)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresTestGuard.ValidateTarget(connection));
        Assert.DoesNotContain("Password", exception.Message);
    }
}
