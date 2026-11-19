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
        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
    }
}
