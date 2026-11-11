using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Services;
using TalentMatch.Infrastructure.SharePoint;
using TalentMatch.Infrastructure.SharePoint.Authorization;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringWorkerTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

// What happens to a same-tenant request when the recruiter's stored sign-in stops working (SC-017, SC-018).
// The worker is driven one cycle at a time so each state can be inspected. Graph 403 on one file, a file the recruiter
// lost access to, transient token errors and the identity provider's own error codes are covered where those paths live:
// BulkImportPipelineTests, GraphSharePointSourceTests and RequestAuthorizationStoreTests.
public sealed class BulkAuthorizationLapseTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task A_lapse_before_staging_stops_on_the_first_Graph_call_without_trying_a_fallback()
    {
        await using var scenario = await ProcessorScenario.CreateAsync(BulkItemStatus.Importing);
        scenario.Source.LapseImmediately = true;

        var act = () => scenario.Processor.ProcessAsync(scenario.Item, scenario.Request, default);

        await act.Should().ThrowAsync<SourceAuthorizationLapsedException>();
        scenario.Source.Calls.Should().ContainSingle().Which.Should().Be("metadata");
        scenario.Source.Contexts.Should().OnlyContain(c =>
            c.Phase == SourcePhase.Background
            && c.RecruiterActorId == scenario.Request.OwnerActorId
            && c.RequestId == scenario.Request.Id);
        (await scenario.Fixture.Db.BulkScoringItemContents.CountAsync()).Should().Be(0);
        (await scenario.Fixture.Db.Applications.CountAsync()).Should().Be(0, "there is no application or fallback identity path before staging");
        (await scenario.Fixture.Db.BulkScoringItems.AsNoTracking().SingleAsync()).SourceStoredAt.Should().BeNull();
    }

    [Theory]
    [InlineData(BulkItemStatus.Importing)]
    [InlineData(BulkItemStatus.Converting)]
    public async Task SourceStoredAt_makes_importing_and_converting_source_independent(BulkItemStatus phase)
    {
        await using var scenario = await ProcessorScenario.CreateAsync(phase);

        var outcome = await scenario.Processor.ProcessAsync(scenario.Item, scenario.Request, default);

        outcome.Should().Be(BulkItemOutcome.HandedOff);
        scenario.Source.CallsAfterBoundary.Should().BeEmpty("Graph must stop immediately at the durable source boundary");
        scenario.Content.ReadKinds.Should().Contain([BulkContentKind.Original, BulkContentKind.Converted],
            "conversion and scoring consume the product-owned staged copies");
        scenario.Source.Contexts.Should().OnlyContain(c =>
            c.Phase == SourcePhase.Background
            && c.RecruiterActorId == scenario.Request.OwnerActorId
            && c.RequestId == scenario.Request.Id);
        scenario.Rasterizer.Inputs.Should().ContainSingle().Which.Should().Equal(ProcessorScenario.SourcePdf);
        var saved = await scenario.Fixture.Db.BulkScoringItems.AsNoTracking().SingleAsync();
        saved.Status.Should().Be(BulkItemStatus.Scoring);
        saved.ScoredVersion.Should().Be(BulkScoredVersion.Converted, "conversion never falls back to the original");
        var document = await scenario.Fixture.Db.ApplicationDocuments.AsNoTracking().SingleAsync();
        var blob = await scenario.Fixture.Db.DocumentBlobs.AsNoTracking().SingleAsync(b => b.DocumentId == document.Id);
        Convert.FromBase64String(blob.Content).Should().Equal(ProcessorScenario.RasterPdf);
    }

    [Fact]
    public async Task A_lapse_fails_every_unfinished_file_and_leaves_finished_and_scoring_files_and_reads_nothing_more()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        using var scope = host.Provider.CreateScope();
        var cycle = scope.ServiceProvider.GetRequiredService<BulkScoringCycle>();

        // Two files are imported and handed to scoring before the sign-in lapses; one of them then finishes.
        foreach (var item in await cycle.ClaimAsync("t1", default)) await cycle.RunItemAsync(item, default);
        var handedOff = await host.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId && i.Status == BulkItemStatus.Scoring).OrderBy(i => i.Ordinal).ToListAsync();
        handedOff.Should().HaveCount(2);
        await host.Db.Applications.Where(a => a.Id == handedOff[0].ApplicationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Completed"));
        await cycle.SweepAsync(default);
        var applicationsBefore = await host.Db.Applications.CountAsync();

        host.Provider.GetRequiredService<MockAuthorizationControl>().Lapse(requestId, "session_revoked");
        foreach (var item in await cycle.ClaimAsync("t1", default)) await cycle.RunItemAsync(item, default);

        host.Db.ChangeTracker.Clear();
        var items = await host.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId).OrderBy(i => i.Ordinal).ToListAsync();
        items.Count(i => i.Status == BulkItemStatus.Completed).Should().Be(1, "finished work is unchanged");
        items.Count(i => i.Status == BulkItemStatus.Scoring).Should().Be(1, "a file already in scoring continues");
        var failed = items.Where(i => i.Status == BulkItemStatus.Failed).ToList();
        failed.Should().HaveCount(9, "every other file, including the one that discovered the lapse");
        failed.Should().OnlyContain(i => i.ReasonCode == BulkReasonCode.AuthorizationLapsed && i.ApplicationId == null);
        failed.Should().OnlyContain(i => i.ReasonMessage!.Contains("Sign in again") && i.ReasonMessage.Contains("Retry failed"));
        (await host.Db.Applications.CountAsync()).Should().Be(applicationsBefore, "nothing was read once the sign-in lapsed, and no other identity was tried");

        var auth = await host.Db.BulkRequestAuthorizations.AsNoTracking().SingleAsync();
        (auth.Status, auth.LapseReason, auth.Payload, auth.WrappedDataKey).Should().Be((BulkAuthorizationStatus.Lapsed, "session_revoked", null, null));
    }

    [Fact]
    public async Task The_request_finishes_and_the_recruiter_is_notified_once_the_last_scoring_file_settles_after_a_lapse()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        using var scope = host.Provider.CreateScope();
        var cycle = scope.ServiceProvider.GetRequiredService<BulkScoringCycle>();
        foreach (var item in await cycle.ClaimAsync("t1", default)) await cycle.RunItemAsync(item, default);
        host.Provider.GetRequiredService<MockAuthorizationControl>().Lapse(requestId, "conditional_access");
        foreach (var item in await cycle.ClaimAsync("t1", default)) await cycle.RunItemAsync(item, default);
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();
        (await repository.ListUnacknowledgedAsync("recruiter@contoso.com")).Should().BeEmpty("the request is not finished while a file is still in scoring");

        await host.Db.Applications.ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Completed"));
        await cycle.SweepAsync(default);

        var done = await host.RequestAsync(requestId);
        done.Status.Should().Be(BulkRequestStatus.Completed);
        done.Outcome.Should().Be(BulkRequestOutcome.Partial);
        (await repository.ListUnacknowledgedAsync("recruiter@contoso.com")).Should().ContainSingle(r => r.Id == requestId);
        (await repository.GetRetryableItemsAsync(requestId)).Should().HaveCount(9, "every lapsed file can be retried after signing in again");
        (await host.Db.BulkRequestAuthorizations.AsNoTracking().SingleAsync()).Status.Should().Be(BulkAuthorizationStatus.Lapsed, "a lapse stays visible");
    }

    [Fact]
    public async Task Several_files_discovering_the_lapse_together_record_it_once()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        host.Provider.GetRequiredService<MockAuthorizationControl>().Lapse(requestId, "interaction_required");
        using var scope = host.Provider.CreateScope();
        var cycle = scope.ServiceProvider.GetRequiredService<BulkScoringCycle>();
        var claimed = await cycle.ClaimAsync("t1", default);
        claimed.Should().HaveCountGreaterThan(1);

        await Task.WhenAll(claimed.Select(i => cycle.RunItemAsync(i, default)));

        (await host.Db.ProcessingEvents.AsNoTracking().CountAsync(e => e.EventType == "bulk-scoring-authorization.lapsed" && e.EntityId == requestId)).Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_request_has_its_authorization_discarded_by_the_next_sweep()
    {
        await using var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        using var scope = host.Provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();
        var request = (await repository.GetAsync(requestId))!;

        await repository.RequestCancelAsync(requestId, request.ConcurrencyVersion, "recruiter@contoso.com", DateTime.UtcNow);
        (await host.RequestAsync(requestId)).Status.Should().Be(BulkRequestStatus.Completed, "nothing was in flight");
        await scope.ServiceProvider.GetRequiredService<BulkScoringCycle>().SweepAsync(default);

        var auth = await host.Db.BulkRequestAuthorizations.AsNoTracking().SingleAsync();
        (auth.Status, auth.Payload, auth.WrappedDataKey).Should().Be((BulkAuthorizationStatus.Discarded, null, null));
        (await host.Db.ProcessingEvents.AsNoTracking().CountAsync(e => e.EventType == "bulk-scoring-authorization.discarded")).Should().Be(1);
    }

    [Fact]
    public async Task A_preview_that_expires_never_held_an_authorization()
    {
        await using var host = await WorkerHost.CreateAsync();
        using var scope = host.Provider.CreateScope();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        await host.Db.BulkRequestAuthorizations.ExecuteDeleteAsync();
        await host.Db.BulkScoringRequests.Where(r => r.Id == requestId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, BulkRequestStatus.Draft).SetProperty(r => r.Outcome, (BulkRequestOutcome?)null)
                .SetProperty(r => r.ExpiresAt, (DateTime?)DateTime.UtcNow.AddMinutes(-1)));

        var purged = await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>().PurgeExpiredDraftsAsync(DateTime.UtcNow);

        purged.Should().Contain(requestId);
        (await host.Db.BulkRequestAuthorizations.CountAsync()).Should().Be(0);
    }

    private sealed class ProcessorScenario : IAsyncDisposable
    {
        public static readonly byte[] OriginalDocx = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
        public static readonly byte[] SourcePdf = "%PDF-source-owned"u8.ToArray();
        public static readonly byte[] RasterPdf = "%PDF-raster-owned"u8.ToArray();

        public Fixture Fixture { get; }
        public BulkScoringRequest Request { get; }
        public BulkScoringItem Item { get; }
        public BoundarySource Source { get; }
        public RecordingContentStore Content { get; }
        public RecordingRasterizer Rasterizer { get; }
        public BulkItemProcessor Processor { get; }

        private ProcessorScenario(
            Fixture fixture,
            BulkScoringRequest request,
            BulkScoringItem item,
            BoundarySource source,
            RecordingContentStore content,
            RecordingRasterizer rasterizer,
            BulkItemProcessor processor)
        {
            Fixture = fixture;
            Request = request;
            Item = item;
            Source = source;
            Content = content;
            Rasterizer = rasterizer;
            Processor = processor;
        }

        public static async Task<ProcessorScenario> CreateAsync(BulkItemStatus phase)
        {
            var fixture = await Fixture.CreateAsync(migrate: false);
            var options = BulkScoringOptions.Create(key => key == "MICROSOFT365_SOURCE_MODE" ? "mock" : null, false, false);
            var limits = BulkRequestLimits.Snapshot(options, 8_388_608, 30_000);
            var request = fixture.NewRequest();
            request.LimitsJson = limits.ToJson();
            request.MaxFilesPerRequest = limits.MaxFilesPerRequest;
            request.MaxBytesPerFile = limits.MaxBytesPerFile;
            request.MaxTotalBytes = limits.MaxTotalBytes;
            request.DiscoveryStepMaxFiles = limits.DiscoveryStepFiles;
            request.DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries;
            request.DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds;
            request.RasterDpi = limits.RasterDpi;
            var discovered = new BulkScoringItem
            {
                SourceDriveId = "drive",
                SourceDriveItemId = "item",
                SourceETag = "etag",
                FileName = "candidate.docx",
                SourcePath = "/",
                MimeType = Docx,
                SizeBytes = OriginalDocx.LongLength,
            };
            var draft = await fixture.Repo.CreateDraftAsync(request, Step([discovered], status: BulkDiscoveryStatus.Complete));
            var active = await fixture.Repo.ConfirmAsync(
                draft.Id,
                draft.ConcurrencyVersion,
                convertToRasterPdf: true,
                allowDuplicates: true,
                scoringConfigVersionId: "cfg",
                authorization: null,
                utcNow: Now);
            var item = (await fixture.Repo.GetItemsAsync(active.Id, new BulkItemQuery(1, 1))).Items.Single();
            item.TransitionTo(BulkItemStatus.Importing, Now);
            await fixture.Repo.SaveItemAsync(item);

            var store = new RecordingContentStore(new BulkContentStore(new InlineBlobStore()));
            if (phase == BulkItemStatus.Converting)
            {
                await fixture.Repo.AddContentAsync(await store.StageAsync(
                    active.Id, item.Id, BulkContentKind.Original, item.FileName, item.MimeType, OriginalDocx));
                await fixture.Repo.AddContentAsync(await store.StageAsync(
                    active.Id, item.Id, BulkContentKind.Converted, item.FileName, "application/pdf", SourcePdf));
                item.Fingerprint = ContentFingerprint.Raw(OriginalDocx);
                item.SourceStoredAt = Now;
                item.TransitionTo(BulkItemStatus.Converting, Now);
                await fixture.Repo.SaveItemAsync(item);
            }

            var source = new BoundarySource(() => item.SourceStoredAt is not null);
            var rasterizer = new RecordingRasterizer();
            var processor = new BulkItemProcessor(
                fixture.Repo,
                new ApplicationRepository(fixture.Db),
                source,
                store,
                rasterizer,
                options,
                new FixedClock(Now),
                NullLogger<BulkItemProcessor>.Instance);
            return new ProcessorScenario(fixture, active, item, source, store, rasterizer, processor);
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private sealed class BoundarySource(Func<bool> boundaryReached) : ISharePointSource
    {
        public bool LapseImmediately { get; set; }
        public List<string> Calls { get; } = [];
        public List<string> CallsAfterBoundary { get; } = [];
        public List<SourceCallContext> Contexts { get; } = [];

        public bool CanConvertToPdf(string extension) => extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

        public Task<SourceItemMetadata?> GetItemMetadataAsync(
            string driveId,
            string itemId,
            SourceCallContext context,
            CancellationToken cancellationToken = default)
        {
            Record("metadata", context);
            return Task.FromResult<SourceItemMetadata?>(new("etag", ProcessorScenario.OriginalDocx.LongLength));
        }

        public Task<byte[]> DownloadAsync(
            string driveId,
            string itemId,
            long maxBytes,
            SourceCallContext context,
            CancellationToken cancellationToken = default)
        {
            Record("download", context);
            return Task.FromResult(ProcessorScenario.OriginalDocx.ToArray());
        }

        public Task<byte[]> ConvertToPdfAsync(
            string driveId,
            string itemId,
            long maxBytes,
            SourceCallContext context,
            CancellationToken cancellationToken = default)
        {
            Record("convert", context);
            return Task.FromResult(ProcessorScenario.SourcePdf.ToArray());
        }

        private void Record(string operation, SourceCallContext context)
        {
            Calls.Add(operation);
            Contexts.Add(context);
            if (boundaryReached())
            {
                CallsAfterBoundary.Add(operation);
                throw new SourceAuthorizationLapsedException("interaction_required");
            }
            if (LapseImmediately)
                throw new SourceAuthorizationLapsedException("interaction_required");
        }

        public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRasterizer : IDocumentRasterizer
    {
        public List<byte[]> Inputs { get; } = [];

        public Task<RasterizationResult> RasterizeAsync(
            byte[] source,
            string mimeType,
            RasterizationOptions options,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(source.ToArray());
            return Task.FromResult(new RasterizationResult(ProcessorScenario.RasterPdf.ToArray(), 1));
        }
    }

    private sealed class RecordingContentStore(IBulkContentStore inner) : IBulkContentStore
    {
        public List<BulkContentKind> ReadKinds { get; } = [];

        public Task<BulkScoringItemContent> StageAsync(
            string requestId,
            string itemId,
            BulkContentKind kind,
            string fileName,
            string mimeType,
            byte[] bytes,
            CancellationToken cancellationToken = default) =>
            inner.StageAsync(requestId, itemId, kind, fileName, mimeType, bytes, cancellationToken);

        public async Task<byte[]> ReadAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default)
        {
            ReadKinds.Add(content.Kind);
            return await inner.ReadAsync(content, cancellationToken);
        }

        public Task DeleteAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(content, cancellationToken);

        public Task DeleteByRequestAsync(string requestId, CancellationToken cancellationToken = default) =>
            inner.DeleteByRequestAsync(requestId, cancellationToken);
    }
}
