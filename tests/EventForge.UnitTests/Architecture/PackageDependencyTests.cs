using System.Xml.Linq;

namespace EventForge.UnitTests.Architecture;

/// <summary>EVF-21: the approved Npgsql dependency is declared where intended and the checker's copy matches it.</summary>
public sealed class PackageDependencyTests
{
    private static IReadOnlyList<(string Project, string Version)> NpgsqlReferences(string directory)
    {
        var root = ArchitectureRuleChecker.FindRepositoryRoot();
        return ArchitectureRuleChecker.LoadProjectFiles(root)
            .Where(project => project.Path.StartsWith(directory, StringComparison.Ordinal))
            .SelectMany(project => XDocument.Parse(project.Content).Descendants()
                .Where(e => e.Name.LocalName == "PackageReference" &&
                            string.Equals(e.Attribute("Include")?.Value, "Npgsql", StringComparison.OrdinalIgnoreCase))
                .Select(e => (project.Path, e.Attribute("Version")?.Value ?? "<missing>")))
            .ToList();
    }

    [Fact]
    public void Npgsql_IsReferencedByInfrastructureOnly_AmongProductionProjects()
    {
        var production = NpgsqlReferences("src/");

        var reference = Assert.Single(production);
        Assert.Equal("src/EventForge.Infrastructure/EventForge.Infrastructure.csproj", reference.Project);
    }

    [Fact]
    public void CheckerNpgsqlMetadata_MatchesTheVersionInfrastructureReferences()
    {
        var infrastructure = Assert.Single(NpgsqlReferences("src/")).Version;
        var checker = Assert.Single(NpgsqlReferences("tests/")).Version;
        Assert.Equal(infrastructure, checker);

        // The assembly the checker actually loads must belong to that package version.
        var path = Path.Combine(AppContext.BaseDirectory, "Npgsql.dll");
        var loaded = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
        Assert.StartsWith(infrastructure, loaded, StringComparison.Ordinal);
    }
}
