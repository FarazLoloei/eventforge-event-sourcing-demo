using EventForge.UnitTests.Architecture.Fixtures;

namespace EventForge.UnitTests.Architecture;

/// <summary>
/// Runs synthetic source sets through the same checker the production tests use, proving that the
/// checker accepts valid structure and rejects each forbidden dependency for the intended rule.
/// </summary>
public sealed class ArchitectureRuleTests
{
    public static TheoryData<string> NegativeScenarios() =>
        [.. ArchitectureFixtures.Negatives.Select(f => f.Name)];

    private static AnalysisResult Analyze(IReadOnlyCollection<SourceFile> sources) =>
        ArchitectureRuleChecker.CheckNamespaceDependencies(sources, ArchitectureFixtures.ForbiddenFrameworkStubs);

    [Fact]
    public void ValidStructure_IsAccepted_AndEveryLayerIsActuallyAnalyzed()
    {
        var result = Analyze(ArchitectureFixtures.ValidWorld);

        // Domain -> Domain (SyntheticTask uses SyntheticTaskId) and Application -> Domain
        // (SyntheticHandler uses both) are present and allowed.
        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
        Assert.Equal(2, result.TypesByLayer[ModuleLayer.Domain]);
        Assert.Equal(1, result.TypesByLayer[ModuleLayer.Application]);
        Assert.Equal(1, result.TypesByLayer[ModuleLayer.Infrastructure]);
        Assert.Equal(1, result.TypesByLayer[ModuleLayer.Simulation]);
        Assert.Equal(1, result.TypesByLayer[ModuleLayer.Api]);
    }

    [Theory]
    [MemberData(nameof(NegativeScenarios))]
    public void ForbiddenDependency_IsRejectedForTheIntendedRule(string scenario)
    {
        var fixture = ArchitectureFixtures.NegativeByName(scenario);

        var result = Analyze(fixture.Sources);

        var report = ArchitectureRuleChecker.Format(result.Violations);
        Assert.NotEmpty(fixture.ExpectedLines);
        Assert.True(result.Violations.Count > 0, $"'{scenario}' was accepted.");

        // Only the intended rule fires: not a compilation error, a missing reference, or another rule.
        Assert.True(result.Violations.All(v => v.Rule == fixture.Rule), report);
        Assert.True(result.Violations.All(v => v.File == fixture.Violating.Path), report);
        Assert.Contains(result.Violations, v => v.Target.Contains(fixture.TargetFragment, StringComparison.Ordinal));

        // The flagged lines are exactly the lines the fixture marks as violating.
        Assert.Equal(fixture.ExpectedLines, result.Violations.Select(v => v.Line).Distinct().Order().ToList());
    }

    [Fact]
    public void InfrastructureUsingApprovedNpgsql_IsAccepted_WithoutAnalysisErrors()
    {
        var result = Analyze(ArchitectureFixtures.ValidInfrastructureNpgsqlUsage);

        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
        Assert.Equal(2, result.TypesByLayer[ModuleLayer.Infrastructure]);
    }

    [Fact]
    public void MissingApprovedPackageAssembly_FailsClosed()
    {
        var empty = Directory.CreateTempSubdirectory("eventforge-no-packages-");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ArchitectureRuleChecker.LoadPackageReferences(ApprovedRules.Packages, empty.FullName));
            Assert.Contains("Npgsql.dll", exception.Message);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void ApprovedPackageOfWrongMajorVersion_FailsClosed()
    {
        var wrong = new[] { new ApprovedPackage("Npgsql.dll", "Npgsql", 9) };

        Assert.Throws<InvalidOperationException>(() =>
            ArchitectureRuleChecker.LoadPackageReferences(wrong, AppContext.BaseDirectory));
    }

    [Fact]
    public void ValidNestedNamespaces_AreAccepted_UsingResolvedNamespaces()
    {
        var result = Analyze(ArchitectureFixtures.ValidNestedNamespaces);

        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
        Assert.Equal(4, result.TypesByLayer[ModuleLayer.Domain]);
        Assert.Equal(3, result.TypesByLayer[ModuleLayer.Application]);
    }

    [Fact]
    public void GeneratedAndBuildOutputSources_AreExcludedFromAnalysis()
    {
        var sources = ArchitectureFixtures.ValidWorld.Concat(ArchitectureFixtures.GeneratedAndBuildOutputSources).ToList();

        var result = Analyze(sources);

        Assert.True(result.Violations.Count == 0, ArchitectureRuleChecker.Format(result.Violations));
        Assert.Equal(2, result.TypesByLayer[ModuleLayer.Domain]);
    }
}
