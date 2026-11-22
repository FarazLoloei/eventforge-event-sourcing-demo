using EventForge.Workflow.Application.Tasks;
using Microsoft.AspNetCore.Diagnostics;

namespace EventForge.Api.Tasks;

/// <summary>
/// RFC 9457 Problem Details with stable <c>code</c> values (docs/api-contracts.md, Section 5). Bodies contain fixed
/// safe text only: never exception messages, SQL, connection details or stored payloads.
/// </summary>
internal static class TaskProblems
{
    public static IResult Of(int status, string code, string title, string detail, params (string Name, object? Value)[] extensions)
    {
        var members = new Dictionary<string, object?> { ["code"] = code };
        foreach (var (name, value) in extensions)
        {
            members[name] = value;
        }

        return Results.Problem(detail: detail, statusCode: status, title: title, type: Type(code), extensions: members);
    }

    public static IResult Invalid(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors,
            detail: "One or more request values are invalid.",
            title: "Invalid request",
            type: Type("invalid_request"),
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

    public static IResult NotFound() =>
        Of(StatusCodes.Status404NotFound, "task_not_found", "Task not found", "The task does not exist.");

    public static IResult Internal() =>
        Of(StatusCodes.Status500InternalServerError, "internal_error", "Internal error", "The request could not be processed.");

    public static IResult InvalidHistory(InvalidHistoryReason reason, int? position)
    {
        const string detail = "The stored event history cannot be read as a valid schema v1 Task history. Nothing was written.";
        return position is { } at
            ? Of(StatusCodes.Status500InternalServerError, "invalid_event_history", "Invalid event history", detail,
                ("reason", reason.ToString()), ("position", at))
            : Of(StatusCodes.Status500InternalServerError, "invalid_event_history", "Invalid event history", detail,
                ("reason", reason.ToString()));
    }

    /// <summary>Global handler: unreadable request bodies become 400 problems (a wrong content type is answered 415 by the framework itself), everything else a safe 500.</summary>
    public static Task HandleExceptionAsync(HttpContext context)
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var result = error is BadHttpRequestException
            ? Of(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request", "The request body is missing, malformed or contains unknown members.")
            : Internal();

        return result.ExecuteAsync(context);
    }

    private static string Type(string code) => $"urn:eventforge:problem:{code}";
}
