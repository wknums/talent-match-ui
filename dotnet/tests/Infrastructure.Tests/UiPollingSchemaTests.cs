using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests;

public sealed class UiPollingSchemaTests
{
    private const string PreviousMigration = "20261014204309_SyncBulkV3Model";
    private const string PollingMigration = "20261019173434_AddUiPollingIntervals";
    private const string LegacyTable = """
        CREATE TABLE UploadSettings (
            Id TEXT NOT NULL PRIMARY KEY,
            FileConcurrency INTEGER NOT NULL,
            MaxIndividualFileBytes INTEGER NOT NULL,
            MaxInFlightBytes INTEGER NOT NULL,
            ConcurrencyVersion INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            CreatedBy TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            UpdatedBy TEXT NOT NULL
        );
        INSERT INTO UploadSettings VALUES ('optional-file-upload', 7, 5000000, 100000000, 3,
            '2026-10-19', 'admin', '2026-10-19', 'admin');
        """;

    [Fact]
    public void ExistingSqliteStartupAddsDefaultsOnceAndPreservesLimitsAndSavedIntervals()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, LegacyTable);
        UpgradeStartup(connection);

        AssertSettings(connection, 60, 60);
        Execute(connection, "UPDATE UploadSettings SET ThroughputRefreshIntervalSeconds = 90, StatusPollingIntervalSeconds = 120;");
        UpgradeStartup(connection);
        AssertSettings(connection, 90, 120);
    }

    [Fact]
    public void ExistingSqliteStartupCanCreateMissingSettingsTable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        UpgradeStartup(connection);
        Execute(connection, """
            INSERT INTO UploadSettings (Id, FileConcurrency, MaxIndividualFileBytes, MaxInFlightBytes,
                ConcurrencyVersion, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
            VALUES ('optional-file-upload', 7, 5000000, 100000000, 3, '2026-10-19', 'admin', '2026-10-19', 'admin');
            """);

        AssertSettings(connection, 60, 60);
    }

    [Fact]
    public void SqliteMigrationUpgradesAnExistingRowWithoutResettingItsLimits()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, LegacyTable);
        Execute(connection, """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """);
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var script = context.GetService<IMigrator>().GenerateScript(PreviousMigration, PollingMigration);

        Execute(connection, script);

        AssertSettings(connection, 60, 60);
        var invalid = () => Execute(connection, "UPDATE UploadSettings SET StatusPollingIntervalSeconds = 0;");
        invalid.Should().Throw<SqliteException>();
    }

    [Fact]
    public void SqlServerMigrationUsesTheSharedSchemaAndIntColumns()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True").Options);

        var script = context.GetService<IMigrator>().GenerateScript(PreviousMigration, PollingMigration);

        script.Should().Contain("ALTER TABLE [talentmatch].[UploadSettings] ADD [StatusPollingIntervalSeconds] int NOT NULL DEFAULT 60")
            .And.Contain("ALTER TABLE [talentmatch].[UploadSettings] ADD [ThroughputRefreshIntervalSeconds] int NOT NULL DEFAULT 60")
            .And.Contain("CK_UploadSettings_StatusPolling")
            .And.Contain("CK_UploadSettings_ThroughputRefresh");
    }

    [Fact]
    public void SharedSqliteSchemaDefaultsBothIntervalsAndRejectsInvalidValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "server", "storage", "schema-sqlite.sql")))
            root = root.Parent;
        root.Should().NotBeNull();
        Execute(connection, File.ReadAllText(Path.Combine(root!.FullName, "server", "storage", "schema-sqlite.sql")));
        Execute(connection, """
            INSERT INTO UploadSettings (Id, FileConcurrency, MaxIndividualFileBytes, MaxInFlightBytes,
                ConcurrencyVersion, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
            VALUES ('optional-file-upload', 7, 5000000, 100000000, 3, '2026-10-19', 'admin', '2026-10-19', 'admin');
            """);

        AssertSettings(connection, 60, 60);
        var invalid = () => Execute(connection, "UPDATE UploadSettings SET ThroughputRefreshIntervalSeconds = -1;");
        invalid.Should().Throw<SqliteException>();
    }

    private static void AssertSettings(SqliteConnection connection, int throughput, int status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FileConcurrency, MaxIndividualFileBytes, MaxInFlightBytes, ConcurrencyVersion, ThroughputRefreshIntervalSeconds, StatusPollingIntervalSeconds FROM UploadSettings;";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt32(0).Should().Be(7);
        reader.GetInt64(1).Should().Be(5_000_000);
        reader.GetInt64(2).Should().Be(100_000_000);
        reader.GetInt32(3).Should().Be(3);
        reader.GetInt32(4).Should().Be(throughput);
        reader.GetInt32(5).Should().Be(status);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void UpgradeStartup(SqliteConnection connection) =>
        typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name.Contains("g__EnsureSqlitePollingSettingsColumns|", StringComparison.Ordinal))
            .Invoke(null, [connection]);
}
