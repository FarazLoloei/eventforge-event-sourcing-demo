using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// One <c>task_overview</c> row: exactly the six persisted fields (migration 002). <see cref="LastStreamVersion"/> is
/// the number of events folded into the row. A task that does not exist has no row, so <see cref="Status"/> is never
/// <see cref="WorkflowStatus.NotCreated"/>.
/// </summary>
public sealed record TaskOverviewRow(
    Guid TaskId,
    WorkflowStatus Status,
    string? AssignedAgentId,
    string? CurrentGenerationId,
    TestOutcome? LatestTestResult,
    int LastStreamVersion);

/// <summary>Why a projection update was refused. Faults never carry stored data.</summary>
public enum TaskOverviewFault
{
    /// <summary>The stream continues (version greater than 1) but the overview row does not exist.</summary>
    MissingRow,

    /// <summary>A <c>TaskCreated</c> event arrived for a task that already has an overview row.</summary>
    RowAlreadyExists,

    /// <summary>The event's stream version is not exactly the row's version plus one (stale, duplicate or gapped).</summary>
    VersionNotContiguous,

    /// <summary>The event cannot follow the row's current state.</summary>
    InvalidTransition,

    /// <summary>The existing row is not a state a valid Task history can produce.</summary>
    InvalidRow,

    /// <summary>The stored event is not a valid schema v1 Task event.</summary>
    InvalidEvent,
}

/// <summary>
/// An inline projection update was refused. Thrown inside the event-store transaction so that the whole append rolls
/// back. The message is fixed, safe text.
/// </summary>
public sealed class TaskOverviewProjectionException(TaskOverviewFault fault)
    : Exception($"The task overview projection cannot be updated ({fault}).")
{
    public TaskOverviewFault Fault { get; } = fault;
}

/// <summary>
/// The pure, deterministic reducer behind the inline <c>task_overview</c> projection. It folds recorded Task facts into
/// the six-field row with the same semantics as <see cref="TaskAggregate.Apply"/>, without calling
/// <see cref="TaskAggregate.Decide"/> and without any I/O, clock or ambient state. It cannot rebuild
/// <see cref="TaskState.UsedGenerations"/> from the row, so it does not re-check generation uniqueness: that rule
/// belongs to the aggregate, which has already decided every event it is given here.
/// </summary>
public static class TaskOverviewProjector
{
    /// <summary>Folds one event (at stream position <paramref name="streamVersion"/>) into <paramref name="previous"/>.</summary>
    /// <exception cref="TaskOverviewProjectionException">The row, version or transition is not valid.</exception>
    public static TaskOverviewRow Reduce(TaskOverviewRow? previous, TaskEvent @event, int streamVersion)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (previous is not null)
        {
            EnsureValid(previous);
        }

        if (@event is TaskCreated created)
        {
            if (previous is not null)
            {
                throw Fault(TaskOverviewFault.RowAlreadyExists);
            }

            if (streamVersion != 1)
            {
                throw Fault(TaskOverviewFault.VersionNotContiguous);
            }

            if (created.TaskId is null)
            {
                throw Fault(TaskOverviewFault.InvalidTransition);
            }

            return new TaskOverviewRow(created.TaskId.Value, WorkflowStatus.Created, null, null, null, 1);
        }

        if (previous is null)
        {
            throw Fault(TaskOverviewFault.MissingRow);
        }

        // Compare without arithmetic overflow: a row at int.MaxValue can never advance.
        if (previous.LastStreamVersion == int.MaxValue || streamVersion != previous.LastStreamVersion + 1)
        {
            throw Fault(TaskOverviewFault.VersionNotContiguous);
        }

        return @event switch
        {
            TaskAssigned e when e.AgentId is not null && previous.Status == WorkflowStatus.Created =>
                previous with { Status = WorkflowStatus.Assigned, AssignedAgentId = e.AgentId.Value, LastStreamVersion = streamVersion },

            // A new generation invalidates the previous test outcome.
            CodeGenerated e when e.GenerationId is not null && e.ArtifactRef is not null
                && previous.Status == WorkflowStatus.Assigned =>
                previous with { CurrentGenerationId = e.GenerationId.Value, LatestTestResult = null, LastStreamVersion = streamVersion },

            TestsPassed e when IsCurrentGeneration(previous, e.GenerationId) =>
                previous with { LatestTestResult = TestOutcome.Passed, LastStreamVersion = streamVersion },

            TestsFailed e when IsCurrentGeneration(previous, e.GenerationId) =>
                previous with { LatestTestResult = TestOutcome.Failed, LastStreamVersion = streamVersion },

            TaskCompleted when previous.Status == WorkflowStatus.Assigned && previous.LatestTestResult == TestOutcome.Passed =>
                previous with { Status = WorkflowStatus.Completed, LastStreamVersion = streamVersion },

            _ => throw Fault(TaskOverviewFault.InvalidTransition),
        };
    }

    /// <summary>
    /// Folds <paramref name="events"/> in order; the first one is at stream version <paramref name="firstStreamVersion"/>.
    /// Returns the final row, or throws on the first invalid step so that nothing is partially applied.
    /// </summary>
    public static TaskOverviewRow ReduceBatch(
        TaskOverviewRow? previous, IReadOnlyList<TaskEvent> events, int firstStreamVersion)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(events));
        }

        var row = previous;
        for (var i = 0; i < events.Count; i++)
        {
            row = Reduce(row, events[i], checked(firstStreamVersion + i));
        }

        return row!;
    }

    private static void EnsureValid(TaskOverviewRow row)
    {
        if (!IsPlausible(row))
        {
            throw Fault(TaskOverviewFault.InvalidRow);
        }
    }

    /// <summary>
    /// Whether an existing row is plausible: it satisfies the nullability rules of migration 002 and the minimum
    /// stream version that the shortest valid history reaching that state would have. Every valid history starts
    /// <c>TaskCreated</c> (1), <c>TaskAssigned</c> (2), then at least <c>CodeGenerated</c> (3), a test result (4) and
    /// <c>TaskCompleted</c> (5), so a <c>Created</c> row can only be at version 1, an <c>Assigned</c> row without a
    /// generation only at version 2, with a generation at 3 or more, with a result at 4 or more, and a
    /// <c>Completed</c> row at 5 or more. The write-side reducer and projection-backed reads share this check, so both
    /// refuse the same impossible rows.
    /// </summary>
    /// <remarks>
    /// These checks are necessary, not sufficient. They reject impossible rows, but a row that passes them can still
    /// disagree with the real event history (for example a wrong agent or generation id, or a result that belongs to a
    /// different history of the same length). Only the event stream proves that, and
    /// <see cref="TaskAggregate"/> stays the authority on business rules: this reducer does not re-run its decisions.
    /// Detecting such drift is the job of the later verification and replay work, not of the inline projection.
    /// </remarks>
    public static bool IsPlausible(TaskOverviewRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var version = row.LastStreamVersion;
        return row.TaskId != Guid.Empty
            && version > 0
            && row.Status switch
            {
                WorkflowStatus.Created =>
                    row.AssignedAgentId is null && row.CurrentGenerationId is null && row.LatestTestResult is null
                    && version == 1,
                WorkflowStatus.Assigned =>
                    row.AssignedAgentId is not null
                    && (row.LatestTestResult is null || row.CurrentGenerationId is not null)
                    && (row.CurrentGenerationId is null
                        ? version == 2
                        : version >= (row.LatestTestResult is null ? 3 : 4)),
                WorkflowStatus.Completed =>
                    row.AssignedAgentId is not null && row.CurrentGenerationId is not null
                    && row.LatestTestResult == TestOutcome.Passed
                    && version >= 5,
                _ => false,
            };
    }

    private static bool IsCurrentGeneration(TaskOverviewRow row, GenerationId? generationId) =>
        generationId is not null
        && row.Status == WorkflowStatus.Assigned
        && row.CurrentGenerationId is not null
        && string.Equals(row.CurrentGenerationId, generationId.Value, StringComparison.Ordinal);

    private static TaskOverviewProjectionException Fault(TaskOverviewFault fault) => new(fault);
}
