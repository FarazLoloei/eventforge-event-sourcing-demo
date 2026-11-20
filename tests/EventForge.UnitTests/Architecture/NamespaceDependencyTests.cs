using Xunit.Abstractions;

namespace EventForge.UnitTests.Architecture;

public sealed class NamespaceDependencyTests(ITestOutputHelper output)
{
    [Fact]
    public void ProductionSources_RespectNamespaceDependencyRules()
    {
        var root = ArchitectureRuleChecker.FindRepositoryRoot();
        var sources = ArchitectureRuleChecker.LoadSourceFiles(root);

        var result = ArchitectureRuleChecker.CheckNamespaceDependencies(sources);

        foreach (var layer in Enum.GetValues<ModuleLayer>())
        {
            output.WriteLine($"Analyzed {layer,-14} files={result.FilesByLayer[layer]} types={result.TypesByLayer[layer]}");
        }

        Assert.True(result.FilesByLayer.Values.Sum() > 0, "No production C# sources were analyzed.");

        // Domain and Application now contain production types; the scan must actually include them, otherwise
        // a loader regression could skip these layers and still report no violations.
        foreach (var layer in new[] { ModuleLayer.Domain, ModuleLayer.Application })
        {
            Assert.True(result.FilesByLayer[layer] > 0, $"No {layer} source files were analyzed.");
            Assert.True(result.TypesByLayer[layer] > 0, $"No {layer} types were analyzed.");
        }

        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
    }
}
