using EventForge.Infrastructure.Persistence.PostgreSql;

// Explicit, opt-in database verification: `dotnet run --project src/EventForge.Api -- --verify-database`.
// The flag is removed before host configuration so it cannot be mistaken for a configuration key.
const string verifyDatabaseFlag = "--verify-database";
var verifyDatabase = args.Contains(verifyDatabaseFlag, StringComparer.Ordinal);
var hostArgs = args.Where(arg => !string.Equals(arg, verifyDatabaseFlag, StringComparison.Ordinal)).ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Services.AddHealthChecks();

// Singleton: one pooled NpgsqlDataSource per process, disposed by the container. The factory runs on first
// resolution only, so the host starts (and /health works) without database credentials.
builder.Services.AddSingleton(services => PostgreSqlDatabase.Create(
    services.GetRequiredService<IConfiguration>().GetConnectionString(PostgreSqlDatabase.ConnectionStringName)));

var app = builder.Build();

if (verifyDatabase)
{
    await using (app)
    {
        return await VerifyDatabaseAsync(app.Services);
    }
}

app.MapHealthChecks("/health");

app.Run();
return 0;

static async Task<int> VerifyDatabaseAsync(IServiceProvider services)
{
    try
    {
        var database = services.GetRequiredService<PostgreSqlDatabase>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await database.VerifyConnectivityAsync(timeout.Token);

        Console.WriteLine($"PostgreSQL verification: {(result.Succeeded ? "PASSED" : "FAILED")}");
        foreach (var check in result.Checks)
        {
            Console.WriteLine($"  [{(check.Passed ? "ok" : "FAIL")}] {check.Name}: {check.Detail}");
        }

        return result.Succeeded ? 0 : 1;
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine($"PostgreSQL verification: FAILED{Environment.NewLine}  {exception.Message}");
        return 1;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"PostgreSQL verification: FAILED{Environment.NewLine}  timed out after 30 seconds");
        return 1;
    }
}
