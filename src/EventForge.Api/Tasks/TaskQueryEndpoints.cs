using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;

namespace EventForge.Api.Tasks;

// The two Task query endpoints. They read the committed event history through TaskQueryHandler and never append.
public static partial class TaskEndpoints
{
    private const string QueryTag = "Task Queries";

    private static void MapQueries(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tasks").WithTags(QueryTag);

        group.MapGet("{id}", GetStateAsync)
            .WithName("GetTask")
            .WithSummary("Get the current task state")
            .WithDescription("Rebuilds the state from the committed event history. Returns status, assigned agent, current generation, current test outcome and the version to use as expectedVersion.")
            .Produces<TaskStateResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{id}/events", GetHistoryAsync)
            .WithName("GetTaskEvents")
            .WithSummary("Get the ordered task event history")
            .WithDescription("Returns every recorded event in ascending stream version order with safe event data. Raw payloads and metadata are never returned. The whole stream is validated first; an invalid history returns an error and no events.")
            .Produces<TaskHistoryResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetStateAsync(string id, TaskQueryHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        if (!v.IsValid)
        {
            return v.ToResult();
        }

        return await handler.GetStateAsync(taskId!, ct) switch
        {
            TaskStateFound found => Results.Ok(ToResponse(found.State)),
            QueryTaskNotFound => TaskProblems.NotFound(),
            QueryInvalidHistory invalid => TaskProblems.InvalidHistory(invalid.Reason, invalid.Position),
            _ => TaskProblems.Internal(),
        };
    }

    private static async Task<IResult> GetHistoryAsync(string id, TaskQueryHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        if (!v.IsValid)
        {
            return v.ToResult();
        }

        return await handler.GetHistoryAsync(taskId!, ct) switch
        {
            TaskHistoryFound found => Results.Ok(ToResponse(found)),
            QueryTaskNotFound => TaskProblems.NotFound(),
            QueryInvalidHistory invalid => TaskProblems.InvalidHistory(invalid.Reason, invalid.Position),
            _ => TaskProblems.Internal(),
        };
    }

    private static TaskStateResponse ToResponse(TaskState state) => new(
        state.Id!.Value,
        state.Status switch
        {
            WorkflowStatus.Created => "created",
            WorkflowStatus.Assigned => "assigned",
            WorkflowStatus.Completed => "completed",
            _ => throw new InvalidOperationException("A found task has no status."),
        },
        state.AssignedAgent?.Value,
        state.CurrentGeneration?.Value,
        state.CurrentTestOutcome switch
        {
            TestOutcome.Passed => "passed",
            TestOutcome.Failed => "failed",
            _ => null,
        },
        state.Version);

    private static TaskHistoryResponse ToResponse(TaskHistoryFound found) => new(
        found.TaskId.Value,
        found.Version,
        found.Events.Select(e => new TaskEventResponse(
            e.EventId, e.StreamVersion, e.EventType, e.SchemaVersion, e.RecordedAt.UtcDateTime, Data(e.Event))).ToList());

    /// <summary>The approved safe facts of each event, taken from the validated Domain event.</summary>
    private static Dictionary<string, string> Data(TaskEvent @event) => @event switch
    {
        TaskCreated e => new() { ["taskId"] = e.TaskId.ToString() },
        TaskAssigned e => new() { ["agentId"] = e.AgentId.Value },
        CodeGenerated e => new() { ["generationId"] = e.GenerationId.Value, ["artifactRef"] = e.ArtifactRef.Value },
        TestsPassed e => new() { ["generationId"] = e.GenerationId.Value },
        TestsFailed e => new() { ["generationId"] = e.GenerationId.Value },
        TaskCompleted => new(),
        _ => throw new InvalidOperationException("Unsupported event type."),
    };
}
