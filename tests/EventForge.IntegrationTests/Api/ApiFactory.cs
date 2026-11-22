using EventForge.Workflow.Application.EventStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventForge.IntegrationTests.Api;

/// <summary>
/// Hosts the API in memory. The connection string is blank, so no database is ever contacted; by default the event
/// store is replaced with an <see cref="InMemoryEventStore"/>.
/// </summary>
internal sealed class ApiFactory(string environment = "Development", bool fakeStore = true) : WebApplicationFactory<Program>
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
