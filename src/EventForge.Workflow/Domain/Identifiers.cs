namespace EventForge.Workflow.Domain;

/// <summary>Identifies a task and its event stream. Must not be <see cref="Guid.Empty"/>.</summary>
public sealed record TaskId
{
    public TaskId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The task id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>Opaque agent identifier: non-blank, at most 100 characters, no control characters.</summary>
public sealed record AgentId
{
    public AgentId(string value)
    {
        Value = IdentifierRules.Check(value, 100, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Opaque code generation identifier, unique within a task: non-blank, at most 100 characters,
/// no control characters. Comparison is ordinal.
/// </summary>
public sealed record GenerationId
{
    public GenerationId(string value)
    {
        Value = IdentifierRules.Check(value, 100, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Opaque reference to a generated artifact: non-blank, at most 500 characters, no control characters.
/// The Domain does not interpret it. Restricting references to synthetic values is the responsibility of the
/// adapter or demo boundary, which also validates untrusted input.
/// </summary>
public sealed record ArtifactRef
{
    public ArtifactRef(string value)
    {
        Value = IdentifierRules.Check(value, 500, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

internal static class IdentifierRules
{
    internal static string Check(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value must not be blank.", parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"The value must be at most {maxLength} characters.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("The value must not contain control characters.", parameterName);
        }

        return value;
    }
}
