using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkJobDeletionTests
{
    [Fact]
    public async Task Job_lifetime_retains_draft_active_completed_failed_cancelled_and_retry_history_until_job_deletion()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var draft = await fixture.Repo.CreateDraftAsync(NewRequest(fixture), Step(Items(1, "draft")));
        var active = await ActiveAsync(fixture, "active");
        var completed = await ActiveAsync(fixture, "completed");
        var failed = await ActiveAsync(fixture, "failed");
        var cancelled = await ActiveAsync(fixture, "cancelled");

        await TerminalizeAsync(fixture, completed, BulkItemStatus.Completed);
        await TerminalizeAsync(fixture, failed, BulkItemStatus.Failed, BulkReasonCode.ScoringFailed);
        await TerminalizeAsync(fixture, cancelled, BulkItemStatus.Cancelled, BulkReasonCode.CancelledByUser);
        var failedItem = await fixture.Db.BulkScoringItems.AsNoTracking()
            .SingleAsync(value => value.RequestId == failed.Id);
        var retry = BulkScoringRequest.CreateRetry(
            failed,
            "actor",
            "config-retry",
            Guid.NewGuid().ToString(),
            Now.AddMinutes(1));
        await fixture.Repo.CreateRetryRequestAsync(retry, [failedItem.CreateRetry(Now.AddMinutes(1))], null);

        fixture.Db.ChangeTracker.Clear();
        var retained = await fixture.Db.BulkScoringRequests.AsNoTracking()
            .Where(value => value.JobId == "job-1")
            .ToListAsync();

        retained.Select(value => value.Id).Should().BeEquivalentTo(
            [draft.Id, active.Id, completed.Id, failed.Id, cancelled.Id, retry.Id]);
        retained.Single(value => value.Id == completed.Id).Outcome.Should().Be(BulkRequestOutcome.AllSucceeded);
        retained.Single(value => value.Id == failed.Id).Outcome.Should().Be(BulkRequestOutcome.Failed);
        retained.Single(value => value.Id == cancelled.Id).Outcome.Should().Be(BulkRequestOutcome.Cancelled);
        (await fixture.Db.BulkScoringItems.CountAsync()).Should().Be(6);
    }

    [Fact]
    public async Task Authorized_deletion_fences_workers_cascades_every_relational_row_and_deletes_original_converted_and_transient_content()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var request = await ActiveAsync(fixture, "delete", withAuthorization: true);
        var item = await fixture.Db.BulkScoringItems.SingleAsync(value => value.RequestId == request.Id);
        fixture.Db.BulkScoringItemContents.AddRange(
            Inline(item.Id, BulkContentKind.Original, [1]),
            Inline(item.Id, BulkContentKind.Converted, [2]));
        await fixture.Db.SaveChangesAsync();
        var content = new RecordingContentStore();
        content.Stage(request.Id, item.Id, "original");
        content.Stage(request.Id, item.Id, "converted");
        content.Stage(request.Id, item.Id, "transient");

        var handler = Handler(fixture, content);
        (await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None)).Should().BeTrue();

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Jobs.AnyAsync()).Should().BeFalse();
        (await fixture.Db.BulkScoringRequests.AnyAsync()).Should().BeFalse();
        (await fixture.Db.BulkScoringItems.AnyAsync()).Should().BeFalse();
        (await fixture.Db.BulkScoringItemContents.AnyAsync()).Should().BeFalse();
        (await fixture.Db.BulkRequestAuthorizations.AnyAsync()).Should().BeFalse();
        content.Objects.Should().BeEmpty();
        (await fixture.Repo.ClaimDueItemsAsync(
            "late-worker", 10, Now, TimeSpan.FromMinutes(2))).Should().BeEmpty();

        var events = await fixture.Db.BulkAuditEvents.AsNoTracking()
            .Where(value => DeletionEventTypes.Contains(value.EventType))
            .ToListAsync();
        events.Select(value => value.EventType).Should().Contain(
        [
            BulkAuditEventTypes.BulkOriginalContentDeleted,
            BulkAuditEventTypes.BulkConvertedContentDeleted,
            BulkAuditEventTypes.BulkTransientContentDeleted,
            BulkAuditEventTypes.BulkItemReconciliationCorrected,
            BulkAuditEventTypes.BulkRequestReconciliationCorrected,
            BulkAuditEventTypes.BulkContentCleanupReconciliationCompleted,
            BulkAuditEventTypes.BulkJobCleanupCompleted,
        ]);
        AssertDeletionEnvelope(events, "admin");
    }

    [Fact]
    public async Task Partial_content_failure_records_pending_durable_decision_and_reconciliation_finishes_idempotently()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var request = await ActiveAsync(fixture, "partial");
        var itemId = await fixture.Db.BulkScoringItems.Where(value => value.RequestId == request.Id)
            .Select(value => value.Id).SingleAsync();
        var content = new RecordingContentStore { FailuresRemaining = 1 };
        content.Stage(request.Id, itemId, "original");
        content.Stage(request.Id, itemId, "converted");
        content.Stage(request.Id, itemId, "transient");
        var handler = Handler(fixture, content);

        (await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None))
            .Should().BeFalse("cleanup cannot report success while known staged content remains");
        content.Objects.Should().NotBeEmpty();
        var pending = await fixture.Db.BulkAuditEvents.AsNoTracking()
            .SingleAsync(value => value.EventType == BulkAuditEventTypes.BulkJobCleanupPending);
        pending.ActorType.Should().Be(BulkAuditActorType.Service);
        pending.ActorId.Should().Be("bulk-job-cleanup-service");
        pending.InitiatedByActorId.Should().Be("admin");
        pending.Boundary.Should().Be(BulkAuditBoundary.Reconciliation);

        var reconcilerType = typeof(BulkScoringRepository).Assembly.GetType(
            "TalentMatch.Infrastructure.Persistence.Repositories.BulkJobCleanupReconciler");
        reconcilerType.Should().NotBeNull(
            "T096 must provide a durable service-actor reconciliation path for orphaned staged content");
    }

    [Fact]
    public async Task Repeated_deletion_is_idempotent_and_preserves_one_detached_safe_cleanup_history()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var request = await ActiveAsync(fixture, "repeat");
        var itemId = await fixture.Db.BulkScoringItems.Where(value => value.RequestId == request.Id)
            .Select(value => value.Id).SingleAsync();
        var content = new RecordingContentStore();
        content.Stage(request.Id, itemId, "original");
        var handler = Handler(fixture, content);

        (await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None)).Should().BeTrue();
        await handler.Handle(new DeleteJobCommand("job-1"), CancellationToken.None);

        var audits = await fixture.Db.BulkAuditEvents.AsNoTracking()
            .Where(value => value.EventType == BulkAuditEventTypes.BulkJobCleanupCompleted)
            .ToListAsync();
        audits.Should().ContainSingle();
        AssertDeletionEnvelope(audits, "admin");
        var serialized = JsonSerializer.Serialize(audits);
        serialized.Should().NotContainAny(ProhibitedPayloads);
        (await fixture.Db.BulkScoringRequests.AnyAsync()).Should().BeFalse();
        content.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_decision_and_detached_audit_are_atomic_append_only_and_survive_job_and_content_cleanup()
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var request = await ActiveAsync(fixture, "audit");
        var content = new RecordingContentStore();
        var itemId = await fixture.Db.BulkScoringItems.Where(value => value.RequestId == request.Id)
            .Select(value => value.Id).SingleAsync();
        content.Stage(request.Id, itemId, "transient");

        await Handler(fixture, content).Handle(new DeleteJobCommand("job-1"), CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        var audit = await fixture.Db.BulkAuditEvents.SingleAsync(
            value => value.EventType == BulkAuditEventTypes.BulkJobCleanupCompleted);
        var originalFacts = audit.SafeFactsJson;

        fixture.Db.Entry(audit).Property(value => value.SafeFactsJson).CurrentValue =
            """{"factVersion":1,"state":"tampered"}""";
        await fixture.Db.Invoking(value => value.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>();
        fixture.Db.ChangeTracker.Clear();
        fixture.Db.Remove(await fixture.Db.BulkAuditEvents.SingleAsync(value => value.AuditEventId == audit.AuditEventId));
        await fixture.Db.Invoking(value => value.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>();
        fixture.Db.ChangeTracker.Clear();

        var survived = await fixture.Db.BulkAuditEvents.AsNoTracking()
            .SingleAsync(value => value.AuditEventId == audit.AuditEventId);
        survived.SafeFactsJson.Should().Be(originalFacts);
        survived.Validate();
        (await fixture.Db.Jobs.AnyAsync()).Should().BeFalse();
        content.Objects.Should().BeEmpty();
    }

    [Fact]
    public void SQLite_and_SQL_Server_have_matching_cascades_detached_audit_and_all_staged_content_kinds()
    {
        using var sqlite = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkJobDeletionModelOnly;Trusted_Connection=True")
                .Options);

        AssertProviderModel(sqlite);
        AssertProviderModel(sqlServer);
        Enum.GetNames<BulkContentKind>().Should().BeEquivalentTo(["Original", "Converted", "Transient"]);

        var sqliteScript = sqlite.GetService<IMigrator>().GenerateScript();
        var sqlServerScript = sqlServer.GetService<IMigrator>().GenerateScript();
        foreach (var table in new[]
                 {
                     "BulkScoringRequests", "BulkScoringItems", "BulkScoringItemContents",
                     "BulkRequestAuthorizations", "BulkAuditEvents",
                 })
        {
            sqliteScript.Should().Contain(table);
            sqlServerScript.Should().Contain(table);
        }
        sqlServerScript.Should().Contain("ON DELETE CASCADE");
    }

    private static readonly string[] ProhibitedPayloads =
    [
        "https://tenant.example",
        "Candidates",
        "candidate.pdf",
        "jane@example.com",
        "access_token",
        "authorization_code",
        "fingerprint-secret",
        "provider payload",
        "contentbase64",
    ];

    private static readonly string[] DeletionEventTypes =
    [
        BulkAuditEventTypes.BulkOriginalContentDeleted,
        BulkAuditEventTypes.BulkConvertedContentDeleted,
        BulkAuditEventTypes.BulkTransientContentDeleted,
        BulkAuditEventTypes.BulkItemReconciliationCorrected,
        BulkAuditEventTypes.BulkRequestReconciliationCorrected,
        BulkAuditEventTypes.BulkContentCleanupReconciliationPending,
        BulkAuditEventTypes.BulkContentCleanupReconciliationCompleted,
        BulkAuditEventTypes.BulkJobCleanupPending,
        BulkAuditEventTypes.BulkJobCleanupCompleted,
    ];

    private static DeleteJobCommandHandler Handler(Fixture fixture, IBulkContentStore content) =>
        new(
            new JobRepository(fixture.Db),
            new AdminUser(),
            null,
            fixture.Repo,
            content,
            NullLogger<DeleteJobCommandHandler>.Instance);

    private static BulkScoringItemContent Inline(string itemId, BulkContentKind kind, byte[] bytes) =>
        new()
        {
            ItemId = itemId,
            Kind = kind,
            ContentBase64 = Convert.ToBase64String(bytes),
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            CreatedAt = Now,
        };

    private static BulkScoringRequest NewRequest(Fixture fixture)
    {
        var request = fixture.NewRequest();
        request.MaxFilesPerRequest = 30_000;
        request.MaxBytesPerFile = 8_388_608;
        request.MaxTotalBytes = 53_687_091_200;
        request.DiscoveryStepMaxFiles = 1_000;
        request.DiscoveryStepMaxEntries = 10_000;
        request.DiscoveryStepMaxSeconds = 45;
        request.SourceAccountBindingHash = "account-binding";
        return request;
    }

    private static async Task<BulkScoringRequest> ActiveAsync(
        Fixture fixture,
        string prefix,
        bool withAuthorization = false)
    {
        var draft = await fixture.Repo.CreateDraftAsync(
            NewRequest(fixture),
            Step(Items(1, prefix)));
        var authorization = withAuthorization
            ? BulkRequestAuthorization.Establish(
                draft.Id,
                draft.OwnerActorId,
                draft.SourceTenantId,
                draft.SourceAccountBindingHash,
                draft.SourceDriveId,
                draft.SourceFolderItemId,
                [1],
                [1],
                "v1",
                Now)
            : null;
        var active = await fixture.Repo.ConfirmAsync(
            draft.Id,
            draft.ConcurrencyVersion,
            false,
            false,
            null,
            authorization,
            Now);
        fixture.Db.ChangeTracker.Clear();
        return active;
    }

    private static async Task TerminalizeAsync(
        Fixture fixture,
        BulkScoringRequest request,
        BulkItemStatus status,
        BulkReasonCode? reason = null)
    {
        var item = await fixture.Db.BulkScoringItems.SingleAsync(value => value.RequestId == request.Id);
        if (status == BulkItemStatus.Completed)
        {
            item.TransitionTo(BulkItemStatus.Importing, Now);
            item.TransitionTo(BulkItemStatus.Scoring, Now);
        }
        item.TransitionTo(status, Now, reason);
        request.WaitingCount = 0;
        request.CompletedCount = status == BulkItemStatus.Completed ? 1 : 0;
        request.FailedCount = status == BulkItemStatus.Failed ? 1 : 0;
        request.CancelledCount = status == BulkItemStatus.Cancelled ? 1 : 0;
        if (status == BulkItemStatus.Cancelled)
            request.RequestCancel("actor", Now);
        request.Complete(Now);
        fixture.Db.Update(request);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
    }

    private static void AssertDeletionEnvelope(IReadOnlyCollection<BulkAuditEvent> events, string deletingUser)
    {
        events.Should().NotBeEmpty();
        events.Should().OnlyContain(value => IsGuid(value.CorrelationId));
        events.Should().OnlyContain(value => value.OccurredAt.Kind == DateTimeKind.Utc);
        events.Should().OnlyContain(value => value.SchemaVersion > 0);
        events.Should().OnlyContain(value =>
            value.ActorType == BulkAuditActorType.Service
            && !string.IsNullOrWhiteSpace(value.ActorId)
            && value.InitiatedByActorId == deletingUser);
        events.Should().OnlyContain(value => value.Boundary == BulkAuditBoundary.Reconciliation);
        events.Should().AllSatisfy(value => value.Validate());
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);

    private static void AssertProviderModel(AppDbContext context)
    {
        var request = context.Model.FindEntityType(typeof(BulkScoringRequest));
        var item = context.Model.FindEntityType(typeof(BulkScoringItem));
        var content = context.Model.FindEntityType(typeof(BulkScoringItemContent));
        var authorization = context.Model.FindEntityType(typeof(BulkRequestAuthorization));
        var audit = context.Model.FindEntityType(typeof(BulkAuditEvent));

        request.Should().NotBeNull();
        item.Should().NotBeNull();
        content.Should().NotBeNull();
        authorization.Should().NotBeNull();
        audit.Should().NotBeNull();
        request!.GetForeignKeys().Single(value => value.PrincipalEntityType.ClrType == typeof(Job))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        item!.GetForeignKeys().Single(value => value.PrincipalEntityType.ClrType == typeof(BulkScoringRequest))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        content!.GetForeignKeys().Single(value => value.PrincipalEntityType.ClrType == typeof(BulkScoringItem))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        authorization!.GetForeignKeys().Single(value => value.PrincipalEntityType.ClrType == typeof(BulkScoringRequest))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        audit!.GetForeignKeys().Should().BeEmpty();
    }

    private sealed class RecordingContentStore : IBulkContentStore
    {
        public HashSet<string> Objects { get; } = [];
        public int FailuresRemaining { get; set; }

        public void Stage(string requestId, string itemId, string kind) =>
            Objects.Add($"bulk/{requestId}/{itemId}/{kind}");

        public Task<BulkScoringItemContent> StageAsync(
            string requestId,
            string itemId,
            BulkContentKind kind,
            string fileName,
            string mimeType,
            byte[] bytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadAsync(
            BulkScoringItemContent content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            BulkScoringItemContent content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteByRequestAsync(string requestId, CancellationToken cancellationToken = default)
        {
            if (FailuresRemaining-- > 0)
                throw new IOException("simulated partial cleanup");
            Objects.RemoveWhere(value => value.StartsWith($"bulk/{requestId}/", StringComparison.Ordinal));
            return Task.CompletedTask;
        }
    }

    private sealed class AdminUser : ICurrentUserService
    {
        public string? UserId => "admin";
        public string? Username => "admin";
        public string? Role => "admin";
        public string? Department => null;
        public bool IsAdmin => true;
        public Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CurrentAuthorizationState?>(null);
    }
}
