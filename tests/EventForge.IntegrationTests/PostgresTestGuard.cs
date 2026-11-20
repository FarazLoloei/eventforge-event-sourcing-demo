using Npgsql;

namespace EventForge.IntegrationTests;

/// <summary>
/// Fail-closed checks that run before any database or role is created. They are a safety net against
/// pointing the suite at the wrong server, not proof that a server is disposable: only the person who starts
/// the isolated PostgreSQL decides that, by setting the opt-in variables below.
/// </summary>
internal static class PostgresTestGuard
{
    public const string EnabledVariable = "EVENTFORGE_IT_ENABLED";
    public const string EnabledValue = "disposable-postgres";
    public const string AdminConnectionVariable = "EVENTFORGE_IT_ADMIN_CONNECTION";

    // The owner's development PostgreSQL and the preserved Compose PostgreSQL.
    private static readonly int[] ForbiddenPorts = [5434, 5535];
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnabledVariable) == EnabledValue;

    /// <summary>Validates the administrative connection string without connecting. Messages never echo it.</summary>
    public static NpgsqlConnectionStringBuilder ValidateTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Refuse($"{AdminConnectionVariable} is not set");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw Refuse("the administrative connection string is not valid");
        }

        var host = builder.Host ?? "";
        if (!LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw Refuse("the host must be a single loopback address (localhost, 127.0.0.1 or ::1)");
        }

        if (ForbiddenPorts.Contains(builder.Port))
        {
            throw Refuse($"port {builder.Port} belongs to a non-disposable EventForge PostgreSQL");
        }

        if ((builder.Database ?? "").StartsWith("eventforge", StringComparison.OrdinalIgnoreCase) ||
            (builder.Username ?? "").StartsWith("eventforge", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse("the administrative database and user must not be EventForge objects");
        }

        return builder;
    }

    /// <summary>Server-side checks: PostgreSQL 17 and no existing EventForge database or role.</summary>
    public static async Task ValidateServerAsync(NpgsqlConnection admin)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT current_setting('server_version_num')::int / 10000,
                   (SELECT count(*) FROM pg_database WHERE datname LIKE 'eventforge%'),
                   (SELECT count(*) FROM pg_roles WHERE rolname LIKE 'eventforge%')
            """, admin);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        if (reader.GetInt32(0) != 17)
        {
            throw Refuse("the server must be PostgreSQL 17");
        }

        if (reader.GetInt64(1) != 0 || reader.GetInt64(2) != 0)
        {
            throw Refuse("the server already contains EventForge databases or roles; use a fresh, empty PostgreSQL");
        }
    }

    private static InvalidOperationException Refuse(string reason) =>
        new($"Refusing to run PostgreSQL integration tests: {reason}.");
}
