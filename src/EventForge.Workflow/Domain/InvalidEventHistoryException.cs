namespace EventForge.Workflow.Domain;

/// <summary>
/// Thrown when a recorded event sequence cannot be a valid schema v1 Task history, or an event is malformed.
/// The message contains only the 1-based position and the event type name, never identifiers or payload values.
/// </summary>
public sealed class InvalidEventHistoryException : Exception
{
    public InvalidEventHistoryException(string message)
        : base(message)
    {
    }
}
