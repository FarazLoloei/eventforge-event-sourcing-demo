using Npgsql;

namespace EventForge.IntegrationTests;

/// <summary>Migration re-execution, drift detection and runtime-role privileges. Skipped unless explicitly enabled.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class PostgresMigrationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Migration_CanBeRunAgainOnAnUpToDateDatabase()
    {
        // The fixture already applied it once; a second run must verify and change nothing.
        await PostgresFixture.RunMigrationAsync(fixture.MigratorConnectionString);

        Assert.Equal(8L, await ScalarAsync(fixture.AdminConnectionString(fixture.DatabaseName),
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'eventforge.events'::regclass AND contype <> 'n'"));
    }

    [PostgresFact]
    public async Task Migration_FailsOnADifferentExistingTable_AndKeepsItUntouched()
    {
        // A table that lacks the unique constraint, in a scratch database.
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await fixture.ExecuteAsAdminAsync(database,
                "CREATE TABLE eventforge.events (event_id uuid PRIMARY KEY); ALTER TABLE eventforge.events OWNER TO eventforge_migrator");

            var exception = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.RunMigrationAsync(migrator));
            Assert.Contains("Schema drift", exception.MessageText);
            Assert.Equal(1L, await ScalarAsync(fixture.AdminConnectionString(database),
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'eventforge' AND table_name = 'events'"));
        });
    }

    [PostgresFact]
    public async Task Migration_FailsWhenTheRuntimeRoleCanCreateObjectsInTheSchema()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            await fixture.ExecuteAsAdminAsync(database, "GRANT CREATE ON SCHEMA eventforge TO eventforge_app");

            var exception = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.RunMigrationAsync(migrator));
            Assert.Contains("exactly SELECT and INSERT", exception.MessageText);
        });
    }

    [PostgresFact]
    public async Task Migration_RevokesExtraTablePrivilegesFromTheRuntimeRole()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            await fixture.ExecuteAsAdminAsync(database, "GRANT UPDATE, DELETE ON eventforge.events TO eventforge_app");

            await PostgresFixture.RunMigrationAsync(migrator);

            Assert.Equal(false, await ScalarAsync(fixture.AdminConnectionString(database),
                "SELECT has_table_privilege('eventforge_app', 'eventforge.events', 'DELETE') OR has_any_column_privilege('eventforge_app', 'eventforge.events', 'UPDATE')"));
        });
    }

    [PostgresFact]
    public async Task RuntimeRole_CanOnlySelectAndInsert()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        Assert.NotNull(await new NpgsqlCommand("SELECT count(*) FROM eventforge.events", connection).ExecuteScalarAsync());

        foreach (var statement in new[]
                 {
                     "UPDATE eventforge.events SET event_type = 'X'",
                     "DELETE FROM eventforge.events",
                     "TRUNCATE eventforge.events",
                     "CREATE TABLE eventforge.rogue (id int)",
                     "DROP TABLE eventforge.events",
                     "ALTER TABLE eventforge.events ADD COLUMN extra int",
                 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Equal("42501", exception.SqlState); // insufficient_privilege (or "must be owner")
        }
    }

    private async Task WithScratchDatabaseAsync(Func<string, string, Task> test)
    {
        var database = PostgresFixture.NewDatabaseName();
        try
        {
            await fixture.CreateProvisionedDatabaseAsync(database); // inside try: a partial create is still dropped
            await test(database, fixture.ConnectionStringFor("eventforge_migrator", database));
        }
        finally
        {
            await fixture.DropDatabaseAsync(database);
        }
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
