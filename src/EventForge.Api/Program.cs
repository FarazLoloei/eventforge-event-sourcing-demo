using System.Text.Json;
using System.Text.Json.Serialization;
using EventForge.Api.Tasks;
using EventForge.Infrastructure.Persistence.PostgreSql;
using EventForge.Workflow.Application.EventStore;
using EventForge.Workflow.Application.Tasks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;

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

// Task command API (EVF-33). Both services are cheap to create and hold no request state; the event store only
// takes the lazily created database singleton, so the host still starts without database credentials.
// Task appends update the task_overview projection inside PostgreSqlEventStore itself (EVF-37); there is no
// projection-free configuration to register.
builder.Services.AddSingleton<IEventStore, PostgreSqlEventStore>();
builder.Services.AddSingleton<TaskQueryHandler>();
builder.Services.AddSingleton(services => new TaskCommandHandler(services.GetRequiredService<IEventStore>(), Guid.NewGuid));

// Unknown request members are rejected (property names stay case-insensitive); unreadable bodies always throw so the
// exception handler below turns them into Problem Details in every environment.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict; // "1" is not an integer
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

var app = builder.Build();

if (verifyDatabase)
{
    await using (app)
    {
        return await VerifyDatabaseAsync(app.Services);
    }
}

app.UseExceptionHandler(errorApp => errorApp.Run(TaskProblems.HandleExceptionAsync));

if (app.Environment.IsDevelopment())
{
    // Local demonstration only: the OpenAPI document and Swagger UI are never mapped outside Development.
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "EventForge Task Workflow API";
        options.SwaggerEndpoint("/openapi/v1.json", "EventForge API v1");
    });
}

app.MapHealthChecks("/health");
app.MapTaskEndpoints();

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

// Lets the integration tests host this application with WebApplicationFactory.
public partial class Program;
