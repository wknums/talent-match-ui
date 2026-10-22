using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

// Stack B owns its Azure SQL schema through EF migrations. Stack A continues to use
// server/storage/schema.sql and is intentionally not migrated to the v3 model until B-GATE T100
// unlocks T101-T122. These assertions protect that sequencing without treating the pre-gate
// Stack A schema as the Stack B provider schema.
public sealed class BulkSchemaFileTests
{
    private static readonly string[] StackBTables =
    [
        "BulkSourceAdmissionEvents", "BulkSourceConnections", "BulkImportSettings",
        "BulkScoringRequests", "BulkScoringItems", "BulkScoringItemContents",
        "BulkRequestAuthorizations",
    ];

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static string SchemaFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "server", "storage", "schema.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("server/storage/schema.sql was not found above " + AppContext.BaseDirectory);
    }

    private static string UpgradeFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "server", "storage", "schema-bulk-v3-upgrades.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("server/storage/schema-bulk-v3-upgrades.sql was not found above " + AppContext.BaseDirectory);
    }

    // The same split the host uses when it applies the file.
    private static List<string> HostBatches() =>
        Regex.Split(File.ReadAllText(SchemaFile()), @"\r?\n(?=IF NOT EXISTS|CREATE (?:UNIQUE )?INDEX)")
            .Select(batch => string.Join("\n", batch.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).Where(l => !l.Trim().StartsWith("--"))).Trim())
            .Where(batch => batch.Length > 0)
            .ToList();

    private static List<string> EfBatches()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=True;TrustServerCertificate=True").Options);
        return Regex.Split(db.Database.GenerateCreateScript(), @"(?m)^GO\s*$").Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
    }

    [Fact]
    public void Stack_B_Azure_SQL_create_script_contains_all_seven_v3_tables_and_required_indexes()
    {
        var ef = EfBatches();

        foreach (var table in StackBTables)
            ef.Should().ContainSingle(b => b.StartsWith($"CREATE TABLE [talentmatch].[{table}] ("));

        var indexes = ef.Where(b => Regex.IsMatch(
            b,
            @"^CREATE (UNIQUE )?INDEX \[[^\]]+\] ON \[talentmatch\]\.\[("
            + string.Join("|", StackBTables)
            + @")\]")).ToList();
        indexes.Should().Contain(b => b.Contains("IX_BulkScoringRequests_ConnectionId"));
        indexes.Should().Contain(b => b.Contains("UX_BulkScoringItems_Request_Fingerprint")
            && b.Contains("WHERE [FingerprintClaimed] = 1"));
        ef.Should().Contain(b => b.StartsWith(
            "CREATE INDEX [IX_BulkAuditEvents_SubjectId_EventType] ON [talentmatch].[BulkAuditEvents] ([SubjectId], [EventType])"));
    }

    [Fact]
    public void Stack_A_schema_stays_at_the_pre_B_gate_shape_until_T100_unlocks_its_migration()
    {
        var schema = Normalize(File.ReadAllText(SchemaFile()));

        schema.Should().Contain("[AccessModel]");
        schema.Should().Contain("[OwningStack]");
        schema.Should().NotContain("BulkSourceConnections");
        schema.Should().NotContain("BulkSourceAdmissionEvents");
        schema.Should().NotContain("[ConfigurationVersion]");
        schema.Should().NotContain("[ConnectionId]");
    }

    [Fact]
    public void The_bulk_statements_survive_the_hosts_batching_and_touch_no_other_table()
    {
        var bulk = HostBatches().Where(b => b.Contains("Bulk")).ToList();

        bulk.Should().HaveCount(18, "five tables and thirteen indexes");
        bulk.Should().OnlyContain(b => b.StartsWith("IF NOT EXISTS"));
        bulk.Should().OnlyContain(b => !Regex.IsMatch(b, @"(?i)\b(DROP|ALTER|TRUNCATE)\b|(?<!ON )\bDELETE\b"), "the section only adds objects");
    }

    [Fact]
    public void Azure_SQL_upgrade_preserves_history_and_materializes_the_delegated_v3_shape()
    {
        var upgrade = Normalize(File.ReadAllText(UpgradeFile()));

        upgrade.Should().Contain("delegated_v3_old_active_work_requires_drain_or_discard");
        upgrade.Should().Contain("COL_LENGTH('talentmatch.BulkScoringItems', 'ScoringConfigVersionId')");
        upgrade.Should().Contain("ADD [ScoringConfigVersionId] nvarchar(36) NULL");
        upgrade.Should().Contain("CREATE TABLE [talentmatch].[BulkSourceConnections]");
        upgrade.Should().Contain("CREATE TABLE [talentmatch].[BulkSourceAdmissionEvents]");
        upgrade.Should().Contain("CREATE TABLE [talentmatch].[BulkAuditEvents]");
        upgrade.Should().Contain("CREATE INDEX [IX_BulkAuditEvents_SubjectId_EventType]");
        upgrade.Should().Contain("SourceKind IN ('sharepoint', 'onedrive_business')");
        upgrade.Should().Contain("Kind IN ('original', 'converted', 'transient')");
        upgrade.Should().NotContain("DROP TABLE [talentmatch].[BulkScoringRequests]");
        upgrade.Should().NotContain("DROP TABLE [talentmatch].[BulkScoringItems]");
    }

    [Fact]
    public void Azure_SQL_upgrade_only_backfills_legacy_requests_and_missing_converted_page_counts()
    {
        var upgrade = Normalize(File.ReadAllText(UpgradeFile()));

        upgrade.Should().Contain(
            "IF COL_LENGTH('talentmatch.BulkScoringRequests', 'AccessModel') IS NOT NULL BEGIN UPDATE");
        upgrade.Should().Contain(
            "WHERE [ScoredVersion] = N'converted' AND [ConvertedPageCount] IS NULL AND [OriginalPageCount] IS NOT NULL");
        upgrade.Should().NotContain(
            "SET [ConvertedPageCount] = CASE WHEN [ScoredVersion]");
    }

    [Theory]
    [InlineData("CK_BulkScoringRequests_SourceKind", "onedrive_business")]
    [InlineData("CK_BulkScoringItems_Reason", "converted_magic_invalid")]
    [InlineData("CK_BulkScoringItemContents_Kind", "transient")]
    [InlineData("CK_BulkRequestAuthorizations_Status", "unavailableHistorical")]
    public void Azure_SQL_upgrade_keeps_already_current_constraints(string constraint, string marker)
    {
        var upgrade = Normalize(File.ReadAllText(UpgradeFile()));

        upgrade.Should().Contain(
            $"FROM sys.check_constraints WHERE object_id = OBJECT_ID(N'[talentmatch].[{constraint}]') AND definition NOT LIKE N'%{marker}%'");
        upgrade.Should().Contain(
            $"OBJECT_ID(N'[talentmatch].[{constraint}]', N'C') IS NULL");
    }
}
