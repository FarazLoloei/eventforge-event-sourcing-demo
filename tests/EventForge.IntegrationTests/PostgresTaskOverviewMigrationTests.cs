using Npgsql;
using NpgsqlTypes;

namespace EventForge.IntegrationTests;

/// <summary>
/// Migration 002 (<c>eventforge.task_overview</c>): schema, constraints, re-execution, drift detection and runtime-role
/// privileges, in the isolated PostgreSQL only. Skipped unless explicitly enabled (see <see cref="PostgresTestGuard"/>).
/// Nothing here writes through an event store or projector (EVF-37): rows are inserted directly.
/// Negative statements run on their own autocommit connection so that no failure leaves a transaction aborted.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSql")]
public sealed class PostgresTaskOverviewMigrationTests(PostgresFixture fixture)
{
    private const string Table = "eventforge.task_overview";
    private const string PermissionDenied = "42501";
    private const string CheckViolation = "23514";

    private static readonly string[] ConstraintNames =
    [
        "task_overview_agent_id_valid", "task_overview_assigned_has_agent", "task_overview_completed_passed",
        "task_overview_created_has_no_assignment", "task_overview_generation_id_valid", "task_overview_pkey",
        "task_overview_result_requires_generation", "task_overview_status_valid", "task_overview_test_result_valid",
        "task_overview_version_positive",
    ];

    private string Admin => fixture.AdminConnectionString(fixture.DatabaseName);

    // ---- Schema -----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Table_HasTheSixExpectedColumnsTypesAndNullability()
    {
        Assert.Equal(
            "task_id:uuid:NO,status:text:NO,assigned_agent_id:text:YES,current_generation_id:text:YES," +
            "latest_test_result:text:YES,last_stream_version:integer:NO",
            await ScalarAsync(Admin,
                "SELECT string_agg(column_name::text || ':' || data_type::text || ':' || is_nullable::text, ',' ORDER BY ordinal_position) " +
                "FROM information_schema.columns WHERE table_schema = 'eventforge' AND table_name = 'task_overview'"));
    }

    [PostgresFact]
    public async Task Table_HasTheDocumentedConstraintsAndOnlyThePrimaryKeyIndex()
    {
        var expected = ConstraintNames.Order(StringComparer.Ordinal);
        Assert.Equal(
            string.Join(',', expected),
            await ScalarAsync(Admin,
                $"SELECT string_agg(conname, ',' ORDER BY conname COLLATE \"C\") FROM pg_constraint WHERE conrelid = '{Table}'::regclass"));
        Assert.Equal(
            "task_id",
            await ScalarAsync(Admin,
                $"SELECT string_agg(a.attname, ',') FROM pg_constraint c JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey) " +
                $"WHERE c.conrelid = '{Table}'::regclass AND c.contype = 'p'"));
        Assert.Equal(1L, await ScalarAsync(Admin,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'eventforge' AND tablename = 'task_overview'"));
    }

    [PostgresTheory]
    [InlineData("created", null, null, null, 1)]
    [InlineData("assigned", "agent-1", null, null, 2)]
    [InlineData("assigned", "agent-1", "g1", null, 3)]
    [InlineData("assigned", "agent-1", "g1", "passed", 4)]
    [InlineData("assigned", "agent-1", "g1", "failed", 4)]
    [InlineData("completed", "agent-1", "g1", "passed", 5)]
    [InlineData("assigned", "agent-1", "g2", null, int.MaxValue)]
    public async Task ValidRows_AreAcceptedFromTheRuntimeRole(
        string status, string? agent, string? generation, string? result, int version)
    {
        var id = Guid.NewGuid();

        await InsertAsync(fixture.AppConnectionString, id, status, agent, generation, result, version);

        Assert.Equal(version, await ScalarAsync(Admin, $"SELECT last_stream_version FROM {Table} WHERE task_id = '{id}'"));
    }

    [PostgresFact]
    public async Task Identifiers_AtTheDomainLimitOfOneHundredCharactersAreAccepted()
    {
        var id = Guid.NewGuid();

        await InsertAsync(fixture.AppConnectionString, id, "assigned", new string('a', 100), new string('g', 100), null, 3);

        Assert.Equal(1L, await ScalarAsync(Admin, $"SELECT count(*) FROM {Table} WHERE task_id = '{id}' AND char_length(assigned_agent_id) = 100"));
    }

    [PostgresTheory]
    [InlineData("deleted", "a", null, null, 2, "task_overview_status_valid")]
    [InlineData("assigned", "a", "g1", "unknown", 4, "task_overview_test_result_valid")]
    [InlineData("created", null, null, null, 0, "task_overview_version_positive")]
    [InlineData("created", null, null, null, -1, "task_overview_version_positive")]
    [InlineData("created", "a", null, null, 1, "task_overview_created_has_no_assignment")]
    [InlineData("created", null, "g1", null, 1, "task_overview_created_has_no_assignment")]
    [InlineData("created", null, "g1", "passed", 1, "task_overview_created_has_no_assignment")]
    [InlineData("assigned", null, null, null, 2, "task_overview_assigned_has_agent")]
    [InlineData("assigned", "a", null, "passed", 4, "task_overview_result_requires_generation")]
    [InlineData("completed", "a", "g1", "failed", 5, "task_overview_completed_passed")]
    [InlineData("completed", "a", "g1", null, 5, "task_overview_completed_passed")]
    [InlineData("completed", "a", null, null, 5, "task_overview_completed_passed")]
    [InlineData("assigned", "   ", null, null, 2, "task_overview_agent_id_valid")]
    [InlineData("assigned", "", null, null, 2, "task_overview_agent_id_valid")]
    [InlineData("assigned", "a\tb", null, null, 2, "task_overview_agent_id_valid")]
    [InlineData("assigned", "a\u0085b", null, null, 2, "task_overview_agent_id_valid")]
    [InlineData("assigned", "a", "g\nx", null, 3, "task_overview_generation_id_valid")]
    [InlineData("assigned", "a", "  ", null, 3, "task_overview_generation_id_valid")]
    public async Task InvalidRows_AreRejectedByTheNamedConstraint(
        string status, string? agent, string? generation, string? result, int version, string constraint)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(fixture.AppConnectionString, Guid.NewGuid(), status, agent, generation, result, version));

        Assert.Equal(CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identifiers_LongerThanOneHundredCharactersAreRejected(bool agent)
    {
        var tooLong = new string('x', 101);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => agent
            ? InsertAsync(fixture.AppConnectionString, Guid.NewGuid(), "assigned", tooLong, null, null, 2)
            : InsertAsync(fixture.AppConnectionString, Guid.NewGuid(), "assigned", "a", tooLong, null, 3));

        Assert.Equal(CheckViolation, exception.SqlState);
        Assert.Equal(agent ? "task_overview_agent_id_valid" : "task_overview_generation_id_valid", exception.ConstraintName);
    }

    [PostgresFact]
    public async Task MissingStatus_IsRejectedAsNotNull()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(fixture.AppConnectionString, Guid.NewGuid(), null, null, null, null, 1));

        Assert.Equal("23502", exception.SqlState);
    }

    [PostgresFact]
    public async Task DuplicateTaskId_IsRejectedByThePrimaryKey()
    {
        var id = Guid.NewGuid();
        await InsertAsync(fixture.AppConnectionString, id, "created", null, null, null, 1);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(fixture.AppConnectionString, id, "created", null, null, null, 1));

        Assert.Equal("23505", exception.SqlState);
        Assert.Equal("task_overview_pkey", exception.ConstraintName);
    }

    // ---- Migration safety ---------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Migration_CanBeRunAgain_WithoutChangingExistingRowsOrTheSchema()
    {
        var id = Guid.NewGuid();
        await InsertAsync(fixture.AppConnectionString, id, "assigned", "agent-1", "g1", "failed", 4);
        var before = await SnapshotAsync(Admin, "task_overview");

        await PostgresFixture.RunMigrationAsync(fixture.MigratorConnectionString, PostgresFixture.TaskOverviewMigration);

        Assert.Equal(before, await SnapshotAsync(Admin, "task_overview"));
        Assert.Equal("assigned|agent-1|g1|failed|4", await ScalarAsync(Admin,
            $"SELECT concat_ws('|', status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version) FROM {Table} WHERE task_id = '{id}'"));
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE eventforge.task_overview ADD COLUMN extra integer", "(columns)")]
    [InlineData("ALTER TABLE eventforge.task_overview ALTER COLUMN assigned_agent_id SET NOT NULL", "(columns)")]
    [InlineData("ALTER TABLE eventforge.task_overview DROP CONSTRAINT task_overview_version_positive", "(constraints)")]
    [InlineData("ALTER TABLE eventforge.task_overview ADD CONSTRAINT task_overview_extra CHECK (true)", "(constraints)")]
    [InlineData(
        "ALTER TABLE eventforge.task_overview DROP CONSTRAINT task_overview_version_positive, " +
        "ADD CONSTRAINT task_overview_version_positive CHECK (last_stream_version >= 0)",
        "(constraints)")]
    [InlineData("CREATE INDEX task_overview_status_ix ON eventforge.task_overview (status)", "(indexes or triggers)")]
    public async Task Migration_FailsOnSchemaDrift_AndNeitherRepairsItNorTouchesRows(string drift, string area)
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            var admin = fixture.AdminConnectionString(database);
            await ExecuteAsync(admin,
                $"INSERT INTO {Table} (task_id, status, assigned_agent_id, last_stream_version) VALUES (gen_random_uuid(), 'assigned', 'agent-1', 2)");
            await ExecuteAsync(admin, drift);
            var before = await SnapshotAsync(admin, "task_overview");

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration));

            Assert.Contains($"Schema drift in eventforge.task_overview {area}", exception.MessageText);
            Assert.Equal(before, await SnapshotAsync(admin, "task_overview")); // drift is still there: nothing was repaired
            Assert.Equal(1L, await ScalarAsync(admin, $"SELECT count(*) FROM {Table}"));
        });
    }

    [PostgresFact]
    public async Task Migration_FailsOnADifferentExistingTable_AndKeepsItUntouched()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            var admin = fixture.AdminConnectionString(database);
            await ExecuteAsync(admin,
                $"CREATE TABLE {Table} (task_id uuid PRIMARY KEY); ALTER TABLE {Table} OWNER TO eventforge_migrator; " +
                $"INSERT INTO {Table} VALUES (gen_random_uuid())");
            var before = await SnapshotAsync(admin, "task_overview");

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration));

            Assert.Contains("Schema drift in eventforge.task_overview (columns)", exception.MessageText);
            Assert.Equal(before, await SnapshotAsync(admin, "task_overview"));
            Assert.Equal(1L, await ScalarAsync(admin, $"SELECT count(*) FROM {Table}"));
        });
    }

    [PostgresFact]
    public async Task Migration_RefusesToRunAsAnotherRole_AndCreatesNothing()
    {
        await WithScratchDatabaseAsync(async (database, _) =>
        {
            var admin = fixture.AdminConnectionString(database);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(admin, PostgresFixture.TaskOverviewMigration));

            Assert.Contains("Run this migration as eventforge_migrator", exception.MessageText);
            Assert.Equal(true, await ScalarAsync(admin, $"SELECT to_regclass('{Table}') IS NULL"));
        });
    }

    [PostgresFact]
    public async Task Migration_RefusesAnUnownedSchema_AndCreatesNothing()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            var admin = fixture.AdminConnectionString(database);
            await ExecuteAsync(admin, "ALTER SCHEMA eventforge OWNER TO CURRENT_USER");

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration));

            Assert.Contains("not owned by eventforge_migrator", exception.MessageText);
            Assert.Equal(true, await ScalarAsync(admin, $"SELECT to_regclass('{Table}') IS NULL"));
        });
    }

    [PostgresFact]
    public async Task Migration_RefusesAnExistingTableOwnedByAnotherRole()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            var admin = fixture.AdminConnectionString(database);
            await ExecuteAsync(admin, $"ALTER TABLE {Table} OWNER TO CURRENT_USER");

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration));

            Assert.Contains("Table eventforge.task_overview is not owned by eventforge_migrator", exception.MessageText);
        });
    }

    [PostgresFact]
    public async Task Migration_LeavesTheEventStoreTableUnchanged()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            var admin = fixture.AdminConnectionString(database);
            await PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.EventStoreMigration);
            var before = await SnapshotAsync(admin, "events");

            await PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration);
            await PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration);

            Assert.Equal(before, await SnapshotAsync(admin, "events"));
            Assert.Equal(false, await ScalarAsync(admin,
                "SELECT has_table_privilege('eventforge_app', 'eventforge.events', 'UPDATE') " +
                "OR has_table_privilege('eventforge_app', 'eventforge.events', 'DELETE')"));
        });
    }

    // ---- Least privilege ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RuntimeRole_HasExactlyTheDocumentedEffectivePrivileges()
    {
        await AssertRuntimePrivilegesAsync(Admin);
    }

    [PostgresFact]
    public async Task RuntimeRole_CanSelectInsertAndUpdateTheProjectorColumns()
    {
        var id = Guid.NewGuid();
        await InsertAsync(fixture.AppConnectionString, id, "created", null, null, null, 1);

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand(
            $"UPDATE {Table} SET status = 'assigned', assigned_agent_id = 'agent-1', current_generation_id = 'g1', " +
            "latest_test_result = 'failed', last_stream_version = 4 WHERE task_id = @id", connection);
        update.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        Assert.Equal(1, await update.ExecuteNonQueryAsync());

        await using var select = new NpgsqlCommand(
            $"SELECT concat_ws('|', status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version) " +
            $"FROM {Table} WHERE task_id = @id", connection);
        select.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        Assert.Equal("assigned|agent-1|g1|failed|4", await select.ExecuteScalarAsync());
    }

    [PostgresTheory]
    [InlineData("UPDATE eventforge.task_overview SET task_id = gen_random_uuid()")]
    [InlineData("DELETE FROM eventforge.task_overview")]
    [InlineData("TRUNCATE eventforge.task_overview")]
    [InlineData("ALTER TABLE eventforge.task_overview ADD COLUMN extra integer")]
    [InlineData("ALTER TABLE eventforge.task_overview DROP COLUMN status")]
    [InlineData("DROP TABLE eventforge.task_overview")]
    [InlineData("CREATE INDEX rogue_ix ON eventforge.task_overview (status)")]
    [InlineData("CREATE TABLE eventforge.rogue (id integer)")]
    public async Task RuntimeRole_CannotChangeTheKeyDeleteTruncateAlterDropOrCreate(string statement)
    {
        // A fresh autocommit connection per statement: the denial cannot leave a transaction aborted.
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        });

        Assert.Equal(PermissionDenied, exception.SqlState);
    }

    [PostgresFact]
    public async Task Migration_RevokesExtraPrivilegesFromTheRuntimeRoleAndPublic()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            var admin = fixture.AdminConnectionString(database);
            await ExecuteAsync(admin,
                $"GRANT UPDATE, DELETE, TRUNCATE ON {Table} TO eventforge_app; " +
                $"GRANT UPDATE (task_id) ON {Table} TO eventforge_app; " +
                $"GRANT SELECT ON {Table} TO PUBLIC");

            await PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration);

            await AssertRuntimePrivilegesAsync(admin);
        });
    }

    [PostgresFact]
    public async Task Migration_FailsWhenTheRuntimeRoleCanCreateObjectsInTheSchema()
    {
        await WithScratchDatabaseAsync(async (database, migrator) =>
        {
            await PostgresFixture.RunMigrationAsync(migrator);
            await ExecuteAsync(fixture.AdminConnectionString(database), "GRANT CREATE ON SCHEMA eventforge TO eventforge_app");

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                PostgresFixture.RunMigrationAsync(migrator, PostgresFixture.TaskOverviewMigration));

            Assert.Contains("exactly SELECT, INSERT and column UPDATE", exception.MessageText);
        });
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static async Task AssertRuntimePrivilegesAsync(string adminConnectionString)
    {
        Assert.Equal("SELECT,INSERT", await ScalarAsync(adminConnectionString, TablePrivilegesSql));
        Assert.Equal(
            "status,assigned_agent_id,current_generation_id,latest_test_result,last_stream_version",
            await ScalarAsync(adminConnectionString, ColumnUpdatePrivilegesSql));
        Assert.Equal(false, await ScalarAsync(adminConnectionString,
            "SELECT has_schema_privilege('eventforge_app', 'eventforge', 'CREATE')"));
        Assert.Equal(0L, await ScalarAsync(adminConnectionString,
            $"SELECT count(*) FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) x WHERE c.oid = '{Table}'::regclass AND x.grantee = 0"));
        Assert.Equal("eventforge_migrator", await ScalarAsync(adminConnectionString,
            $"SELECT pg_get_userbyid(relowner)::text FROM pg_class WHERE oid = '{Table}'::regclass"));
        Assert.Equal(false, await ScalarAsync(adminConnectionString,
            "SELECT r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolbypassrls OR r.rolreplication " +
            "FROM pg_roles r WHERE r.rolname = 'eventforge_app'"));
    }

    private static string TablePrivilegesSql =>
        "SELECT concat_ws(',', " +
        string.Join(", ", new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" }
            .Select(p => $"CASE WHEN has_table_privilege('eventforge_app', '{Table}', '{p}') THEN '{p}' END")) + ")";

    private static string ColumnUpdatePrivilegesSql =>
        "SELECT concat_ws(',', " +
        string.Join(", ", new[]
            {
                "task_id", "status", "assigned_agent_id", "current_generation_id", "latest_test_result", "last_stream_version",
            }.Select(c => $"CASE WHEN has_column_privilege('eventforge_app', '{Table}', '{c}', 'UPDATE') THEN '{c}' END")) + ")";

    /// <summary>Columns, constraints, indexes, owner and ACL of one table in the eventforge schema, as one comparable text.</summary>
    private static Task<object?> SnapshotAsync(string adminConnectionString, string table) => ScalarAsync(adminConnectionString,
        $"""
        SELECT string_agg(s.item, E'\n' ORDER BY s.item COLLATE "C") FROM (
          SELECT 'column ' || a.attname || ' ' || format_type(a.atttypid, a.atttypmod) || ' ' || a.attnotnull::text AS item
          FROM pg_attribute a WHERE a.attrelid = 'eventforge.{table}'::regclass AND a.attnum > 0 AND NOT a.attisdropped
          UNION ALL
          SELECT 'constraint ' || conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'eventforge.{table}'::regclass
          UNION ALL
          SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'eventforge' AND tablename = '{table}'
          UNION ALL
          SELECT 'owner ' || pg_get_userbyid(relowner)::text || ' acl ' || COALESCE(relacl::text, '') FROM pg_class WHERE oid = 'eventforge.{table}'::regclass
        ) s
        """);

    private static async Task InsertAsync(
        string connectionString, Guid id, string? status, string? agent, string? generation, string? result, int version)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {Table} (task_id, status, assigned_agent_id, current_generation_id, latest_test_result, last_stream_version) " +
            "VALUES (@id, @status, @agent, @generation, @result, @version)", connection);
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;
        command.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("agent", NpgsqlDbType.Text).Value = (object?)agent ?? DBNull.Value;
        command.Parameters.Add("generation", NpgsqlDbType.Text).Value = (object?)generation ?? DBNull.Value;
        command.Parameters.Add("result", NpgsqlDbType.Text).Value = (object?)result ?? DBNull.Value;
        command.Parameters.Add("version", NpgsqlDbType.Integer).Value = version;
        await command.ExecuteNonQueryAsync();
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await PostgresFixture.ExecuteAsync(connection, sql);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
