using EventForge.UnitTests.Architecture.Fixtures;

namespace EventForge.UnitTests.Architecture;

public sealed class ProjectDependencyTests
{
    private static IReadOnlyList<ArchitectureViolation> Check(IReadOnlyList<ProjectFile> projects) =>
        ArchitectureRuleChecker.CheckProjectReferences(projects, ApprovedRules.ProjectReferences);

    // ---- production project files

    [Fact]
    public void ProductionProjectReferences_MatchApprovedMatrix()
    {
        var root = ArchitectureRuleChecker.FindRepositoryRoot();
        var projects = ArchitectureRuleChecker.LoadProjectFiles(root);

        Assert.NotEmpty(projects);
        var violations = Check(projects);
        Assert.True(violations.Count == 0, ArchitectureRuleChecker.Format(violations));
    }

    [Fact]
    public void RepositoryRootLookup_FailsClearlyOutsideARepository()
    {
        var outside = Path.Combine(Path.GetTempPath(), "eventforge-not-a-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var exception = Assert.Throws<DirectoryNotFoundException>(() => ArchitectureRuleChecker.FindRepositoryRoot(outside));
            Assert.Contains("EventForge.slnx", exception.Message);
        }
        finally
        {
            Directory.Delete(outside);
        }
    }

    // ---- synthetic fixtures run through the same checker

    [Fact]
    public void ValidFixtureProjects_HaveNoViolations()
    {
        var violations = Check(ArchitectureFixtures.ValidProjects);
        Assert.True(violations.Count == 0, ArchitectureRuleChecker.Format(violations));
    }

    [Fact]
    public void OutwardDependency_IsRejected()
    {
        var violations = Check(ArchitectureFixtures.OutwardDependencyProjects);

        Assert.Contains(violations, v =>
            v.Rule == "unexpected-reference" && v.Source == "EventForge.Workflow" && v.Target == "EventForge.Infrastructure");
    }

    [Fact]
    public void CircularDependency_IsRejected()
    {
        var violations = Check(ArchitectureFixtures.CircularDependencyProjects);

        var cycle = Assert.Single(violations, v => v.Rule == "circular-reference");
        Assert.Contains("EventForge.Infrastructure", cycle.Target);
        Assert.Contains("EventForge.Simulation", cycle.Target);
    }

    [Fact]
    public void ProductionReferenceToTestProject_IsRejected()
    {
        var violations = Check(ArchitectureFixtures.ProductionReferencesTestProjects);

        Assert.Contains(violations, v =>
            v.Rule == "production-references-test" && v.Source == "EventForge.Api" && v.Target == "EventForge.UnitTests");
    }

    [Fact]
    public void MissingApprovedReference_IsRejected()
    {
        var violations = Check(ArchitectureFixtures.MissingReferenceProjects);

        Assert.Contains(violations, v =>
            v.Rule == "missing-reference" && v.Source == "EventForge.Infrastructure" && v.Target == "EventForge.Workflow");
    }

    [Fact]
    public void UnresolvedProjectReference_FailsInsteadOfBeingIgnored()
    {
        var violations = Check(ArchitectureFixtures.UnresolvedReferenceProjects);

        Assert.Contains(violations, v =>
            v.Rule == "unresolved-project-reference" && v.Source == "EventForge.Simulation");
    }

    [Fact]
    public void ConditionalReference_IsReportedAsUnsupportedInsteadOfIgnored()
    {
        var violations = Check(ArchitectureFixtures.ConditionalReferenceProjects);

        Assert.Contains(violations, v =>
            v.Rule == "unsupported-msbuild-construct" && v.Source == "EventForge.Simulation");
    }
}
