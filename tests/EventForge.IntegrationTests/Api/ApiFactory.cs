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
/// Hosts the API in memory. By default the connection string is blank, so no database is ever contacted, and the
/// event store and the task overview reader are replaced with in-memory doubles. The PostgreSQL tests pass a real
/// connection string and keep the real store and reader.
/// </summary>
internal sealed class ApiFactory(
    string environment = "Development", bool fakeStore = true, string connectionString = "") : WebApplicationFactory<Program>
{
    private InMemoryTaskOverviewReader? _overview;

    public InMemoryEventStore Store { get; } = new();

    /// <summary>The in-memory projection reader; it follows <see cref="Store"/> unless a test overrides or hides a row.</summary>
    public InMemoryTaskOverviewReader Overview => _overview ??= new InMemoryTaskOverviewReader(Store);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EventForge"] = connectionString }));
        builder.ConfigureTestServices(services =>
        {
            if (fakeStore)
            {
                services.RemoveAll<IEventStore>();
                services.AddSingleton<IEventStore>(Store);
                services.RemoveAll<ITaskOverviewReader>();
                services.AddSingleton<ITaskOverviewReader>(Overview);
            }
        });
    }
}
