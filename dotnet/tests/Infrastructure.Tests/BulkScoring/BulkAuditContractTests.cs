using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkAuditContractTests
{
    private static readonly AuditRow[] P1Rows =
    [
        User(BulkAuditEventTypes.SourceConnectionCreated),
        User(BulkAuditEventTypes.SourceConnectionCompleted),
        UserOrService(BulkAuditEventTypes.SourceConnectionFailed),
        Service(BulkAuditEventTypes.SourceConnectionExpired),
        User(BulkAuditEventTypes.SourceConnectionConsumed),
        User(BulkAuditEventTypes.PreviewDraftCreated),
        UserOrService(BulkAuditEventTypes.PreviewDiscoveryCommitted),
        UserOrService(BulkAuditEventTypes.PreviewDraftAbandoned),
        User(BulkAuditEventTypes.BulkRequestConfirmed),
        User(BulkAuditEventTypes.BulkRequestCancellationRequested),
        User(BulkAuditEventTypes.BulkRequestRetryCreated),
        UserOrService(BulkAuditEventTypes.BulkItemDiscovered),
        Service(BulkAuditEventTypes.BulkItemClaimed),
        Service(BulkAuditEventTypes.BulkItemLeaseRenewed),
        Service(BulkAuditEventTypes.BulkItemLeaseRecovered),
        Service(BulkAuditEventTypes.BulkItemLeaseReleased),
        Service(BulkAuditEventTypes.BulkItemImportStarted),
        Service(BulkAuditEventTypes.BulkItemSourceMetadataRecheckSucceeded),
        Service(BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed),
        Service(BulkAuditEventTypes.BulkOriginalContentStaged),
        Service(BulkAuditEventTypes.BulkConvertedContentStaged),
        Service(BulkAuditEventTypes.BulkItemFingerprintRecorded),
        Service(BulkAuditEventTypes.BulkItemDuplicateClaimed),
        Service(BulkAuditEventTypes.BulkItemDuplicateSkipped),
        Service(BulkAuditEventTypes.BulkItemConversionStarted),
        Service(BulkAuditEventTypes.BulkItemConversionCompleted),
        Service(BulkAuditEventTypes.BulkItemConversionFailed),
        Service(BulkAuditEventTypes.BulkConvertedOutputQuarantined, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkConvertedOutputRemoved, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkItemApplicationCreated),
        Service(BulkAuditEventTypes.BulkItemDocumentCreated),
        Service(BulkAuditEventTypes.BulkItemScoringHandoffQueued, BulkAuditBoundary.DurableOutbox),
        Service(BulkAuditEventTypes.BulkItemScoringStarted),
        Service(BulkAuditEventTypes.BulkItemCompleted),
        Service(BulkAuditEventTypes.BulkItemFailed),
        Service(BulkAuditEventTypes.BulkItemSkipped),
        Service(BulkAuditEventTypes.BulkItemCancelled),
        UserOrService(BulkAuditEventTypes.BulkRetryItemCreated),
        Service(BulkAuditEventTypes.BulkRequestCountsReconciled),
        Service(BulkAuditEventTypes.BulkRequestTerminalFinalized),
        Service(BulkAuditEventTypes.BulkNotificationCreated),
        User(BulkAuditEventTypes.BulkNotificationAcknowledged),
        User(BulkAuditEventTypes.RequestAuthorizationEstablished),
        Service(BulkAuditEventTypes.RequestAuthorizationUsed),
        Service(BulkAuditEventTypes.RequestAuthorizationLapsed),
        Service(BulkAuditEventTypes.RequestAuthorizationDiscarded),
        Service(BulkAuditEventTypes.BulkOriginalContentDeleted, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkConvertedContentDeleted, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkTransientContentDeleted, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkItemReconciliationCorrected, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkRequestReconciliationCorrected, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkContentCleanupReconciliationPending, BulkAuditBoundary.Reconciliation),
        Service(BulkAuditEventTypes.BulkContentCleanupReconciliationCompleted, BulkAuditBoundary.Reconciliation),
        User(BulkAuditEventTypes.BulkSettingsChanged),
        UserOrService(BulkAuditEventTypes.BulkJobCleanupCompleted, BulkAuditBoundary.Reconciliation),
        UserOrService(BulkAuditEventTypes.BulkJobCleanupPending, BulkAuditBoundary.Reconciliation),
    ];

    public static TheoryData<string, BulkAuditActorType, BulkAuditBoundary> MatrixRows
    {
        get
        {
            var rows = new TheoryData<string, BulkAuditActorType, BulkAuditBoundary>();
            foreach (var row in P1Rows)
                rows.Add(row.EventType, row.RequiredActor, row.Boundary);
            return rows;
        }
    }

    [Fact]
    public void OpenApi_domain_and_test_matrix_enumerate_the_same_complete_P1_registry()
    {
        var openApiRows = ReadOpenApiAuditRows();
        var testRows = P1Rows.Select(row => row.EventType).ToArray();

        testRows.Should().HaveCount(56).And.OnlyHaveUniqueItems();
        testRows.Should().BeEquivalentTo(BulkAuditEventTypes.All);
        openApiRows.Should().BeEquivalentTo(testRows);
    }

    [Theory]
    [MemberData(nameof(MatrixRows))]
    public void Every_P1_row_has_correlation_actor_optional_initiator_UTC_versioned_facts_and_atomic_boundary(
        string eventType,
        BulkAuditActorType actorType,
        BulkAuditBoundary boundary)
    {
        var audit = NewAudit(eventType, actorType, boundary);

        audit.Validate();

        Guid.TryParse(audit.CorrelationId, out _).Should().BeTrue();
        audit.ActorId.Should().NotBeNullOrWhiteSpace();
        audit.InitiatedByActorId.Should().Be(
            actorType == BulkAuditActorType.Service ? "product-user-1" : null);
        audit.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        audit.SchemaVersion.Should().Be(1);
        audit.SafeFactsJson.Should().Be("""{"factVersion":1,"state":"committed","configurationVersion":7}""");
        audit.Boundary.Should().Be(boundary);
    }

    [Fact]
    public void SQLite_and_SQL_Server_models_and_migrations_expose_the_same_detached_envelope()
    {
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkAuditModelOnly;Trusted_Connection=True")
                .Options);

        var sqliteAudit = sqlite.Model.FindEntityType(typeof(BulkAuditEvent));
        var sqlServerAudit = sqlServer.Model.FindEntityType(typeof(BulkAuditEvent));
        sqliteAudit.Should().NotBeNull("Stack B requires its typed immutable ledger on SQLite");
        sqlServerAudit.Should().NotBeNull("Stack B requires the identical ledger on SQL Server");

        var expectedProperties = new[]
        {
            nameof(BulkAuditEvent.AuditEventId),
            nameof(BulkAuditEvent.EventType),
            nameof(BulkAuditEvent.CorrelationId),
            nameof(BulkAuditEvent.ActorType),
            nameof(BulkAuditEvent.ActorId),
            nameof(BulkAuditEvent.InitiatedByActorId),
            nameof(BulkAuditEvent.OccurredAt),
            nameof(BulkAuditEvent.SubjectType),
            nameof(BulkAuditEvent.SubjectId),
            nameof(BulkAuditEvent.SafeFactsJson),
            nameof(BulkAuditEvent.SchemaVersion),
            nameof(BulkAuditEvent.Boundary),
        };
        sqliteAudit!.GetProperties().Select(property => property.Name).Should()
            .BeEquivalentTo(expectedProperties);
        sqlServerAudit!.GetProperties().Select(property => property.Name).Should()
            .BeEquivalentTo(expectedProperties);
        sqliteAudit.GetForeignKeys().Should().BeEmpty("audit rows must survive all cascades");
        sqlServerAudit.GetForeignKeys().Should().BeEmpty("audit rows must survive all cascades");

        var sqliteScript = sqlite.GetService<IMigrator>().GenerateScript();
        var sqlServerScript = sqlServer.GetService<IMigrator>().GenerateScript();
        sqliteScript.Should().Contain("BulkAuditEvents");
        sqlServerScript.Should().Contain("BulkAuditEvents");
    }

    [Fact]
    public async Task Relational_state_and_audit_insert_roll_back_together_when_the_audit_write_fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        const string duplicateAuditId = "10000000-0000-4000-8000-000000000001";

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Add(NewAudit(
                BulkAuditEventTypes.BulkSettingsChanged,
                BulkAuditActorType.User,
                auditEventId: duplicateAuditId));
            await seed.SaveChangesAsync();
        }

        await using (var mutation = new AppDbContext(options))
        {
            mutation.BulkImportSettings.Add(new BulkImportSettings
            {
                UpdatedByActorId = "admin-user-1",
                UpdatedAt = DateTime.UtcNow,
                MaxFilesPerRequest = 42_000,
            });
            mutation.Add(NewAudit(
                BulkAuditEventTypes.BulkSettingsChanged,
                BulkAuditActorType.User,
                auditEventId: duplicateAuditId));

            await FluentActions.Awaiting(() => mutation.SaveChangesAsync())
                .Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = new AppDbContext(options);
        (await verification.BulkImportSettings.AnyAsync()).Should().BeFalse(
            "a failed audit insert must roll back the associated relational mutation");
        (await verification.Set<BulkAuditEvent>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Audit_rows_reject_update_and_delete_and_survive_job_cleanup()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        const string auditId = "20000000-0000-4000-8000-000000000002";

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Jobs.Add(new Job { Id = "job-audit", Title = "Audited job" });
            seed.Add(NewAudit(
                BulkAuditEventTypes.BulkJobCleanupPending,
                BulkAuditActorType.Service,
                BulkAuditBoundary.Reconciliation,
                auditEventId: auditId,
                subjectId: "job-audit"));
            await seed.SaveChangesAsync();
        }

        await using (var update = new AppDbContext(options))
        {
            var audit = await update.Set<BulkAuditEvent>().SingleAsync();
            update.Entry(audit).Property(nameof(BulkAuditEvent.SafeFactsJson)).CurrentValue =
                """{"factVersion":1,"state":"tampered"}""";
            await FluentActions.Awaiting(() => update.SaveChangesAsync())
                .Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var delete = new AppDbContext(options))
        {
            delete.Remove(await delete.Set<BulkAuditEvent>().SingleAsync());
            await FluentActions.Awaiting(() => delete.SaveChangesAsync())
                .Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var cleanup = new AppDbContext(options))
        {
            cleanup.Remove(await cleanup.Jobs.SingleAsync(job => job.Id == "job-audit"));
            await cleanup.SaveChangesAsync();
        }

        await using var verification = new AppDbContext(options);
        (await verification.Set<BulkAuditEvent>().SingleAsync()).AuditEventId.Should().Be(auditId);
    }

    [Fact]
    public void Initiating_user_never_replaces_the_service_actor()
    {
        var missingServiceActor = NewAudit(
            BulkAuditEventTypes.BulkItemClaimed,
            BulkAuditActorType.Service,
            actorId: "",
            initiatedByActorId: "product-user-1");
        var userWithInitiator = NewAudit(
            BulkAuditEventTypes.BulkRequestConfirmed,
            BulkAuditActorType.User,
            initiatedByActorId: "another-user");

        FluentActions.Invoking(() => missingServiceActor.Validate())
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => userWithInitiator.Validate())
            .Should().Throw<InvalidOperationException>();
    }

    private static AuditRow User(
        string eventType,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        new(eventType, BulkAuditActorType.User, boundary);

    private static AuditRow Service(
        string eventType,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        new(eventType, BulkAuditActorType.Service, boundary);

    private static AuditRow UserOrService(
        string eventType,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional) =>
        new(eventType, BulkAuditActorType.Service, boundary);

    private static BulkAuditEvent NewAudit(
        string eventType,
        BulkAuditActorType actorType,
        BulkAuditBoundary boundary = BulkAuditBoundary.Transactional,
        string? auditEventId = null,
        string? subjectId = null,
        string? actorId = null,
        string? initiatedByActorId = null) => new()
    {
        AuditEventId = auditEventId ?? Guid.NewGuid().ToString(),
        EventType = eventType,
        CorrelationId = Guid.NewGuid().ToString(),
        ActorType = actorType,
        ActorId = actorId ?? (actorType == BulkAuditActorType.User ? "product-user-1" : "bulk-worker-v1"),
        InitiatedByActorId = initiatedByActorId
            ?? (actorType == BulkAuditActorType.Service ? "product-user-1" : null),
        OccurredAt = DateTime.UtcNow,
        SubjectType = "bulk_item",
        SubjectId = subjectId ?? Guid.NewGuid().ToString(),
        SafeFactsJson = """{"factVersion":1,"state":"committed","configurationVersion":7}""",
        SchemaVersion = 1,
        Boundary = boundary,
    };

    private static IReadOnlySet<string> ReadOpenApiAuditRows()
    {
        var contract = ReadContract();
        var start = contract.IndexOf("x-immutable-audit-contract:", StringComparison.Ordinal);
        var end = contract.IndexOf("servers:", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var audit = contract[start..end];
        var rows = Regex.Matches(audit, @"(?m)^\s+eventType:\s+([a-z0-9_]+)\s*$")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(audit, @"(?m)^\s+eventTypes:\s+\[([^\]]+)\]\s*$"))
            foreach (var eventType in match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                rows.Add(eventType);
        return rows;
    }

    private static string ReadContract()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "specs",
                "001-sharepoint-bulk-scoring",
                "contracts",
                "openapi.yaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            "Bulk-scoring OpenAPI contract was not found above " + AppContext.BaseDirectory);
    }

    private sealed record AuditRow(
        string EventType,
        BulkAuditActorType RequiredActor,
        BulkAuditBoundary Boundary);
}
