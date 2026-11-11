using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkScoringMigrationTests
{
    private const string PreviousMigration = "20260922231530_AddOptionalParallelUploads";
    private static readonly string[] BulkTables =
    [
        "BulkSourceAdmissionEvents", "BulkSourceConnections", "BulkScoringRequests",
        "BulkScoringItems", "BulkScoringItemContents", "BulkImportSettings",
        "BulkRequestAuthorizations",
    ];

    [Fact]
    public async Task Model_and_migrations_are_in_sync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Migrations_create_the_seven_feature_tables_on_top_of_the_preserved_history()
    {
        await using var fixture = await Fixture.CreateAsync();

        (await TableNames(fixture.Connection)).Should().Contain(BulkTables);
        (await TableNames(fixture.Connection)).Should().Contain(["UploadSessions", "Jobs", "Applications"]);
        (await fixture.Context.Database.GetAppliedMigrationsAsync()).Should().Contain(PreviousMigration);
        (await fixture.Context.Database.GetAppliedMigrationsAsync()).Should()
            .Contain(migration => migration.EndsWith("_DelegatedBulkSourceV3"));
    }

    [Fact]
    public async Task Check_constraints_reject_inconsistent_requests()
    {
        await using var fixture = await Fixture.CreateAsync();

        // outcome must be set exactly when completed
        await ExecuteAsync(fixture, "UPDATE BulkScoringRequests SET Outcome = 'partial' WHERE Id = 'r1'");
        await ExecuteAsync(fixture, "UPDATE BulkScoringRequests SET MaxTotalBytes = 0 WHERE Id = 'r1'");
        await ExecuteAsync(fixture, "UPDATE BulkScoringRequests SET IncludedItemCount = 100001 WHERE Id = 'r1'");
        await ExecuteAsync(fixture, "UPDATE BulkScoringRequests SET Status = 'banana' WHERE Id = 'r1'");
        await ExecuteAsync(fixture, "UPDATE BulkScoringRequests SET WaitingCount = -1 WHERE Id = 'r1'");
    }

    [Fact]
    public async Task Fingerprint_is_unique_within_a_request_only_while_claimed()
    {
        await using var fixture = await Fixture.CreateAsync();

        await InsertItem(fixture, "i1", "d1", fingerprint: "abc", claimed: 1);
        await Insert(fixture, "i2", "d2", fingerprint: "abc", claimed: 1).Should().ThrowAsync<SqliteException>();
        await InsertItem(fixture, "i3", "d3", fingerprint: "abc", claimed: 0);
        await Insert(fixture, "i4", "d1", fingerprint: null, claimed: 0).Should().ThrowAsync<SqliteException>("(RequestId, SourceDriveItemId) is unique");
    }

    [Fact]
    public async Task Content_has_exactly_one_canonical_location()
    {
        await using var fixture = await Fixture.CreateAsync();
        await InsertItem(fixture, "i1", "d1", null, 0);

        await ExecuteSql(fixture, "INSERT INTO BulkScoringItemContents (ItemId, Kind, ContentBase64, SizeBytes, Sha256, CreatedAt) VALUES ('i1', 'original', 'AAAA', 3, 'x', '2026-10-02')");
        await FailSql(fixture, "INSERT INTO BulkScoringItemContents (ItemId, Kind, SizeBytes, Sha256, CreatedAt) VALUES ('i1', 'converted', 3, 'x', '2026-10-02')");
        await FailSql(fixture, "INSERT INTO BulkScoringItemContents (ItemId, Kind, BlobUri, ContentBase64, SizeBytes, Sha256, CreatedAt) VALUES ('i1', 'converted', 'u', 'AAAA', 3, 'x', '2026-10-02')");
    }

    [Fact]
    public async Task Inactive_authorizations_cannot_retain_protected_material()
    {
        await using var fixture = await Fixture.CreateAsync();
        await ExecuteSql(fixture, BoundAuthorizationInsert());
        await FailSql(fixture, "UPDATE BulkRequestAuthorizations SET Status = 'lapsed' WHERE RequestId = 'r1'");
        await ExecuteSql(fixture, "UPDATE BulkRequestAuthorizations SET Status = 'lapsed', Payload = NULL, WrappedDataKey = NULL WHERE RequestId = 'r1'");
    }

    [Fact]
    public async Task Deleting_a_job_cascades_to_requests_items_content_and_authorization()
    {
        await using var fixture = await Fixture.CreateAsync();
        await InsertItem(fixture, "i1", "d1", null, 0);
        await ExecuteSql(fixture, "INSERT INTO BulkScoringItemContents (ItemId, Kind, ContentBase64, SizeBytes, Sha256, CreatedAt) VALUES ('i1', 'original', 'AAAA', 3, 'x', '2026-10-02')");
        await ExecuteSql(fixture, BoundAuthorizationInsert());

        await ExecuteSql(fixture, "DELETE FROM Jobs WHERE Id = 'job-1'");

        foreach (var table in new[] { "BulkScoringRequests", "BulkScoringItems", "BulkScoringItemContents", "BulkRequestAuthorizations" })
            (await Count(fixture, table)).Should().Be(0, table);
    }

    [Fact]
    public async Task SqlServer_current_model_script_uses_the_talentmatch_schema_and_filtered_indexes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        await using var context = new AppDbContext(options);
        var script = context.Database.GenerateCreateScript();

        foreach (var table in BulkTables) script.Should().Contain($"[talentmatch].[{table}]");
        script.Should().Contain("UX_BulkScoringItems_Request_Fingerprint").And.Contain("[FingerprintClaimed] = 1");
        script.Should().Contain("CK_BulkRequestAuthorizations_NoMaterialWhenInactive");
        script.Should().Contain("varbinary(max)").And.Contain("nvarchar(max)").And.Contain("datetime2");
        script.Should().NotContain("INTEGER").And.NotContain("[Payload] BLOB");
    }

    private static string BoundAuthorizationInsert() =>
        "INSERT INTO BulkRequestAuthorizations "
        + "(RequestId, ProductActorId, SourceTenantId, SourceAccountBindingHash, SourceDriveId, SourceFolderItemId, Payload, WrappedDataKey, KeyVersion, Status, ObtainedAt) "
        + "VALUES ('r1', 'actor', 'tenant', 'account-binding', 'drv', 'fld', x'0102', x'03', 'v1', 'active', '2026-10-02')";

    private static async Task ExecuteAsync(Fixture fixture, string sql)
    {
        // Each statement is expected to violate a constraint; the row is left untouched.
        await FailSql(fixture, sql);
        (await Count(fixture, "BulkScoringRequests")).Should().Be(1);
    }

    private static Func<Task> Insert(Fixture fixture, string id, string driveItemId, string? fingerprint, int claimed) =>
        () => InsertItem(fixture, id, driveItemId, fingerprint, claimed);

    private static Task InsertItem(Fixture fixture, string id, string driveItemId, string? fingerprint, int claimed) =>
        ExecuteSql(fixture,
            "INSERT INTO BulkScoringItems (Id, RequestId, Ordinal, SourceDriveId, SourceDriveItemId, SourceETag, FileName, SourcePath, MimeType, SizeBytes, ExcludedWhenRasterized, Status, Fingerprint, FingerprintClaimed, Attempt, CreatedAt, UpdatedAt) "
            + $"VALUES ('{id}', 'r1', 0, 'drv', '{driveItemId}', 'e', 'f.pdf', '/', 'application/pdf', 10, 0, 'waiting', {(fingerprint is null ? "NULL" : $"'{fingerprint}'")}, {claimed}, 0, '2026-10-02', '2026-10-02')");

    private static async Task ExecuteSql(Fixture fixture, string sql)
    {
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task FailSql(Fixture fixture, string sql)
    {
        var act = () => ExecuteSql(fixture, sql);
        await act.Should().ThrowAsync<SqliteException>(sql);
    }

    private static async Task<long> Count(Fixture fixture, string table)
    {
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> TableNames(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; }
        public AppDbContext Context { get; }

        private Fixture(SqliteConnection connection, AppDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.MigrateAsync();
            context.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await context.SaveChangesAsync();
            var fixture = new Fixture(connection, context);
            var request = new BulkScoringRequest
            {
                Id = "r1",
                JobId = "job-1",
                OwnerActorId = "actor",
                SourceKind = BulkSourceKind.SharePoint,
                SourceTenantId = "tenant",
                SourceAccountBindingHash = "account-binding",
                SourceDriveId = "drv",
                SourceFolderItemId = "fld",
                SourceFolderName = "Candidates",
                SourceFolderPath = "/Candidates",
                SourceWebUrl = "https://x",
                RasterDpi = 150,
                MaxFilesPerRequest = 30_000,
                MaxBytesPerFile = 8_388_608,
                MaxTotalBytes = 53_687_091_200,
                DiscoveryStepMaxFiles = 1_000,
                DiscoveryStepMaxEntries = 10_000,
                DiscoveryStepMaxSeconds = 45,
                DiscoveryStatus = BulkDiscoveryStatus.Complete,
                DiscoveredCount = 1,
                IncludedItemCount = 1,
                IncludedBytes = 10,
                TotalEligibleBytes = 10,
                CreatedAt = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
                CorrelationId = "c",
            };
            request.Confirm(false, false, "config-1", request.CreatedAt);
            context.BulkScoringRequests.Add(request);
            await context.SaveChangesAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
