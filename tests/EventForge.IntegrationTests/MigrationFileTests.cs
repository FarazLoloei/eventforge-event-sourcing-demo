namespace EventForge.IntegrationTests;

/// <summary>
/// File-level checks on the repository migrations. They never connect to a database, so they run in every
/// <c>dotnet test</c>. They are not evidence that the SQL is valid; the PostgreSQL tests are.
/// </summary>
public sealed class MigrationFileTests
{
    [Fact]
    public void Fixture_AppliesEveryMigrationFileInTheRepositoryInOrder()
    {
        var directory = Path.GetDirectoryName(PostgresFixture.MigrationPath(PostgresFixture.EventStoreMigration))!;
        var onDisk = Directory.GetFiles(directory, "*.sql")
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(onDisk, PostgresFixture.Migrations.ToArray());
        Assert.Equal(
            new[] { PostgresFixture.EventStoreMigration, PostgresFixture.TaskOverviewMigration },
            PostgresFixture.Migrations.ToArray());
    }

    [Theory]
    [InlineData(PostgresFixture.EventStoreMigration)]
    [InlineData(PostgresFixture.TaskOverviewMigration)]
    public void Migration_IsOnePlainSqlTransactionWithoutPsqlMetaCommands(string migration)
    {
        var lines = File.ReadAllLines(PostgresFixture.MigrationPath(migration))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        Assert.Equal("BEGIN;", lines[0]);
        Assert.Equal("COMMIT;", lines[^1]);
        Assert.DoesNotContain(lines, line => line.StartsWith('\\'));
    }

    [Fact]
    public void TaskOverviewMigration_DoesNotMentionTheEventTableOrDestructiveStatements()
    {
        var sql = string.Join(
            '\n',
            File.ReadAllLines(PostgresFixture.MigrationPath(PostgresFixture.TaskOverviewMigration))
                .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.DoesNotContain("eventforge.events", sql);
        foreach (var forbidden in new[] { "DROP TABLE eventforge", "TRUNCATE TABLE", "DELETE FROM", "UPDATE eventforge", "ALTER TABLE", "INSERT INTO" })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
