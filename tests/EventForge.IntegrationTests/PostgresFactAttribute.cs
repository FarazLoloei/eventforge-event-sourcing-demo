namespace EventForge.IntegrationTests;

/// <summary>
/// A test that needs the isolated PostgreSQL. It is skipped unless the opt-in variable is set, so a plain
/// <c>dotnet test</c> never connects anywhere. The CI integration job sets it and fails on any skipped test.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestGuard.IsEnabled)
        {
            Skip = $"Set {PostgresTestGuard.EnabledVariable}={PostgresTestGuard.EnabledValue} and " +
                   $"{PostgresTestGuard.AdminConnectionVariable} to run against an isolated PostgreSQL 17.";
        }
    }
}

/// <summary>The theory counterpart of <see cref="PostgresFactAttribute"/>, skipped under the same conditions.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresTestGuard.IsEnabled)
        {
            Skip = $"Set {PostgresTestGuard.EnabledVariable}={PostgresTestGuard.EnabledValue} and " +
                   $"{PostgresTestGuard.AdminConnectionVariable} to run against an isolated PostgreSQL 17.";
        }
    }
}
