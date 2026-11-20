using System.Text.RegularExpressions;

namespace EventForge.Workflow.Application.EventStore;

/// <summary>
/// Rule for stream types and event types: a letter followed by letters, digits, '.', '_' or '-',
/// at most 100 characters. The migration enforces the same pattern with a CHECK constraint.
/// </summary>
internal static class EventStoreNames
{
    private static readonly Regex Pattern = new("^[A-Za-z][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);

    internal static void Validate(string? value, string parameterName)
    {
        // A '$' anchor would accept a trailing newline, so compare the whole match length instead.
        var match = value is null ? null : Pattern.Match(value);
        if (match is null || !match.Success || match.Length != value!.Length)
        {
            throw new ArgumentException(
                "The name must start with a letter and contain only letters, digits, '.', '_' or '-' (at most 100 characters).",
                parameterName);
        }
    }
}
