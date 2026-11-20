using System.Collections.Immutable;
using EventForge.Workflow.Domain;

namespace EventForge.UnitTests.Domain;

/// <summary>
/// Hand-written states and assertion helpers. States are built as literals, not through the aggregate, so the
/// tests do not depend on the logic under test.
/// </summary>
internal static class TaskTestData
{
    public static readonly TaskId T1 = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    public static readonly TaskId T2 = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    public static readonly AgentId Agent = new("agent-1");
    public static readonly GenerationId G1 = new("g1");
    public static readonly GenerationId G2 = new("g2");
    public static readonly GenerationId G9 = new("g9");
    public static readonly ArtifactRef Art1 = new("artifact-1");
    public static readonly ArtifactRef Art2 = new("artifact-2");

    public static TaskState Created() =>
        TaskState.Initial with { Id = T1, Status = WorkflowStatus.Created, Version = 1 };

    public static TaskState Assigned() =>
        Created() with { Status = WorkflowStatus.Assigned, AssignedAgent = Agent, Version = 2 };

    /// <summary>Assigned task with the given generations in order; the last one is current and has no outcome.</summary>
    public static TaskState Generated(params GenerationId[] ids) =>
        Assigned() with
        {
            UsedGenerations = ImmutableHashSet.CreateRange(ids),
            CurrentGeneration = ids[^1],
            CurrentTestOutcome = null,
            Version = 2 + ids.Length,
        };

    public static TaskState WithOutcome(TaskState state, TestOutcome outcome) =>
        state with { CurrentTestOutcome = outcome, Version = state.Version + 1 };

    public static TaskState Completed() =>
        WithOutcome(Generated(G1), TestOutcome.Passed) with { Status = WorkflowStatus.Completed, Version = 5 };

    /// <summary>Compares the emitted events by value, element by element, not through Accepted record equality.</summary>
    public static void AssertAccepted(Decision decision, params TaskEvent[] expected)
    {
        var accepted = Assert.IsType<Accepted>(decision);
        Assert.Equal(expected.Length, accepted.Events.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], accepted.Events[i]);
        }
    }

    public static void AssertRejected(Decision decision, RejectionCode code) =>
        Assert.Equal(code, Assert.IsType<Rejected>(decision).Code);

    /// <summary>Compares every field; <c>UsedGenerations</c> by content, since the set has reference equality.</summary>
    public static void AssertSameState(TaskState expected, TaskState actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.AssignedAgent, actual.AssignedAgent);
        Assert.Equal(expected.CurrentGeneration, actual.CurrentGeneration);
        Assert.Equal(expected.CurrentTestOutcome, actual.CurrentTestOutcome);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.UsedGenerations.Count, actual.UsedGenerations.Count);
        Assert.True(expected.UsedGenerations.SetEquals(actual.UsedGenerations));
    }
}
