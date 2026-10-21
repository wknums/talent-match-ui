using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Services;
using TalentMatch.Infrastructure.SharePoint;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkContentVersionTests
{
    private static readonly byte[] OriginalPdf = "%PDF-exact-original"u8.ToArray();
    private static readonly byte[] ConvertedPdf = "%PDF-exact-raster"u8.ToArray();

    [Fact]
    public async Task Converted_item_retains_both_exact_versions_and_uses_the_converted_version_by_default()
    {
        var rasterizer = new ExactRasterizer(ConvertedPdf, pageCount: 2);

        var result = await ProcessAsync("candidate.pdf", "application/pdf", OriginalPdf, rasterizer);

        result.Outcome.Should().Be(BulkItemOutcome.HandedOff);
        result.Item.ScoredVersion.Should().Be(BulkScoredVersion.Converted);
        result.Retained.Keys.Should().BeEquivalentTo([BulkContentKind.Original, BulkContentKind.Converted]);
        result.Retained[BulkContentKind.Original].Should().Equal(OriginalPdf);
        result.Retained[BulkContentKind.Converted].Should().Equal(ConvertedPdf);
        Convert.FromBase64String(result.ScoredDocument!.ContentBase64!).Should().Equal(ConvertedPdf);
        result.ScoredDocument.FileName.Should().Be("candidate.pdf");
        result.ScoredDocument.FileType.Should().Be("application/pdf");
        result.Item.OriginalPageCount.Should().Be(2);
        result.Item.ConvertedPageCount.Should().Be(2);
    }

    [Fact]
    public async Task Txt_bypasses_conversion_and_scores_only_its_exact_original_content()
    {
        var text = "exact plain text résumé"u8.ToArray();
        var rasterizer = new ExactRasterizer(ConvertedPdf, pageCount: 99);

        var result = await ProcessAsync("candidate.txt", "text/plain", text, rasterizer);

        result.Outcome.Should().Be(BulkItemOutcome.HandedOff);
        rasterizer.Calls.Should().Be(0);
        result.Item.ScoredVersion.Should().Be(BulkScoredVersion.Original);
        result.Retained.Should().BeEmpty("TXT is not a converted item and needs no second retained version");
        Convert.FromBase64String(result.ScoredDocument!.ContentBase64!).Should().Equal(text);
        result.ScoredDocument.FileName.Should().Be("candidate.txt");
        result.ScoredDocument.FileType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Failed_conversion_never_falls_back_to_the_original_for_application_or_scoring()
    {
        var rasterizer = new ExactRasterizer(ConvertedPdf, pageCount: 2)
        {
            Failure = new BulkConversionException(BulkReasonCode.ConversionFailed, "synthetic conversion failure"),
        };

        var result = await ProcessAsync("candidate.pdf", "application/pdf", OriginalPdf, rasterizer);

        result.Outcome.Should().Be(BulkItemOutcome.Failed);
        result.Item.Status.Should().Be(BulkItemStatus.Failed);
        result.Item.ReasonCode.Should().Be(BulkReasonCode.ConversionFailed);
        result.Item.ScoredVersion.Should().BeNull();
        result.Item.ApplicationId.Should().BeNull();
        result.ScoredDocument.Should().BeNull();
        result.ApplicationCount.Should().Be(0);
        result.Retained.Should().ContainKey(BulkContentKind.Original)
            .WhoseValue.Should().Equal(OriginalPdf);
        result.Retained.Should().NotContainKey(BulkContentKind.Converted);
    }

    private static async Task<ScenarioResult> ProcessAsync(
        string fileName,
        string mimeType,
        byte[] original,
        ExactRasterizer rasterizer)
    {
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var options = BulkScoringOptions.Create(key => key == "MICROSOFT365_SOURCE_MODE" ? "mock" : null, false, false);
        var request = fixture.NewRequest();
        var limits = BulkRequestLimits.Snapshot(options, 8_388_608, 30_000);
        request.LimitsJson = limits.ToJson();
        request.MaxFilesPerRequest = limits.MaxFilesPerRequest;
        request.MaxBytesPerFile = limits.MaxBytesPerFile;
        request.MaxTotalBytes = limits.MaxTotalBytes;
        request.DiscoveryStepMaxFiles = limits.DiscoveryStepFiles;
        request.DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries;
        request.DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds;
        request.RasterDpi = limits.RasterDpi;
        var item = new BulkScoringItem
        {
            Ordinal = 0,
            SourceDriveId = "drive",
            SourceDriveItemId = "item",
            SourceETag = "etag",
            FileName = fileName,
            SourcePath = "/",
            MimeType = mimeType,
            SizeBytes = original.LongLength,
        };
        var draft = await fixture.Repo.CreateDraftAsync(
            request,
            Step([item], status: BulkDiscoveryStatus.Complete));
        var active = await fixture.Repo.ConfirmAsync(
            draft.Id,
            draft.ConcurrencyVersion,
            convertToRasterPdf: true,
            allowDuplicates: true,
            scoringConfigVersionId: "cfg-1",
            authorization: null,
            utcNow: Now);
        active.LimitsJson = limits.ToJson();
        var claimed = (await fixture.Repo.GetItemsAsync(
            active.Id,
            new BulkItemQuery(1, 10))).Items.Single();
        claimed.TransitionTo(BulkItemStatus.Importing, Now);
        await fixture.Repo.SaveItemAsync(claimed);

        var applications = new ApplicationRepository(fixture.Db);
        var contentStore = new BulkContentStore(new InlineBlobStore());
        var processor = new BulkItemProcessor(
            fixture.Repo,
            applications,
            new ExactSource(original),
            contentStore,
            rasterizer,
            options,
            new FixedClock(Now),
            NullLogger<BulkItemProcessor>.Instance);

        var outcome = await processor.ProcessAsync(claimed, active, default);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == claimed.Id);
        var rows = await fixture.Db.BulkScoringItemContents.AsNoTracking()
            .Where(c => c.ItemId == claimed.Id)
            .ToListAsync();
        var retained = rows.ToDictionary(
            c => c.Kind,
            c => c.ContentBase64 is null ? Array.Empty<byte>() : Convert.FromBase64String(c.ContentBase64));
        var document = saved.ApplicationId is null
            ? null
            : await fixture.Db.ApplicationDocuments.AsNoTracking()
                .SingleAsync(d => d.ApplicationId == saved.ApplicationId);
        if (document is not null)
        {
            document.ContentBase64 = (await fixture.Db.DocumentBlobs.AsNoTracking()
                .SingleAsync(b => b.DocumentId == document.Id)).Content;
        }
        return new(
            outcome,
            saved,
            retained,
            document,
            await fixture.Db.Applications.CountAsync());
    }

    private sealed record ScenarioResult(
        BulkItemOutcome Outcome,
        BulkScoringItem Item,
        IReadOnlyDictionary<BulkContentKind, byte[]> Retained,
        ApplicationDocument? ScoredDocument,
        int ApplicationCount);

    private sealed class ExactRasterizer(byte[] output, int pageCount) : IDocumentRasterizer
    {
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }

        public Task<RasterizationResult> RasterizeAsync(
            byte[] source,
            string mimeType,
            RasterizationOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new RasterizationResult(output.ToArray(), pageCount));
        }
    }

    private sealed class ExactSource(byte[] bytes) : ISharePointSource
    {
        public bool CanConvertToPdf(string extension) => true;

        public Task<SourceItemMetadata?> GetItemMetadataAsync(
            string driveId,
            string itemId,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceItemMetadata?>(new("etag", bytes.LongLength));

        public Task<byte[]> DownloadAsync(
            string driveId,
            string itemId,
            long maxBytes,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.ToArray());

        public Task<byte[]> ConvertToPdfAsync(
            string driveId,
            string itemId,
            long maxBytes,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.ToArray());

        public Task<SourceFolder> ResolveFolderAsync(
            string folderUrl,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SourcePage> ReadNextPageAsync(
            SourceFolder root,
            string? cursor,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(
            string driveId,
            string itemId,
            SourceCallContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
