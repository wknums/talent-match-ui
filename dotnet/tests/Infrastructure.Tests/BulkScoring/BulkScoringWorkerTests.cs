using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.HostedServices;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Services;
using TalentMatch.Infrastructure.SharePoint;
using TalentMatch.Infrastructure.SharePoint.Authorization;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

// Runs the real hosted worker against a database file with no HTTP client involved at all: once a request
// is confirmed, nothing about the recruiter's browser or machine matters (SC-003, SC-012).
public sealed class BulkScoringWorkerTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";

    [Fact]
    public async Task A_confirmed_request_finishes_without_any_client_and_discards_its_authorization()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: false);
        await host.ProcessAllAsync();

        var scoring = await host.RequestAsync(requestId);
        scoring.ScoringCount.Should().Be(9);
        scoring.Status.Should().Be(BulkRequestStatus.Active);
        scoring.SkippedCount.Should().Be(2, "two byte-identical duplicates");
        (await host.Db.Applications.CountAsync()).Should().Be(9);

        // The existing scoring pipeline finishes the applications; the worker only observes the result.
        await host.Db.Applications.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Completed"));
        await host.ObserveScoringAndFinalizeAsync(requestId, discardAuthorization: true);

        var done = await host.RequestAsync(requestId);
        done.Outcome.Should().Be(BulkRequestOutcome.AllSucceeded);
        done.CompletedCount.Should().Be(9);
        done.TerminalAt.Should().NotBeNull();
        using var scope = host.Provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>().ListUnacknowledgedAsync("recruiter@contoso.com"))
            .Should().ContainSingle(r => r.Id == requestId);

        await Eventually(async () =>
            (await scope.ServiceProvider.GetRequiredService<IBulkRequestAuthorizationRepository>().GetAsync(requestId))!.Status == BulkAuthorizationStatus.Discarded,
            "the authorization is discarded once the request finished");
        var auth = await host.Db.BulkRequestAuthorizations.AsNoTracking().SingleAsync();
        auth.Payload.Should().BeNull();
        auth.WrappedDataKey.Should().BeNull();
    }

    [Fact]
    public async Task A_restarted_host_carries_on_without_duplicating_applications()
    {
        await using var first = await WorkerHost.CreateAsync();
        var requestId = await first.ConfirmAsync(Candidates, allowDuplicates: true);
        await first.ProcessAllAsync(maxItems: 3);
        (await first.RequestAsync(requestId)).ScoringCount.Should().Be(3);

        await using var second = await WorkerHost.AttachAsync(first.DatabasePath);
        await second.ProcessAllAsync();
        (await second.RequestAsync(requestId)).ScoringCount.Should().Be(11);

        (await second.Db.Applications.CountAsync()).Should().Be(11, "no duplicate applications after the restart");
        (await second.Db.ApplicationDocuments.CountAsync()).Should().Be(11);
        (await second.Db.BulkScoringItems.AsNoTracking().Select(i => i.ApplicationId).Distinct().CountAsync()).Should().Be(11);

        var idempotencyEvents = await second.Db.BulkAuditEvents.AsNoTracking()
            .Where(e => WorkerIdempotencyEventTypes.Contains(e.EventType))
            .ToListAsync();
        idempotencyEvents.Should().NotBeEmpty("restart-safe stage, create, and handoff markers must be typed durable events");
        idempotencyEvents.GroupBy(e => (e.SubjectId, e.EventType)).Should()
            .OnlyContain(group => group.Count() == 1, "a restart must not duplicate a durable lifecycle event");
    }

    [Fact]
    public async Task A_lapsed_authorization_fails_the_remaining_files_with_a_sign_in_again_reason()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        host.Provider.GetRequiredService<MockAuthorizationControl>().Lapse(requestId, "interaction_required");
        await host.ProcessAllAsync();
        await host.ObserveScoringAndFinalizeAsync(requestId, discardAuthorization: false);

        var request = await host.RequestAsync(requestId);
        request.Outcome.Should().Be(BulkRequestOutcome.Failed);
        request.FailedCount.Should().Be(11);
        var items = await host.Db.BulkScoringItems.AsNoTracking().ToListAsync();
        items.Should().OnlyContain(i => i.ReasonCode == BulkReasonCode.AuthorizationLapsed && i.ApplicationId == null);
        items.Should().OnlyContain(i => i.ReasonMessage!.Contains("Sign in again"));
        var auth = await host.Db.BulkRequestAuthorizations.AsNoTracking().SingleAsync();
        auth.Status.Should().Be(BulkAuthorizationStatus.Lapsed);
        auth.LapseReason.Should().Be("interaction_required");
        auth.Payload.Should().BeNull();
        (await host.Db.Applications.CountAsync()).Should().Be(0, "nothing was read once the authorization lapsed");

        using var scope = host.Provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>().GetRetryableItemsAsync(requestId))
            .Should().HaveCount(11, "after signing in again every one of them can be retried");
    }

    [Fact]
    public async Task Item_claim_is_owned_atomic_and_emits_one_typed_service_event()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        using var scope = host.Provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();

        var claimed = await repository.ClaimDueItemsAsync(
            "bulk-import-worker",
            1,
            DateTime.UtcNow,
            TimeSpan.FromMinutes(5));

        var item = claimed.Should().ContainSingle().Which;
        item.Status.Should().Be(BulkItemStatus.Importing);
        item.LeaseOwner.Should().Be("bulk-import-worker");
        var claimEvent = (await WorkerEventsAsync(host, requestId)).Should()
            .ContainSingle(e => e.SubjectId == item.Id && e.EventType == BulkAuditEventTypes.BulkItemClaimed)
            .Which;
        AssertExplicitSafeServiceEvents([claimEvent]);
    }

    [Fact]
    public async Task Claim_recover_and_graceful_release_emit_atomic_service_events()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        var now = DateTime.UtcNow;
        using var scope = host.Provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();

        var itemId = await host.Db.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId)
            .OrderBy(i => i.Ordinal)
            .Select(i => i.Id)
            .FirstAsync();
        await host.Db.BulkScoringItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(setters => setters
            .SetProperty(i => i.Status, BulkItemStatus.Importing)
            .SetProperty(i => i.LeaseOwner, "bulk-import-worker")
            .SetProperty(i => i.LeaseUntil, now.AddMinutes(1))
            .SetProperty(i => i.Attempt, 1));
        (await repository.ReclaimExpiredLeasesAsync(now.AddMinutes(2))).Should().Be(1);
        await host.Db.BulkScoringItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(setters => setters
            .SetProperty(i => i.Status, BulkItemStatus.Importing)
            .SetProperty(i => i.LeaseOwner, "bulk-import-worker-restarted")
            .SetProperty(i => i.LeaseUntil, now.AddMinutes(4))
            .SetProperty(i => i.Attempt, i => i.Attempt + 1));
        await repository.ReleaseLeasesAsync("bulk-import-worker-restarted");

        host.Db.ChangeTracker.Clear();
        var item = await host.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        item.Status.Should().Be(BulkItemStatus.Waiting);
        item.LeaseOwner.Should().BeNull();

        var events = await WorkerEventsAsync(host, requestId);
        events.Where(e => e.SubjectId == item.Id).Select(e => e.EventType).Should().Contain(
        [
            BulkAuditEventTypes.BulkItemLeaseRecovered,
            BulkAuditEventTypes.BulkItemLeaseReleased,
        ]);
        AssertExplicitSafeServiceEvents(events.Where(e => e.SubjectId == item.Id));
    }

    [Fact]
    public void Lease_renewal_is_an_explicit_owned_repository_operation()
    {
        var renew = typeof(IBulkScoringRepository).GetMethods()
            .Where(method => method.Name == "RenewLeaseAsync")
            .ToList();

        renew.Should().ContainSingle("T067 must expose an ownership-checked lease renewal operation");
        renew[0].ReturnType.Should().Be(typeof(Task<bool>));
        renew[0].GetParameters().Select(parameter => parameter.ParameterType).Should().Contain(
        [
            typeof(string),
            typeof(DateTime),
            typeof(TimeSpan),
            typeof(CancellationToken),
        ]);
    }

    [Fact]
    public async Task Worker_import_terminal_and_reconciliation_mutations_have_the_complete_typed_event_matrix()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        await host.ProcessAllAsync();
        (await host.RequestAsync(requestId)).ScoringCount.Should().Be(11);

        await host.Db.Applications.ExecuteUpdateAsync(setters => setters.SetProperty(application => application.Status, "Completed"));
        await host.ObserveScoringAndFinalizeAsync(requestId, discardAuthorization: true);

        var events = await WorkerEventsAsync(host, requestId);
        events.Should().NotContain(
            e => e.EventType == BulkAuditEventTypes.BulkItemScoringStarted,
            "the downstream scoring subsystem, not the bulk import worker, owns scoring start");
        events.Select(e => e.EventType).Should().Contain(
        [
            BulkAuditEventTypes.BulkItemClaimed,
            BulkAuditEventTypes.BulkItemImportStarted,
            BulkAuditEventTypes.BulkItemSourceMetadataRecheckSucceeded,
            BulkAuditEventTypes.BulkOriginalContentStaged,
            BulkAuditEventTypes.BulkItemFingerprintRecorded,
            BulkAuditEventTypes.BulkOriginalContentDeleted,
            BulkAuditEventTypes.BulkItemApplicationCreated,
            BulkAuditEventTypes.BulkItemDocumentCreated,
            BulkAuditEventTypes.BulkItemScoringHandoffQueued,
            BulkAuditEventTypes.BulkItemCompleted,
            BulkAuditEventTypes.BulkRequestCountsReconciled,
            BulkAuditEventTypes.BulkRequestTerminalFinalized,
        ]);

        events.Where(e => e.EventType == BulkAuditEventTypes.BulkItemScoringHandoffQueued)
            .Should().OnlyContain(e => e.Boundary == BulkAuditBoundary.DurableOutbox);
        events.Where(e => e.EventType == BulkAuditEventTypes.BulkOriginalContentDeleted)
            .Should().OnlyContain(e => e.Boundary == BulkAuditBoundary.Reconciliation);
        AssertExplicitSafeServiceEvents(events);
    }

    [Fact]
    public async Task Metadata_recheck_failure_and_item_terminal_state_are_one_typed_atomic_outcome()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        var target = await host.Db.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId)
            .OrderBy(i => i.Ordinal)
            .FirstAsync();
        host.Provider.GetRequiredService<MockSharePointSource>().ChangeItem(target.SourceDriveItemId);

        var (processed, outcome) = await host.ProcessOneAsync("bulk-import-worker");

        processed.Id.Should().Be(target.Id);
        outcome.Should().Be(BulkItemOutcome.Failed);
        host.Db.ChangeTracker.Clear();
        var failed = await host.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == target.Id);
        failed.Status.Should().Be(BulkItemStatus.Failed);
        failed.ReasonCode.Should().Be(BulkReasonCode.SourceChanged);
        failed.TerminalAt.Should().NotBeNull();

        var events = (await WorkerEventsAsync(host, requestId)).Where(e => e.SubjectId == target.Id).ToList();
        events.Select(e => e.EventType).Should().Contain(
        [
            BulkAuditEventTypes.BulkItemClaimed,
            BulkAuditEventTypes.BulkItemImportStarted,
            BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed,
            BulkAuditEventTypes.BulkItemFailed,
            BulkAuditEventTypes.BulkRequestCountsReconciled,
        ]);
        AssertExplicitSafeServiceEvents(events);
    }

    [Fact]
    public async Task Conversion_creation_and_scoring_handoff_have_idempotent_typed_boundaries()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true, convertToRasterPdf: true);

        var (processed, outcome) = await host.ProcessOneAsync("bulk-conversion-worker");

        outcome.Should().Be(BulkItemOutcome.HandedOff);
        processed.Status.Should().Be(BulkItemStatus.Scoring);
        processed.ApplicationId.Should().NotBeNull();
        processed.DocumentId.Should().NotBeNull();
        processed.ConvertedStoredAt.Should().NotBeNull();
        (await host.Db.Applications.CountAsync(a => a.Id == processed.ApplicationId)).Should().Be(1);
        (await host.Db.ApplicationDocuments.CountAsync(d => d.Id == processed.DocumentId)).Should().Be(1);

        var events = (await WorkerEventsAsync(host, requestId)).Where(e => e.SubjectId == processed.Id).ToList();
        events.Select(e => e.EventType).Should().Contain(
        [
            BulkAuditEventTypes.BulkItemConversionStarted,
            BulkAuditEventTypes.BulkConvertedContentStaged,
            BulkAuditEventTypes.BulkItemConversionCompleted,
            BulkAuditEventTypes.BulkItemApplicationCreated,
            BulkAuditEventTypes.BulkItemDocumentCreated,
            BulkAuditEventTypes.BulkItemScoringHandoffQueued,
        ]);
        events.Where(e => e.EventType == BulkAuditEventTypes.BulkItemScoringHandoffQueued)
            .Should().ContainSingle(e => e.Boundary == BulkAuditBoundary.DurableOutbox);
        AssertExplicitSafeServiceEvents(events);
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_the_SQLite_item_and_content_state()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        var itemId = await host.Db.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId)
            .OrderBy(i => i.Ordinal)
            .Select(i => i.Id)
            .FirstAsync();
        await host.Db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER RejectWorkerStageAudit
            BEFORE INSERT ON BulkAuditEvents
            WHEN NEW.EventType = 'bulk_original_content_staged'
            BEGIN
                SELECT RAISE(ABORT, 'injected audit failure');
            END;
            """);

        var exception = await Record.ExceptionAsync(() => host.ProcessOneAsync("bulk-import-worker"));

        host.Db.ChangeTracker.Clear();
        var item = await host.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        item.Status.Should().Be(BulkItemStatus.Importing, "item state and its typed audit row must roll back together");
        item.SourceStoredAt.Should().BeNull();
        (await host.Db.BulkScoringItemContents.CountAsync(c => c.ItemId == itemId)).Should()
            .Be(0, "inline content cannot be orphaned when its typed stage event fails");
        exception.Should().NotBeNull("the injected typed-audit failure must abort the item transaction");
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("SQL Server")]
    public void Provider_models_have_identical_lease_and_typed_event_semantics(string provider)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == "SQLite")
            options.UseSqlite("Data Source=:memory:");
        else
            options.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BulkWorkerModelOnly;Trusted_Connection=True");
        using var context = new AppDbContext(options.Options);

        var item = context.Model.FindEntityType(typeof(BulkScoringItem));
        var audit = context.Model.FindEntityType(typeof(BulkAuditEvent));
        item.Should().NotBeNull();
        audit.Should().NotBeNull();
        item!.FindProperty(nameof(BulkScoringItem.LeaseOwner)).Should().NotBeNull();
        item.FindProperty(nameof(BulkScoringItem.LeaseUntil)).Should().NotBeNull();
        item.FindProperty(nameof(BulkScoringItem.Attempt)).Should().NotBeNull();
        audit!.FindProperty(nameof(BulkAuditEvent.ActorType)).Should().NotBeNull();
        audit.FindProperty(nameof(BulkAuditEvent.Boundary)).Should().NotBeNull();
        audit.GetForeignKeys().Should().BeEmpty("worker evidence must survive request, item, and content cleanup");
    }

    private static readonly string[] WorkerIdempotencyEventTypes =
    [
        BulkAuditEventTypes.BulkOriginalContentStaged,
        BulkAuditEventTypes.BulkConvertedContentStaged,
        BulkAuditEventTypes.BulkItemApplicationCreated,
        BulkAuditEventTypes.BulkItemDocumentCreated,
        BulkAuditEventTypes.BulkItemScoringHandoffQueued,
    ];

    private static async Task<List<BulkAuditEvent>> WorkerEventsAsync(WorkerHost host, string requestId)
    {
        var request = await host.RequestAsync(requestId);
        return await host.Db.BulkAuditEvents.AsNoTracking()
            .Where(e => e.CorrelationId == request.CorrelationId
                && WorkerLifecycleEventTypes.Contains(e.EventType))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.AuditEventId)
            .ToListAsync();
    }

    private static readonly string[] WorkerLifecycleEventTypes =
    [
        BulkAuditEventTypes.BulkItemClaimed,
        BulkAuditEventTypes.BulkItemLeaseRenewed,
        BulkAuditEventTypes.BulkItemLeaseRecovered,
        BulkAuditEventTypes.BulkItemLeaseReleased,
        BulkAuditEventTypes.BulkItemImportStarted,
        BulkAuditEventTypes.BulkItemSourceMetadataRecheckSucceeded,
        BulkAuditEventTypes.BulkItemSourceMetadataRecheckFailed,
        BulkAuditEventTypes.BulkOriginalContentStaged,
        BulkAuditEventTypes.BulkConvertedContentStaged,
        BulkAuditEventTypes.BulkItemFingerprintRecorded,
        BulkAuditEventTypes.BulkItemDuplicateClaimed,
        BulkAuditEventTypes.BulkItemDuplicateSkipped,
        BulkAuditEventTypes.BulkItemConversionStarted,
        BulkAuditEventTypes.BulkItemConversionCompleted,
        BulkAuditEventTypes.BulkItemConversionFailed,
        BulkAuditEventTypes.BulkConvertedOutputQuarantined,
        BulkAuditEventTypes.BulkConvertedOutputRemoved,
        BulkAuditEventTypes.BulkItemApplicationCreated,
        BulkAuditEventTypes.BulkItemDocumentCreated,
        BulkAuditEventTypes.BulkItemScoringHandoffQueued,
        BulkAuditEventTypes.BulkItemScoringStarted,
        BulkAuditEventTypes.BulkItemCompleted,
        BulkAuditEventTypes.BulkItemFailed,
        BulkAuditEventTypes.BulkItemSkipped,
        BulkAuditEventTypes.BulkItemCancelled,
        BulkAuditEventTypes.BulkRequestCountsReconciled,
        BulkAuditEventTypes.BulkRequestTerminalFinalized,
        BulkAuditEventTypes.BulkOriginalContentDeleted,
        BulkAuditEventTypes.BulkConvertedContentDeleted,
        BulkAuditEventTypes.BulkTransientContentDeleted,
    ];

    private static void AssertExplicitSafeServiceEvents(IEnumerable<BulkAuditEvent> events)
    {
        var rows = events.ToList();
        rows.Should().NotBeEmpty("every feature-owned worker mutation requires typed evidence");
        rows.Should().OnlyContain(e =>
            e.ActorType == BulkAuditActorType.Service
            && !string.IsNullOrWhiteSpace(e.ActorId)
            && !string.Equals(e.ActorId, "system", StringComparison.OrdinalIgnoreCase)
            && e.OccurredAt.Kind == DateTimeKind.Utc);

        string[] prohibited =
        [
            "authorizationcode", "token", "verifier", "cache", "key", "url", "foldername",
            "itemname", "filename", "email", "account", "fingerprint", "contentbase64",
            "providerpayload", "providererror", "environment",
        ];
        foreach (var row in rows)
        {
            row.Validate();
            using var document = JsonDocument.Parse(row.SafeFactsJson);
            var serialized = document.RootElement.GetRawText().ToLowerInvariant();
            prohibited.Should().AllSatisfy(term => serialized.Should().NotContain(term));
        }
    }

    internal static async Task Eventually(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        (await condition()).Should().BeTrue("timed out waiting: " + because);
    }

    internal sealed class WorkerHost : IAsyncDisposable
    {
        private readonly bool _ownsDatabase;
        private IHostedService? _worker;

        public string DatabasePath { get; }
        public ServiceProvider Provider { get; }
        public AppDbContext Db { get; }

        private WorkerHost(string path, bool ownsDatabase)
        {
            DatabasePath = path;
            _ownsDatabase = ownsDatabase;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MICROSOFT365_SOURCE_MODE"] = "mock",
                ["MICROSOFT365_SOURCE_PUBLISHER_VERIFIED"] = "true",
                ["BULK_IMPORT_CONCURRENCY"] = "2",
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                // a persistent dev key so a restarted host can unwrap the authorizations written before it stopped
                ["BULK_AUTH_DEV_KEY_PATH"] = path + ".key",
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IBlobStore>(new InlineBlobStore());
            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={path};Default Timeout=60"), ServiceLifetime.Scoped);
            services.AddScoped<IApplicationRepository, ApplicationRepository>();
            services.AddScoped<IBulkScoringRepository, BulkScoringRepository>();
            services.AddScoped<IBulkImportSettingsRepository, BulkImportSettingsRepository>();
            services.AddScoped<IBulkRequestAuthorizationRepository, BulkRequestAuthorizationRepository>();
            services.AddScoped<IProcessingEventRepository, ProcessingEventRepository>();
            services.AddScoped<IBulkAuditLedger, BulkAuditLedger>();
            services.AddBulkScoring(config);
            services.Replace(ServiceDescriptor.Scoped<IRequestAuthorizationStore, WorkerAuthorizationStore>());
            services.Replace(ServiceDescriptor.Singleton<IDocumentRasterizer, WorkerRasterizer>());
            Provider = services.BuildServiceProvider();
            Db = Provider.GetRequiredService<AppDbContext>();
        }

        public static async Task<WorkerHost> CreateAsync()
        {
            var artifacts = Path.Combine(AppContext.BaseDirectory, "test-artifacts");
            Directory.CreateDirectory(artifacts);
            var path = Path.Combine(artifacts, $"bulk-worker-{Guid.NewGuid():N}.db");
            var host = new WorkerHost(path, ownsDatabase: true);
            await host.Db.Database.EnsureCreatedAsync();
            host.Db.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await host.Db.SaveChangesAsync();
            return host;
        }

        public static Task<WorkerHost> AttachAsync(string path) => Task.FromResult(new WorkerHost(path, ownsDatabase: false));

        public async Task<string> ConfirmAsync(string url, bool allowDuplicates, bool convertToRasterPdf = false)
        {
            using var scope = Provider.CreateScope();
            var sp = scope.ServiceProvider;
            var options = sp.GetRequiredService<BulkScoringOptions>();
            var source = sp.GetRequiredService<ISharePointSource>();
            var repo = sp.GetRequiredService<IBulkScoringRepository>();
            var clock = TimeProvider.System;

            var context = new SourceCallContext(SourcePhase.Interactive, "recruiter@contoso.com");
            var folder = await source.ResolveFolderAsync(url, context);
            var sourceTenantId = string.IsNullOrWhiteSpace(folder.TenantId)
                ? "fixture-tenant-primary"
                : folder.TenantId;
            var limits = BulkRequestLimits.Snapshot(options, 8_388_608, 30_000);
            var request = new BulkScoringRequest
            {
                JobId = "job-1",
                OwnerActorId = "recruiter@contoso.com",
                SourceKind = folder.Kind,
                SourceAccountBindingHash = "fixture-account-binding",
                SourceDriveId = folder.DriveId,
                SourceFolderItemId = folder.ItemId,
                SourceFolderName = folder.Name,
                SourceFolderPath = folder.Path,
                SourceWebUrl = folder.WebUrl,
                AccessModel = SharePointAccessModel.SameTenant,
                SourceTenantId = sourceTenantId,
                OwningStack = "B",
                LimitsJson = limits.ToJson(),
                MaxFilesPerRequest = limits.MaxFilesPerRequest,
                MaxBytesPerFile = limits.MaxBytesPerFile,
                MaxTotalBytes = limits.MaxTotalBytes,
                DiscoveryStepMaxFiles = limits.DiscoveryStepFiles,
                DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries,
                DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds,
                RasterDpi = limits.RasterDpi,
            };

            var steps = sp.GetRequiredService<DiscoveryStepService>();
            var outcome = await steps.RunStepAsync(new DiscoveryStepInput(request.Id, folder, null, context, 8_388_608, 30_000, long.MaxValue, 0));
            var draft = await repo.CreateDraftAsync(request, outcome.ToStepResult(clock.GetUtcNow().UtcDateTime.AddMinutes(30)));
            var authorization = BulkRequestAuthorization.Establish(
                draft.Id,
                "recruiter@contoso.com",
                sourceTenantId,
                request.SourceAccountBindingHash,
                folder.DriveId,
                folder.ItemId,
                [1],
                [2],
                "test-key",
                clock.GetUtcNow().UtcDateTime);
            await repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, convertToRasterPdf, allowDuplicates, "cfg", authorization, clock.GetUtcNow().UtcDateTime);
            Db.ChangeTracker.Clear();
            return draft.Id;
        }

        public async Task<(BulkScoringItem Item, BulkItemOutcome Outcome)> ProcessOneAsync(string workerActorId)
        {
            using var scope = Provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();
            var itemId = await Db.BulkScoringItems.AsNoTracking()
                .Where(i => i.Status == BulkItemStatus.Waiting
                    && (i.NextAttemptAt == null || i.NextAttemptAt <= DateTime.UtcNow))
                .OrderBy(i => i.Ordinal)
                .Select(i => i.Id)
                .FirstAsync();
            var now = DateTime.UtcNow;
            await Db.BulkScoringItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Status, BulkItemStatus.Importing)
                .SetProperty(i => i.LeaseOwner, workerActorId)
                .SetProperty(i => i.LeaseUntil, now.AddMinutes(5))
                .SetProperty(i => i.Attempt, i => i.Attempt + 1)
                .SetProperty(i => i.UpdatedAt, now));
            var item = await Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
            var request = (await repository.GetAsync(item.RequestId))!;
            var outcome = await scope.ServiceProvider.GetRequiredService<BulkScoringCycle>()
                .RunItemAsync(item, CancellationToken.None);
            return (item, outcome!.Value);
        }

        public async Task ProcessAllAsync(int maxItems = int.MaxValue)
        {
            var processed = 0;
            while (processed < maxItems
                && await Db.BulkScoringItems.AsNoTracking().AnyAsync(i => i.Status == BulkItemStatus.Waiting))
            {
                await ProcessOneAsync("bulk-import-worker");
                processed++;
            }
        }

        public async Task ObserveScoringAndFinalizeAsync(string requestId, bool discardAuthorization)
        {
            using var scope = Provider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();
            var applications = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            foreach (var item in await repository.GetScoringItemsBatchAsync(200))
            {
                var application = await applications.GetByIdAsync(item.ApplicationId!);
                if (application?.Status is "Completed" or "NeedsManualReview")
                {
                    item.TransitionTo(BulkItemStatus.Completed, DateTime.UtcNow);
                    await repository.SaveItemAsync(item);
                }
            }

            await repository.TryFinalizeAsync(requestId, DateTime.UtcNow);
            if (discardAuthorization)
                await scope.ServiceProvider.GetRequiredService<IRequestAuthorizationStore>().DiscardAsync(requestId);
        }

        public async Task<BulkScoringRequest> RequestAsync(string id)
        {
            using var scope = Provider.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>().GetAsync(id))!;
        }

        public async Task StartWorkerAsync()
        {
            _worker = Provider.GetServices<IHostedService>().OfType<BulkScoringWorker>().Single();
            await _worker.StartAsync(CancellationToken.None);
        }

        public async Task StopWorkerAsync()
        {
            if (_worker is null) return;
            await _worker.StopAsync(CancellationToken.None);
            _worker = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopWorkerAsync();
            await Provider.DisposeAsync();
            if (_ownsDatabase)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(DatabasePath)!, Path.GetFileName(DatabasePath) + "*"))
                    try { File.Delete(file); } catch (IOException) { }
            }
        }

        private sealed class WorkerRasterizer : IDocumentRasterizer
        {
            public Task<RasterizationResult> RasterizeAsync(
                byte[] source,
                string mimeType,
                RasterizationOptions options,
                CancellationToken cancellationToken = default)
            {
                var output = new byte[512];
                "%PDF-raster"u8.CopyTo(output);
                return Task.FromResult(new RasterizationResult(output, 2)
                {
                    OriginalPageCount = 2,
                    RasterDpiUsed = options.Dpi,
                    MinimumPageSsim = 1,
                    FidelityValidatedAt = DateTime.UtcNow,
                });
            }
        }

        private sealed class WorkerAuthorizationStore(
            IBulkRequestAuthorizationRepository repository,
            MockAuthorizationControl control,
            TimeProvider clock) : IRequestAuthorizationStore
        {
            public Task<BulkRequestAuthorization> TransferToRequestAsync(
                string requestId,
                string connectionId,
                string recruiterActorId,
                string jobId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The worker test binds authorization directly to its fixture folder.");

            public async Task<string> AcquireGraphTokenAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                if (control.TryGetLapse(requestId, out var reason))
                    throw new SourceAuthorizationLapsedException(reason);
                var authorization = await repository.GetAsync(requestId, cancellationToken)
                    ?? throw new SourceAuthorizationLapsedException("authorization_missing");
                if (!authorization.IsActive)
                    throw new SourceAuthorizationLapsedException("authorization_not_active");
                authorization.RecordUse(clock.GetUtcNow().UtcDateTime);
                await repository.SaveAsync(authorization, cancellationToken);
                return "fixture-delegated-token";
            }

            public async Task MarkLapsedAsync(
                string requestId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                var authorization = await repository.GetAsync(requestId, cancellationToken);
                if (authorization is null || !authorization.IsActive) return;
                authorization.MarkLapsed(reason, clock.GetUtcNow().UtcDateTime);
                await repository.SaveAsync(authorization, cancellationToken);
            }

            public async Task DiscardAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                var authorization = await repository.GetAsync(requestId, cancellationToken);
                if (authorization is null || authorization.Status == BulkAuthorizationStatus.Discarded) return;
                authorization.Discard(clock.GetUtcNow().UtcDateTime);
                await repository.SaveAsync(authorization, cancellationToken);
            }
        }
    }
}
