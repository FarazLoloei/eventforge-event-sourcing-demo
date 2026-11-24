using System.Reflection;
using EventForge.Workflow.Domain;

namespace EventForge.UnitTests.Domain;

public sealed class DomainContractTests
{
    private static readonly string[] ApprovedEventNames =
        ["TaskCreated", "TaskAssigned", "CodeGenerated", "TestsPassed", "TestsFailed", "TaskCompleted"];

    [Fact]
    public void Contract_uses_the_approved_names_and_schema_version()
    {
        Assert.Equal("Task", TaskEventContract.StreamType);
        Assert.Equal(1, TaskEventContract.InitialSchemaVersion);
        Assert.Equal(2, TaskEventContract.CodeGeneratedSchemaVersion);
        Assert.Equal(
            ApprovedEventNames.Order(StringComparer.Ordinal),
            new[]
            {
                TaskEventContract.TaskCreated,
                TaskEventContract.TaskAssigned,
                TaskEventContract.CodeGenerated,
                TaskEventContract.TestsPassed,
                TaskEventContract.TestsFailed,
                TaskEventContract.TaskCompleted,
            }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Event_set_is_exactly_the_six_approved_events_and_cannot_be_extended_outside_the_assembly()
    {
        var derived = typeof(TaskEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TaskEvent)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(ApprovedEventNames.Order(StringComparer.Ordinal), derived);
        // Ignore the compiler-generated record copy constructor (one parameter of the record's own type).
        var constructor = Assert.Single(
            typeof(TaskEvent).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            c => c.GetParameters().Length == 0);
        Assert.True(constructor.IsFamilyAndAssembly);
    }

    [Fact]
    public void Rejection_codes_are_the_approved_set()
    {
        Assert.Equal(
            new[]
            {
                "TaskIdMismatch", "TaskAlreadyExists", "TaskNotFound", "TaskAlreadyCompleted", "TaskAlreadyAssigned",
                "TaskNotAssigned", "GenerationAlreadyUsed", "NoGeneration", "UnknownGeneration",
                "SupersededGeneration", "TestsNotPassed",
            }.Order(StringComparer.Ordinal),
            Enum.GetNames<RejectionCode>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Initial_state_is_empty()
    {
        var state = TaskState.Initial;

        Assert.Null(state.Id);
        Assert.Equal(WorkflowStatus.NotCreated, state.Status);
        Assert.Null(state.AssignedAgent);
        Assert.Empty(state.UsedGenerations);
        Assert.Null(state.CurrentGeneration);
        Assert.Null(state.CurrentTestOutcome);
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public void TaskId_rejects_the_empty_guid_and_compares_by_value()
    {
        Assert.Throws<ArgumentException>(() => new TaskId(Guid.Empty));

        var id = Guid.NewGuid();
        Assert.Equal(new TaskId(id), new TaskId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("g\n1")]
    [InlineData("g\t1")]
    public void Opaque_ids_reject_blank_and_control_characters(string value)
    {
        Assert.Throws<ArgumentException>(() => new AgentId(value));
        Assert.Throws<ArgumentException>(() => new GenerationId(value));
        Assert.Throws<ArgumentException>(() => new ArtifactRef(value));
    }

    [Fact]
    public void Opaque_ids_reject_null()
    {
        Assert.Throws<ArgumentException>(() => new AgentId(null!));
        Assert.Throws<ArgumentException>(() => new GenerationId(null!));
        Assert.Throws<ArgumentException>(() => new ArtifactRef(null!));
    }

    [Fact]
    public void Length_limits_are_inclusive()
    {
        Assert.Equal(100, new AgentId(new string('a', 100)).Value.Length);
        Assert.Equal(100, new GenerationId(new string('a', 100)).Value.Length);
        Assert.Equal(500, new ArtifactRef(new string('a', 500)).Value.Length);

        Assert.Throws<ArgumentException>(() => new AgentId(new string('a', 101)));
        Assert.Throws<ArgumentException>(() => new GenerationId(new string('a', 101)));
        Assert.Throws<ArgumentException>(() => new ArtifactRef(new string('a', 501)));
    }

    [Fact]
    public void ArtifactRef_is_opaque_and_does_not_require_a_scheme()
    {
        Assert.Equal("any-reference", new ArtifactRef("any-reference").Value);
    }

    [Fact]
    public void Generation_ids_compare_ordinally()
    {
        Assert.Equal(new GenerationId("g1"), new GenerationId("g1"));
        Assert.NotEqual(new GenerationId("g1"), new GenerationId("G1"));
    }
}
