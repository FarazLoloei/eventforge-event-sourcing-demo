namespace EventForge.UnitTests.Architecture.Fixtures;

/// <summary>
/// A synthetic source set that must be rejected. Violating lines are marked with a trailing
/// <c>// violates</c> comment, so expected line numbers come from the fixture, not from the checker.
/// </summary>
public sealed record NegativeFixture(
    string Name,
    string Rule,
    string TargetFragment,
    SourceFile Violating,
    IReadOnlyList<SourceFile> Sources)
{
    public IReadOnlyList<int> ExpectedLines { get; } = Violating.Text.Split('\n')
        .Select((text, index) => (text, line: index + 1))
        .Where(item => item.text.Contains("// violates", StringComparison.Ordinal))
        .Select(item => item.line)
        .ToList();
}

/// <summary>Minimal synthetic sources and project files. Nothing here is compiled into the test assembly.</summary>
public static class ArchitectureFixtures
{
    private const string DomainPath = "src/EventForge.Workflow/Domain/";
    private const string ApplicationPath = "src/EventForge.Workflow/Application/";

    // ------------------------------------------------------------ valid source world

    public static IReadOnlyList<SourceFile> ValidWorld { get; } =
    [
        new(DomainPath + "SyntheticTaskId.cs", """
            namespace EventForge.Workflow.Domain;

            public readonly record struct SyntheticTaskId(Guid Value);
            """),
        new(DomainPath + "SyntheticTask.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class SyntheticTask(SyntheticTaskId id)
            {
                public SyntheticTaskId Id { get; } = id;
            }
            """),
        new(ApplicationPath + "SyntheticHandler.cs", """
            using EventForge.Workflow.Domain;

            namespace EventForge.Workflow.Application;

            public sealed class SyntheticHandler
            {
                public SyntheticTask Create(SyntheticTaskId id) => new(id);
            }
            """),
        new("src/EventForge.Infrastructure/SyntheticStore.cs", """
            using EventForge.Workflow.Application;

            namespace EventForge.Infrastructure;

            public sealed class SyntheticStore(SyntheticHandler handler)
            {
                public SyntheticHandler Handler { get; } = handler;
            }
            """),
        new("src/EventForge.Simulation/SyntheticSimulator.cs", """
            using EventForge.Workflow.Application;

            namespace EventForge.Simulation;

            public sealed class SyntheticSimulator(SyntheticHandler handler)
            {
                public SyntheticHandler Handler { get; } = handler;
            }
            """),
        new("src/EventForge.Api/SyntheticApiMarker.cs", """
            namespace EventForge.Api;

            public sealed class SyntheticApiMarker;
            """),
    ];

    /// <summary>Valid code that declares its owned namespace through nested namespace blocks.</summary>
    public static IReadOnlyList<SourceFile> ValidNestedNamespaces { get; } =
    [
        .. ValidWorld,
        new(DomainPath + "NestedDomain.cs", """
            namespace EventForge.Workflow
            {
                namespace Domain
                {
                    public sealed class NestedDomainType;

                    namespace Deeper { public sealed class DeeperDomainType; }
                }
            }
            """),
        new(ApplicationPath + "NestedApplication.cs", """
            namespace EventForge.Workflow
            {
                namespace Application
                {
                    public sealed class NestedApplicationType;
                }
            }

            namespace EventForge.Workflow.Application.Sub
            {
                public sealed class SubApplicationType;
            }
            """),
    ];

    /// <summary>Infrastructure code using the approved Npgsql package: must be accepted.</summary>
    public static IReadOnlyList<SourceFile> ValidInfrastructureNpgsqlUsage { get; } =
    [
        .. ValidWorld,
        new("src/EventForge.Infrastructure/SyntheticNpgsqlStore.cs", """
            using Npgsql;

            namespace EventForge.Infrastructure.Persistence;

            public sealed class SyntheticNpgsqlStore(NpgsqlDataSource dataSource)
            {
                public NpgsqlDataSource DataSource { get; } = dataSource;

                public async Task<int> CountAsync(CancellationToken cancellationToken)
                {
                    await using var command = DataSource.CreateCommand("SELECT 1");
                    return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
                }
            }
            """),
    ];

    /// <summary>Stand-ins for packages that are deliberately not referenced. Compiled, never analyzed.</summary>
    public static IReadOnlyList<SourceFile> ForbiddenFrameworkStubs { get; } =
    [
        new("stubs/ForbiddenFrameworkStubs.cs", """
            namespace Microsoft.AspNetCore.Http { public class HttpContext; }
            namespace Microsoft.EntityFrameworkCore { public class DbContext; }
            """),
    ];

    public static IReadOnlyList<SourceFile> GeneratedAndBuildOutputSources { get; } =
    [
        new(DomainPath + "obj/Debug/BuildOutput.cs", "namespace EventForge.Workflow.Domain { public class A { System.Net.Http.HttpClient? C; } }"),
        new(DomainPath + "Designer.g.cs", "namespace EventForge.Workflow.Domain { public class B { System.Net.Http.HttpClient? C; } }"),
        new(DomainPath + "AutoGenerated.cs", "// <auto-generated/>\nnamespace EventForge.Workflow.Domain { public class C { System.Net.Http.HttpClient? D; } }"),
    ];

    // ------------------------------------------------------------ forbidden source scenarios

    public static IReadOnlyList<NegativeFixture> Negatives { get; } =
    [
        Negative("domain-depends-on-application",
            "Domain-must-not-depend-on-EventForge.Workflow.Application", "SyntheticHandler",
            DomainPath + "DomainUsesApplication.cs", """
            using EventForge.Workflow.Application; // violates

            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesApplication
            {
                private readonly SyntheticHandler? _handler; // violates
            }
            """),
        Negative("domain-depends-on-http",
            "Domain-must-not-depend-on-Microsoft.AspNetCore", "HttpContext",
            DomainPath + "DomainUsesAspNetCore.cs", """
            using Microsoft.AspNetCore.Http; // violates

            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesAspNetCore
            {
                public void Accept(HttpContext context) { } // violates
            }
            """),
        Negative("domain-depends-on-sql-abstraction",
            "Domain-must-not-depend-on-System.Data", "IDbConnection",
            DomainPath + "DomainUsesSql.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesSql
            {
                private System.Data.IDbConnection? _connection; // violates
            }
            """),
        Negative("domain-depends-on-npgsql",
            "Domain-must-not-depend-on-Npgsql", "NpgsqlConnection",
            DomainPath + "DomainUsesNpgsql.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesNpgsql
            {
                private Npgsql.NpgsqlConnection? _connection; // violates
            }
            """),
        Negative("domain-depends-on-npgsql-via-using-and-data-source",
            "Domain-must-not-depend-on-Npgsql", "NpgsqlDataSource",
            DomainPath + "DomainUsesNpgsqlDataSource.cs", """
            using Npgsql; // violates

            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesNpgsqlDataSource
            {
                private readonly NpgsqlDataSource? _dataSource; // violates
            }
            """),
        Negative("application-depends-on-npgsql",
            "Application-must-not-depend-on-Npgsql", "NpgsqlDataSource",
            ApplicationPath + "ApplicationUsesNpgsql.cs", """
            using Npgsql; // violates

            namespace EventForge.Workflow.Application;

            public sealed class ApplicationUsesNpgsql
            {
                private readonly NpgsqlDataSource? _dataSource; // violates
            }
            """),
        Negative("simulation-depends-on-npgsql",
            "Simulation-must-not-depend-on-Npgsql", "NpgsqlDataSource",
            "src/EventForge.Simulation/SimulationUsesNpgsql.cs", """
            namespace EventForge.Simulation;

            public sealed class SimulationUsesNpgsql
            {
                private readonly Npgsql.NpgsqlDataSource? _dataSource; // violates
            }
            """),
        Negative("infrastructure-uses-unapproved-package-is-not-a-silent-pass",
            "analysis-incomplete", "CS0246",
            "src/EventForge.Infrastructure/InfrastructureUsesUnapprovedPackage.cs", """
            namespace EventForge.Infrastructure;

            public sealed class InfrastructureUsesUnapprovedPackage
            {
                private readonly Dapper.SqlMapper? _mapper; // violates
            }
            """),
        Negative("domain-depends-on-entity-framework",
            "Domain-must-not-depend-on-Microsoft.EntityFrameworkCore", "DbContext",
            DomainPath + "DomainUsesEntityFramework.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesEntityFramework
            {
                private Microsoft.EntityFrameworkCore.DbContext? _context; // violates
            }
            """),
        Negative("domain-depends-on-infrastructure",
            "Domain-must-not-depend-on-EventForge.Infrastructure", "SyntheticStore",
            DomainPath + "DomainUsesInfrastructure.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesInfrastructure
            {
                private EventForge.Infrastructure.SyntheticStore? _store; // violates
            }
            """),
        Negative("domain-depends-on-simulation",
            "Domain-must-not-depend-on-EventForge.Simulation", "SyntheticSimulator",
            DomainPath + "DomainUsesSimulation.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesSimulation
            {
                private EventForge.Simulation.SyntheticSimulator? _simulator; // violates
            }
            """),
        Negative("application-depends-on-api",
            "Application-must-not-depend-on-EventForge.Api", "SyntheticApiMarker",
            ApplicationPath + "ApplicationUsesApi.cs", """
            using EventForge.Api; // violates

            namespace EventForge.Workflow.Application;

            public sealed class ApplicationUsesApi
            {
                private readonly SyntheticApiMarker? _marker; // violates
            }
            """),
        Negative("infrastructure-depends-on-simulation",
            "Infrastructure-must-not-depend-on-EventForge.Simulation", "SyntheticSimulator",
            "src/EventForge.Infrastructure/InfrastructureUsesSimulation.cs", """
            namespace EventForge.Infrastructure;

            public sealed class InfrastructureUsesSimulation
            {
                private EventForge.Simulation.SyntheticSimulator? _simulator; // violates
            }
            """),
        Negative("simulation-depends-on-infrastructure",
            "Simulation-must-not-depend-on-EventForge.Infrastructure", "SyntheticStore",
            "src/EventForge.Simulation/SimulationUsesInfrastructure.cs", """
            namespace EventForge.Simulation;

            public sealed class SimulationUsesInfrastructure
            {
                private EventForge.Infrastructure.SyntheticStore? _store; // violates
            }
            """),
        Negative("alias-hides-forbidden-dependency",
            "Domain-must-not-depend-on-System.Net.Http", "HttpClient",
            DomainPath + "DomainUsesAlias.cs", """
            using Client = System.Net.Http.HttpClient; // violates

            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesAlias
            {
                private readonly Client? _client; // violates
            }
            """),
        Negative("fully-qualified-forbidden-dependency",
            "Domain-must-not-depend-on-System.Net.Http", "HttpClient",
            DomainPath + "DomainUsesFullyQualified.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesFullyQualified
            {
                private readonly System.Net.Http.HttpClient? _client; // violates
            }
            """),
        Negative("unqualified-forbidden-type-reference",
            "Domain-must-not-depend-on-System.Net.Http", "HttpClient",
            DomainPath + "DomainUsesUnqualified.cs", """
            using System.Net.Http; // violates

            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesUnqualified
            {
                private readonly HttpClient? _client; // violates
            }
            """),
        Negative("implicit-global-using-forbidden-type",
            "Domain-must-not-depend-on-System.Net.Http", "HttpClient",
            DomainPath + "DomainUsesImplicitUsing.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesImplicitUsing
            {
                private readonly HttpClient? _client; // violates
            }
            """),
        Negative("forbidden-dependency-in-every-reference-position",
            "Domain-must-not-depend-on-System.Net.Http", "System.Net.Http",
            DomainPath + "DomainKitchenSink.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainKitchenSink : System.Net.Http.DelegatingHandler // violates
            {
                private readonly System.Net.Http.HttpClient? _field; // violates
                public System.Net.Http.HttpResponseMessage? Last { get; set; } // violates
                public void Accept(System.Net.Http.HttpRequestMessage request) { } // violates
                public List<System.Net.Http.HttpMethod> Methods { get; } = new(); // violates
                public object Member() { var method = System.Net.Http.HttpMethod.Get; return method; } // violates
            }
            """),
        Negative("namespace-does-not-match-folder-ownership",
            "namespace-ownership", "EventForge.Workflow.Application",
            DomainPath + "MisplacedType.cs", """
            namespace EventForge.Workflow.Application; // violates

            public sealed class MisplacedType;
            """),
        Negative("unresolved-symbol-is-not-a-silent-pass",
            "analysis-incomplete", "CS0246",
            DomainPath + "DomainUsesMissingType.cs", """
            namespace EventForge.Workflow.Domain;

            public sealed class DomainUsesMissingType
            {
                private readonly MissingType? _missing; // violates
            }
            """),
        Negative("workflow-root-file-declares-domain-type-with-http-dependency",
            "workflow-source-location", "-",
            "src/EventForge.Workflow/RootDomainType.cs", """
            namespace EventForge.Workflow.Domain; // violates

            public sealed class RootDomainType
            {
                private readonly System.Net.Http.HttpClient? _client;
            }
            """),
        Negative("workflow-root-helper-bypasses-layer-rules",
            "workflow-source-location", "-",
            "src/EventForge.Workflow/RootHelper.cs", """
            namespace EventForge.Workflow.Application; // violates

            public static class RootHelper
            {
                public static EventForge.Infrastructure.SyntheticStore? Store;
            }
            """),
        Negative("api-declares-workflow-owned-namespace",
            "namespace-ownership", "EventForge.Workflow.Domain",
            "src/EventForge.Api/ApiDeclaresWorkflowNamespace.cs", """
            namespace EventForge.Workflow.Domain; // violates

            public sealed class ApiDeclaresWorkflowNamespace;
            """),
    ];

    public static NegativeFixture NegativeByName(string name) => Negatives.Single(f => f.Name == name);

    private static NegativeFixture Negative(string name, string rule, string target, string path, string text)
    {
        var violating = new SourceFile(path, text);
        return new NegativeFixture(name, rule, target, violating, [.. ValidWorld, violating]);
    }

    // ------------------------------------------------------------ project reference scenarios

    public static IReadOnlyList<ProjectFile> ValidProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure", "EventForge.Workflow"),
        Production("EventForge.Simulation", "EventForge.Workflow"),
        TestProject("EventForge.UnitTests"));

    public static IReadOnlyList<ProjectFile> OutwardDependencyProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow", "EventForge.Infrastructure"),
        Production("EventForge.Infrastructure", "EventForge.Workflow"),
        Production("EventForge.Simulation", "EventForge.Workflow"));

    public static IReadOnlyList<ProjectFile> CircularDependencyProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure", "EventForge.Workflow", "EventForge.Simulation"),
        Production("EventForge.Simulation", "EventForge.Workflow", "EventForge.Infrastructure"));

    public static IReadOnlyList<ProjectFile> ProductionReferencesTestProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation", "EventForge.UnitTests"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure", "EventForge.Workflow"),
        Production("EventForge.Simulation", "EventForge.Workflow"),
        TestProject("EventForge.UnitTests"));

    public static IReadOnlyList<ProjectFile> MissingReferenceProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure"),
        Production("EventForge.Simulation", "EventForge.Workflow"));

    public static IReadOnlyList<ProjectFile> UnresolvedReferenceProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure", "EventForge.Workflow"),
        new ProjectFile("src/EventForge.Simulation/EventForge.Simulation.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\EventForge.DoesNotExist\EventForge.DoesNotExist.csproj" />
              </ItemGroup>
            </Project>
            """));

    public static IReadOnlyList<ProjectFile> ConditionalReferenceProjects { get; } = Projects(
        Production("EventForge.Api", "EventForge.Workflow", "EventForge.Infrastructure", "EventForge.Simulation"),
        Production("EventForge.Workflow"),
        Production("EventForge.Infrastructure", "EventForge.Workflow"),
        new ProjectFile("src/EventForge.Simulation/EventForge.Simulation.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(Configuration)' == 'Debug'">
                <ProjectReference Include="..\EventForge.Workflow\EventForge.Workflow.csproj" />
              </ItemGroup>
            </Project>
            """));

    private static IReadOnlyList<ProjectFile> Projects(params ProjectFile[] projects) => projects;

    private static ProjectFile Production(string name, params string[] references) =>
        new($"src/{name}/{name}.csproj", Csproj(references));

    private static ProjectFile TestProject(string name, params string[] references) =>
        new($"tests/{name}/{name}.csproj", Csproj(references));

    private static string Csproj(string[] references)
    {
        // Every fixture project sits two levels below the root (src/<name> or tests/<name>).
        var items = string.Concat(references.Select(reference =>
            $"    <ProjectReference Include=\"..\\..\\{(reference.EndsWith("Tests") ? "tests" : "src")}\\{reference}\\{reference}.csproj\" />\n"));
        return $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n{items}  </ItemGroup>\n</Project>\n";
    }
}
