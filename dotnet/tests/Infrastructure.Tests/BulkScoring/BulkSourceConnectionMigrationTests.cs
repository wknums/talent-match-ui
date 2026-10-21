using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkSourceConnectionMigrationTests
{
    [Fact]
    public void Model_contains_all_seven_feature_tables_and_detached_audit_ledger()
    {
        string[] entities =
        [
            "BulkSourceAdmissionEvent",
            "BulkSourceConnection",
            "BulkScoringRequest",
            "BulkScoringItem",
            "BulkScoringItemContent",
            "BulkImportSettings",
            "BulkRequestAuthorization",
        ];
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkSourceModelOnly;Trusted_Connection=True")
                .Options);

        var sqliteNames = sqlite.Model.GetEntityTypes().Select(type => type.ClrType.Name);
        var sqlServerNames = sqlServer.Model.GetEntityTypes().Select(type => type.ClrType.Name);
        entities.Should().AllSatisfy(name =>
        {
            sqliteNames.Should().Contain(name);
            sqlServerNames.Should().Contain(name);
        });

        var audit = sqlite.Model.FindEntityType(typeof(ProcessingEvent));
        audit.Should().NotBeNull();
        audit!.GetForeignKeys().Should().NotContain(foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(BulkScoringRequest));
    }

    [Fact]
    public void V3_migration_exists_and_names_old_active_work_refusal()
    {
        var migration = typeof(AppDbContext).Assembly.GetType(
            "TalentMatch.Infrastructure.Persistence.Migrations.DelegatedBulkSourceV3");
        migration.Should().NotBeNull();
        migration!.GetMethods(System.Reflection.BindingFlags.Instance |
                              System.Reflection.BindingFlags.NonPublic |
                              System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .Should().Contain("Up");
    }

    [Fact]
    public void V3_migration_generates_for_both_providers_with_refusal_and_delegated_tables()
    {
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkSourceScriptOnly;Trusted_Connection=True")
                .Options);

        var sqliteScript = sqlite.GetService<IMigrator>().GenerateScript(
            "20261002160524_AddBulkScoring",
            "20261013224928_DelegatedBulkSourceV3");
        var sqlServerScript = sqlServer.GetService<IMigrator>().GenerateScript(
            "20261002160524_AddBulkScoring",
            "20261013224928_DelegatedBulkSourceV3");

        foreach (var script in new[] { sqliteScript, sqlServerScript })
        {
            script.Should().Contain("BulkSourceAdmissionEvents");
            script.Should().Contain("BulkSourceConnections");
            script.Should().Contain("SourceAccountBindingHash");
        }
        sqlServerScript.Should().Contain("DROP COLUMN [AccessModel]");
        sqlServerScript.Should().Contain("delegated_v3_old_active_work_requires_drain_or_discard");
        sqliteScript.Should().Contain("__DelegatedV3ActiveWorkGuard");
    }
}
