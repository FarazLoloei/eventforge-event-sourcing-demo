using EventForge.Workflow.Domain;

namespace EventForge.Workflow.Application.Tasks;

/// <summary>
/// The Application read port for the <c>task_overview</c> projection (CQRS read side). An implementation reads only
/// committed projection state from the primary database. It never loads or folds the event stream to build a state:
/// the single event-store question it may ask is whether any Task event exists, and only when no row was found.
/// </summary>
public interface ITaskOverviewReader
{
    /// <summary>Looks up the committed overview row of a task.</summary>
    Task<TaskOverviewLookup> FindAsync(TaskId taskId, CancellationToken cancellationToken);
}

/// <summary>The outcome of a projection lookup.</summary>
public abstract record TaskOverviewLookup;

/// <summary>A committed projection row exists for the task.</summary>
public sealed record TaskOverviewFound(TaskOverviewRow Row) : TaskOverviewLookup;

/// <summary>
/// No projection row exists. <see cref="TaskEventsExist"/> tells a genuinely unknown task (<c>false</c>) from a Task
/// event stream that has no row, such as a stream written before the projection existed (<c>true</c>); the second
/// case is a projection fault, never "not found".
/// </summary>
public sealed record TaskOverviewAbsent(bool TaskEventsExist) : TaskOverviewLookup;
