using System.Security.Cryptography;
using Npgsql;

namespace EventForge.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}

/// <summary>
/// Creates, inside the isolated PostgreSQL that passed <see cref="PostgresTestGuard"/>, the two EventForge roles
/// and one throwaway database provisioned like production (schema owned by the migrator, runtime role with
/// USAGE only), runs the migration once, and removes everything afterwards.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _migratorPassword = RandomSecret();
    private readonly string _appPassword = RandomSecret();
    private NpgsqlConnectionStringBuilder? _admin;
    private bool _rolesCreated;

    public string DatabaseName { get; } = NewDatabaseName();

    public string AppConnectionString => For("eventforge_app", _appPassword, DatabaseName);

    public string MigratorConnectionString => For("eventforge_migrator", _migratorPassword, DatabaseName);

    public string AdminConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Admin.ConnectionString) { Database = database }.ConnectionString;

    public static string NewDatabaseName() => "eventforge_it_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public async Task InitializeAsync()
    {
        if (!PostgresTestGuard.IsEnabled)
        {
            return; // every test is skipped; nothing is touched
        }

        _admin = PostgresTestGuard.ValidateTarget(Environment.GetEnvironmentVariable(PostgresTestGuard.AdminConnectionVariable));
        await using (var connection = new NpgsqlConnection(_admin.ConnectionString))
        {
            await connection.OpenAsync();
            await PostgresTestGuard.ValidateServerAsync(connection); // refuses before anything is created
        }

        // From here on resources may be created. The guard proved the server had no EventForge roles or
        // databases, so the cleanup below only ever removes what this fixture created.
        try
        {
            _rolesCreated = true;
            await using (var connection = new NpgsqlConnection(_admin.ConnectionString))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, RoleSql("eventforge_migrator", _migratorPassword));
                await ExecuteAsync(connection, RoleSql("eventforge_app", _appPassword));
            }

            await CreateProvisionedDatabaseAsync(DatabaseName);
            await RunMigrationAsync(MigratorConnectionString);
        }
        catch
        {
            // xunit does not dispose a fixture whose initialisation failed. Do not let a cleanup failure
            // hide the original error.
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                Console.Error.WriteLine($"Test cleanup also failed: {cleanupFailure.GetType().Name}");
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_admin is null || !_rolesCreated)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_admin.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)");
        await ExecuteAsync(connection, "DROP ROLE IF EXISTS eventforge_app");
        await ExecuteAsync(connection, "DROP ROLE IF EXISTS eventforge_migrator");
    }

    /// <summary>Creates a database with the same ownership and privileges as the provisioning script.</summary>
    public async Task CreateProvisionedDatabaseAsync(string database)
    {
        await using (var connection = new NpgsqlConnection(Admin.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"CREATE DATABASE \"{database}\" OWNER eventforge_migrator");
            await ExecuteAsync(connection,
                $"REVOKE ALL ON DATABASE \"{database}\" FROM PUBLIC; " +
                $"GRANT CONNECT ON DATABASE \"{database}\" TO eventforge_migrator, eventforge_app");
        }

        await using var inDatabase = new NpgsqlConnection(AdminConnectionString(database));
        await inDatabase.OpenAsync();
        await ExecuteAsync(inDatabase,
            "CREATE SCHEMA eventforge AUTHORIZATION eventforge_migrator; " +
            "REVOKE ALL ON SCHEMA eventforge FROM PUBLIC; " +
            "GRANT USAGE ON SCHEMA eventforge TO eventforge_app");
    }

    public async Task DropDatabaseAsync(string database)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(Admin.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
    }

    public string ConnectionStringFor(string role, string database) =>
        role == "eventforge_app" ? For(role, _appPassword, database) : For(role, _migratorPassword, database);

    /// <summary>Runs the repository migration file as one command.</summary>
    public static async Task RunMigrationAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, await File.ReadAllTextAsync(MigrationPath()));
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsAdminAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString(database));
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    private NpgsqlConnectionStringBuilder Admin =>
        _admin ?? throw new InvalidOperationException("The PostgreSQL integration environment is not enabled.");

    private string For(string user, string password, string database) =>
        new NpgsqlConnectionStringBuilder(Admin.ConnectionString)
        {
            Username = user,
            Password = password,
            Database = database,
        }.ConnectionString;

    // Random hex only, so interpolating it into CREATE ROLE is safe.
    private static string RoleSql(string role, string password) =>
        $"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'";

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string MigrationPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "db", "migrations", "001-create-event-store.sql");
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "EventForge.slnx")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("db/migrations/001-create-event-store.sql was not found above the test output folder.");
    }
}
