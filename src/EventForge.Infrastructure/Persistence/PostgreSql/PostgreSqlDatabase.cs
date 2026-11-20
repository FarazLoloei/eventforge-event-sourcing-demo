using System.Data;
using Npgsql;

namespace EventForge.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// Owns the single <see cref="NpgsqlDataSource"/> (and its connection pool) for the EventForge database.
/// Register it as a singleton: the container then disposes it at shutdown. Npgsql types stay inside
/// Infrastructure; later persistence adapters in this assembly use <see cref="DataSource"/>.
/// </summary>
public sealed class PostgreSqlDatabase : IAsyncDisposable
{
    /// <summary>The standard ASP.NET Core connection string name (ConnectionStrings:EventForge).</summary>
    public const string ConnectionStringName = "EventForge";

    /// <summary>The database schema that owns EventForge objects.</summary>
    public const string Schema = "eventforge";

    private const string VerificationSql = """
        SELECT current_database(),
               current_user::text,
               current_setting('server_version'),
               (r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolbypassrls OR r.rolreplication),
               EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema),
               COALESCE((SELECT has_schema_privilege(current_user, n.oid, 'USAGE')
                         FROM pg_namespace n WHERE n.nspname = @schema), false)
        FROM pg_roles r
        WHERE r.rolname = current_user
        """;

    private PostgreSqlDatabase(NpgsqlDataSource dataSource) => DataSource = dataSource;

    internal NpgsqlDataSource DataSource { get; }

    /// <summary>
    /// Creates the data source without opening a connection. Throws <see cref="InvalidOperationException"/>
    /// with a credential-free message when the connection string is missing or invalid.
    /// </summary>
    public static PostgreSqlDatabase Create(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set ConnectionStrings:{ConnectionStringName} " +
                $"(user secrets) or ConnectionStrings__{ConnectionStringName} (environment variable).");
        }

        try
        {
            return new PostgreSqlDatabase(new NpgsqlDataSourceBuilder(connectionString).Build());
        }
        catch (ArgumentException)
        {
            // The inner message is deliberately dropped: it could echo part of the connection string.
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not a valid Npgsql connection string.");
        }
    }

    /// <summary>
    /// Read-only check using the configured (runtime) credentials: connects, then runs one SELECT inside a
    /// READ ONLY transaction that is always rolled back. Expected failures are returned, not thrown.
    /// </summary>
    public async Task<PostgreSqlVerificationResult> VerifyConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(VerificationSql, connection, transaction);
            command.Parameters.AddWithValue("schema", Schema);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Failed("connect", "connected, but the current role was not found in pg_roles");
            }

            var privileged = reader.GetBoolean(3);
            var schemaExists = reader.GetBoolean(4);
            var schemaUsable = reader.GetBoolean(5);
            PostgreSqlCheck[] checks =
            [
                new("connect", true,
                    $"database '{reader.GetString(0)}', role '{reader.GetString(1)}', PostgreSQL {reader.GetString(2)}"),
                new("runtime-role-unprivileged", !privileged,
                    privileged ? "role has SUPERUSER, CREATEROLE, CREATEDB, BYPASSRLS or REPLICATION" : "no elevated role attributes"),
                new($"schema-{Schema}-exists", schemaExists, schemaExists ? "present" : $"schema '{Schema}' does not exist"),
                new($"schema-{Schema}-usage", schemaUsable, schemaUsable ? "USAGE granted" : $"role lacks USAGE on schema '{Schema}'"),
            ];

            // The transaction is never committed: disposal rolls it back.
            return new PostgreSqlVerificationResult(checks.All(c => c.Passed), checks);
        }
        catch (PostgresException exception)
        {
            return Failed("connect", $"PostgreSQL rejected the request (SQLSTATE {exception.SqlState}): {exception.MessageText}");
        }
        catch (NpgsqlException exception)
        {
            return Failed("connect", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    private static PostgreSqlVerificationResult Failed(string name, string detail) =>
        new(false, [new PostgreSqlCheck(name, false, detail)]);
}
