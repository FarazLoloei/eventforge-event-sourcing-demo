using EventForge.Workflow.Application.Tasks;
using EventForge.Workflow.Domain;

namespace EventForge.Api.Tasks;

/// <summary>
/// The Task command endpoints (queries are in TaskQueryEndpoints.cs). HTTP is an adapter: it validates request shape, calls the existing
/// <see cref="TaskCommandHandler"/> once (never retrying) and maps its result. All business rules stay in the aggregate.
/// </summary>
public static partial class TaskEndpoints
{
    private const string Tag = "Task Commands";

    private const string VersionConflictDetail =
        "The task is not at the expected version. Nothing was written. Reload the task and reassess.";

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        MapQueries(app);

        var group = app.MapGroup("/api/tasks").WithTags(Tag);

        group.MapPost("", CreateAsync)
            .WithName("CreateTask")
            .WithSummary("Create a task")
            .WithDescription("Creates a task with a client-supplied id (expected stream version 0). Example body: {\"taskId\": \"9d56311e-acda-47c9-8a1c-f8e5d5605f4c\"}.")
            .Document(StatusCodes.Status201Created, notFound: false);

        group.MapPost("{id}/assign", AssignAsync)
            .WithName("AssignTask")
            .WithSummary("Assign an agent to a task")
            .WithDescription("Example body: {\"expectedVersion\": 1, \"agentId\": \"agent-demo-1\"}.")
            .Document(StatusCodes.Status200OK, notFound: true);

        group.MapPost("{id}/generations", RecordGenerationAsync)
            .WithName("RecordCodeGenerated")
            .WithSummary("Record a code generation")
            .WithDescription("Example body: {\"expectedVersion\": 2, \"generationId\": \"g1\", \"artifactRef\": \"synthetic://artifacts/demo/g1\"}. A new generation invalidates the previous test outcome.")
            .Document(StatusCodes.Status200OK, notFound: true);

        group.MapPost("{id}/test-results", RecordTestResultAsync)
            .WithName("RecordTestResult")
            .WithSummary("Record a test result for the current generation")
            .WithDescription("Example body: {\"expectedVersion\": 3, \"generationId\": \"g1\", \"outcome\": \"passed\"}. Outcome is exactly 'passed' or 'failed'.")
            .Document(StatusCodes.Status200OK, notFound: true);

        group.MapPost("{id}/complete", CompleteAsync)
            .WithName("CompleteTask")
            .WithSummary("Complete a task")
            .WithDescription("Example body: {\"expectedVersion\": 4}. Allowed only when the current generation has passed its tests.")
            .Document(StatusCodes.Status200OK, notFound: true);

        return app;
    }

    private static RouteHandlerBuilder Document(this RouteHandlerBuilder builder, int success, bool notFound)
    {
        builder
            .Produces<TaskCommandResponse>(success)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return notFound ? builder.ProducesProblem(StatusCodes.Status404NotFound) : builder;
    }

    private static Task<IResult> CreateAsync(
        CreateTaskRequest request, HttpContext context, TaskCommandHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(request.TaskId, "taskId");
        var correlation = v.CorrelationId(context.Request);
        return v.IsValid
            ? RunAsync(handler, new CreateTask(taskId!), 0, correlation, created: true, ct)
            : Task.FromResult(v.ToResult());
    }

    private static Task<IResult> AssignAsync(
        string id, AssignTaskRequest request, HttpContext context, TaskCommandHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        var expected = v.ExpectedVersion(request.ExpectedVersion);
        var agent = v.Agent(request.AgentId);
        var correlation = v.CorrelationId(context.Request);
        return v.IsValid
            ? RunAsync(handler, new AssignTask(taskId!, agent!), expected, correlation, created: false, ct)
            : Task.FromResult(v.ToResult());
    }

    private static Task<IResult> RecordGenerationAsync(
        string id, RecordGenerationRequest request, HttpContext context, TaskCommandHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        var expected = v.ExpectedVersion(request.ExpectedVersion);
        var generation = v.Generation(request.GenerationId);
        var artifact = v.Artifact(request.ArtifactRef);
        var correlation = v.CorrelationId(context.Request);
        return v.IsValid
            ? RunAsync(handler, new RecordCodeGenerated(taskId!, generation!, artifact!), expected, correlation, created: false, ct)
            : Task.FromResult(v.ToResult());
    }

    private static Task<IResult> RecordTestResultAsync(
        string id, RecordTestResultRequest request, HttpContext context, TaskCommandHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        var expected = v.ExpectedVersion(request.ExpectedVersion);
        var generation = v.Generation(request.GenerationId);
        var passed = v.Outcome(request.Outcome);
        var correlation = v.CorrelationId(context.Request);
        if (!v.IsValid)
        {
            return Task.FromResult(v.ToResult());
        }

        TaskCommand command = passed!.Value
            ? new RecordTestsPassed(taskId!, generation!)
            : new RecordTestsFailed(taskId!, generation!);
        return RunAsync(handler, command, expected, correlation, created: false, ct);
    }

    private static Task<IResult> CompleteAsync(
        string id, CompleteTaskRequest request, HttpContext context, TaskCommandHandler handler, CancellationToken ct)
    {
        var v = new RequestValidation();
        var taskId = v.TaskId(id, "id");
        var expected = v.ExpectedVersion(request.ExpectedVersion);
        var correlation = v.CorrelationId(context.Request);
        return v.IsValid
            ? RunAsync(handler, new CompleteTask(taskId!), expected, correlation, created: false, ct)
            : Task.FromResult(v.ToResult());
    }

    private static async Task<IResult> RunAsync(
        TaskCommandHandler handler,
        TaskCommand command,
        int expectedVersion,
        string? correlationId,
        bool created,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(command, expectedVersion, correlationId, ct);
        return Map(result, command.TaskId, created);
    }

    /// <summary>Maps each handler result to the approved HTTP contract. Never invents a version.</summary>
    private static IResult Map(TaskCommandResult result, TaskId taskId, bool created)
    {
        switch (result)
        {
            case CommandSucceeded succeeded:
                var body = new TaskCommandResponse(taskId.Value, succeeded.NewVersion);
                return created ? Results.Created($"/api/tasks/{taskId}", body) : Results.Ok(body);

            case CommandRejected { Code: RejectionCode.TaskNotFound }:
                return TaskProblems.NotFound();

            case CommandRejected { Code: RejectionCode.TaskIdMismatch }:
                return TaskProblems.Internal();

            case CommandRejected rejected:
                return TaskProblems.Of(
                    StatusCodes.Status409Conflict,
                    "workflow_rule_violation",
                    "Workflow rule violation",
                    "The command is not allowed in the current task state.",
                    ("rule", rejected.Code.ToString()));

            case VersionConflict { CurrentVersion: { } current }:
                return TaskProblems.Of(
                    StatusCodes.Status409Conflict, "version_conflict", "Version conflict", VersionConflictDetail,
                    ("currentVersion", current));

            case VersionConflict:
                return TaskProblems.Of(
                    StatusCodes.Status409Conflict, "version_conflict", "Version conflict", VersionConflictDetail);

            case InvalidHistory history:
                return TaskProblems.InvalidHistory(history.Reason, history.Position);

            case DuplicateEventId:
                return TaskProblems.Of(
                    StatusCodes.Status500InternalServerError,
                    "duplicate_event_id",
                    "Duplicate event id",
                    "An internal event id collision occurred. Nothing was written.");

            case CommitUnknown:
                return TaskProblems.Of(
                    StatusCodes.Status500InternalServerError,
                    "commit_outcome_unknown",
                    "Commit outcome unknown",
                    "The command may or may not have been committed. Read the task and compare its version before deciding; do not blindly resend.");

            default:
                return TaskProblems.Internal();
        }
    }
}
