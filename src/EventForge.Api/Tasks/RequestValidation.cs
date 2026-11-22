using System.Text.RegularExpressions;
using EventForge.Workflow.Domain;

namespace EventForge.Api.Tasks;

/// <summary>
/// Collects shape and value errors before the command handler is invoked. Identifier rules come from the Domain value
/// types; only API-boundary rules (GUID format, synthetic artifact references, outcome names, header) live here.
/// Messages are fixed text and never echo the submitted value.
/// </summary>
internal sealed partial class RequestValidation
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private const string Required = "A value is required.";
    private const string IdentifierRule = "Must be non-blank, at most 100 characters and contain no control characters.";
    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    [GeneratedRegex(@"\Asynthetic://[A-Za-z0-9][A-Za-z0-9._/-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SyntheticArtifact();

    public bool IsValid => _errors.Count == 0;

    public IResult ToResult() => TaskProblems.Invalid(_errors);

    public TaskId? TaskId(string? text, string field)
    {
        if (text is null)
        {
            Add(field, Required);
            return null;
        }

        if (!Guid.TryParseExact(text, "D", out var guid) || guid == Guid.Empty)
        {
            Add(field, "Must be a non-empty GUID in canonical D format.");
            return null;
        }

        return new TaskId(guid);
    }

    public int ExpectedVersion(int? value)
    {
        if (value is null)
        {
            Add("expectedVersion", Required);
            return 0;
        }

        if (value < 0)
        {
            Add("expectedVersion", "Must be an integer greater than or equal to 0.");
        }

        return value.Value;
    }

    public AgentId? Agent(string? value) => Identifier("agentId", value, v => new AgentId(v), IdentifierRule);

    public GenerationId? Generation(string? value) => Identifier("generationId", value, v => new GenerationId(v), IdentifierRule);

    public ArtifactRef? Artifact(string? value)
    {
        if (value is not null && !SyntheticArtifact().IsMatch(value))
        {
            Add("artifactRef", "Must be a synthetic reference of the form synthetic://<name> (letters, digits, '.', '_', '-', '/').");
            return null;
        }

        return Identifier("artifactRef", value, v => new ArtifactRef(v), "Must be at most 500 characters.");
    }

    /// <summary>Returns true for a passed result and false for failed; null (with an error) for anything else.</summary>
    public bool? Outcome(string? value)
    {
        switch (value)
        {
            case null:
                Add("outcome", Required);
                return null;
            case "passed":
                return true;
            case "failed":
                return false;
            default:
                Add("outcome", "Must be exactly 'passed' or 'failed'.");
                return null;
        }
    }

    /// <summary>The optional correlation header: absent is null; present must satisfy the handler's rule.</summary>
    public string? CorrelationId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(CorrelationHeader, out var values))
        {
            return null;
        }

        var value = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(char.IsControl))
        {
            Add(CorrelationHeader, "Must be a single non-blank value of at most 100 characters without control characters.");
            return null;
        }

        return value;
    }

    private T? Identifier<T>(string field, string? value, Func<string, T> create, string rule)
        where T : class
    {
        if (value is null)
        {
            Add(field, Required);
            return null;
        }

        try
        {
            return create(value);
        }
        catch (ArgumentException)
        {
            Add(field, rule);
            return null;
        }
    }

    private void Add(string field, string message) => _errors[field] = [message];
}
