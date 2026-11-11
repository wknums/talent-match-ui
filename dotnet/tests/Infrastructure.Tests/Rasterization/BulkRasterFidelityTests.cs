using System.Diagnostics;
using System.Text.Json;
using Docnet.Core;
using Docnet.Core.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.Rasterization;
using TalentMatch.Infrastructure.Services;
using TalentMatch.Infrastructure.SharePoint;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringRepositoryTests;

namespace TalentMatch.Infrastructure.Tests.Rasterization;

public sealed class BulkRasterFidelityTests
{
    private const string Pdf = "application/pdf";
    private static readonly RasterizationOptions RasterOptions = new(150, 80, 30, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Production_output_is_a_configured_structurally_valid_pdf_and_passes_every_pinned_oracle_page()
    {
        var sourcePath = RasterFixture("multi-page-source.pdf");
        var source = await File.ReadAllBytesAsync(sourcePath);
        using var rasterizer = new PdfiumRasterizer(new BulkScoringOptions { ConvertConcurrency = 2 });

        var result = await rasterizer.RasterizeAsync(source, Pdf, RasterOptions);

        result.MediaType.Should().Be(Pdf);
        result.PdfBytes.Should().StartWith("%PDF-"u8.ToArray());
        System.Text.Encoding.ASCII.GetString(result.PdfBytes).TrimEnd().Should().EndWith("%%EOF");
        result.SizeBytes.Should().Be(result.PdfBytes.LongLength).And.BePositive();
        result.SizeBytes.Should().BeLessThanOrEqualTo(8_388_608);
        result.PageCount.Should().Be(3);
        result.OriginalPageCount.Should().Be(3);
        result.RasterDpiUsed.Should().Be(150);
        result.MinimumPageSsim.Should().BeGreaterThanOrEqualTo(0.98);
        result.PageSsim.Should().HaveCount(3).And.OnlyContain(score => score >= 0.98);
        result.FidelityValidatedAt.Should().NotBeNull();

        ReadDimensions(result.PdfBytes).Should().HaveCount(3)
            .And.ContainInOrder((612d, 792d), (792d, 612d), (500d, 700d));

        AssertPinnedOracleContract();
        using var oracle = await TryRunPinnedOracleAsync(sourcePath, result.PdfBytes);
        if (oracle is not null)
        {
            oracle.RootElement.GetProperty("verdict").GetBoolean().Should().BeTrue();
            foreach (var page in oracle.RootElement.GetProperty("pages").EnumerateArray())
            {
                page.GetProperty("dpi").GetInt32().Should().Be(150);
                page.GetProperty("ssim").GetDouble().Should().BeGreaterThanOrEqualTo(0.98);
                page.GetProperty("blankVerdict").GetBoolean().Should().BeTrue();
                page.GetProperty("cropVerdict").GetBoolean().Should().BeTrue();
            }
        }
    }

    [Theory]
    [InlineData("multi-page-reordered.pdf", BulkReasonCode.ConversionDimensionMismatch)]
    [InlineData("multi-page-blank.pdf", BulkReasonCode.ConversionBlankPage)]
    [InlineData("multi-page-cropped.pdf", BulkReasonCode.ConversionCroppedPage)]
    public async Task Admission_rejects_order_blank_and_crop_as_independent_predicates(string outputName, BulkReasonCode reason)
    {
        var source = await File.ReadAllBytesAsync(RasterFixture("multi-page-source.pdf"));
        var output = await File.ReadAllBytesAsync(RasterFixture(outputName));

        var act = () => RasterFidelityAdmission.ValidateAsync(
            source, output, Pdf, RasterOptions, 8_388_608, TimeProvider.System);

        (await act.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(reason);
    }

    [Fact]
    public async Task Admission_checks_type_magic_structure_positive_pages_size_and_exact_count_before_handoff()
    {
        var source = await File.ReadAllBytesAsync(RasterFixture("one-page-source.pdf"));
        var valid = await File.ReadAllBytesAsync(RasterFixture("one-page-identical.pdf"));

        var wrongType = () => RasterFidelityAdmission.ValidateAsync(source, valid, "image/png", RasterOptions, valid.Length, TimeProvider.System);
        (await wrongType.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConvertedTypeInvalid);

        var wrongMagic = () => RasterFidelityAdmission.ValidateAsync(source, "not a pdf"u8.ToArray(), Pdf, RasterOptions, valid.Length, TimeProvider.System);
        (await wrongMagic.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConvertedMagicInvalid);

        var corrupt = "%PDF-1.7\ntruncated\n%%EOF"u8.ToArray();
        var wrongStructure = () => RasterFidelityAdmission.ValidateAsync(source, corrupt, Pdf, RasterOptions, valid.Length, TimeProvider.System);
        (await wrongStructure.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConvertedDocumentInvalid);

        var tooLarge = () => RasterFidelityAdmission.ValidateAsync(source, valid, Pdf, RasterOptions, valid.Length - 1L, TimeProvider.System);
        (await tooLarge.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConvertedTooLarge);

        var pageMismatch = await File.ReadAllBytesAsync(RasterFixture("multi-page-identical.pdf"));
        var wrongCount = () => RasterFidelityAdmission.ValidateAsync(source, pageMismatch, Pdf, RasterOptions, pageMismatch.Length, TimeProvider.System);
        (await wrongCount.Should().ThrowAsync<BulkConversionException>()).Which.Reason.Should().Be(BulkReasonCode.ConversionPageMismatch);

        var admitted = await RasterFidelityAdmission.ValidateAsync(source, valid, Pdf, RasterOptions, valid.Length, TimeProvider.System);
        admitted.SizeBytes.Should().Be(valid.Length, "the configured boundary is inclusive");
        admitted.PageCount.Should().BePositive();
    }

    [Fact]
    public async Task Rejected_output_is_removed_without_original_fallback_application_or_scoring_handoff()
    {
        var source = await File.ReadAllBytesAsync(RasterFixture("one-page-source.pdf"));
        await using var fixture = await Fixture.CreateAsync(migrate: false);
        var options = new BulkScoringOptions();
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
            SourceDriveId = "drive",
            SourceDriveItemId = "item",
            SourceETag = "etag",
            FileName = "candidate.pdf",
            SourcePath = "/",
            MimeType = Pdf,
            SizeBytes = source.LongLength,
        };
        var draft = await fixture.Repo.CreateDraftAsync(request, Step([item], status: BulkDiscoveryStatus.Complete));
        var active = await fixture.Repo.ConfirmAsync(
            draft.Id, draft.ConcurrencyVersion, true, true, "cfg-1", null, Now);
        BulkScoringAccess.FrozenLimits(active).MaxBytesPerFile.Should().Be(8_388_608);
        var claimed = (await fixture.Repo.GetItemsAsync(active.Id, new BulkItemQuery(1, 10))).Items.Single();
        claimed.TransitionTo(BulkItemStatus.Importing, Now);
        await fixture.Repo.SaveItemAsync(claimed);
        var signal = new CountingSignal();
        var processor = new BulkItemProcessor(
            fixture.Repo,
            new ApplicationRepository(fixture.Db),
            new ExactSource(source),
            new BulkContentStore(new InlineBlobStore()),
            new RejectedRasterizer(),
            options,
            new FixedClock(Now),
            NullLogger<BulkItemProcessor>.Instance,
            signal);

        var outcome = await processor.ProcessAsync(claimed, active, default);

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.BulkScoringItems.AsNoTracking().SingleAsync(x => x.Id == claimed.Id);
        outcome.Should().Be(BulkItemOutcome.Failed);
        saved.ReasonCode.Should().Be(BulkReasonCode.ConversionBlankPage);
        saved.ScoredVersion.Should().BeNull("the original must never be a conversion fallback");
        saved.ApplicationId.Should().BeNull();
        signal.Pulses.Should().Be(0);
        (await fixture.Db.Applications.CountAsync()).Should().Be(0);
        (await fixture.Db.BulkScoringItemContents.AsNoTracking()
            .CountAsync(x => x.ItemId == claimed.Id && x.Kind == BulkContentKind.Converted)).Should().Be(0);
        var events = await fixture.Db.BulkAuditEvents.AsNoTracking()
            .Where(x => x.SubjectId == claimed.Id)
            .Select(x => x.EventType)
            .ToListAsync();
        events.Should().Contain(BulkAuditEventTypes.BulkItemConversionFailed);
        events.Should().Contain(BulkAuditEventTypes.BulkConvertedOutputQuarantined);
        events.Should().Contain(BulkAuditEventTypes.BulkConvertedOutputRemoved);
        events.Should().Contain(BulkAuditEventTypes.BulkItemFailed);
        events.Should().NotContain(BulkAuditEventTypes.BulkItemApplicationCreated);
        events.Should().NotContain(BulkAuditEventTypes.BulkItemScoringHandoffQueued);
    }

    private static IReadOnlyList<(double Width, double Height)> ReadDimensions(byte[] pdf)
    {
        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1));
        return Enumerable.Range(0, reader.GetPageCount())
            .Select(index =>
            {
                using var page = reader.GetPageReader(index);
                return ((double)page.GetPageWidth(), (double)page.GetPageHeight());
            })
            .ToArray();
    }

    private static void AssertPinnedOracleContract()
    {
        var root = RepositoryRoot();
        var requirements = File.ReadAllText(Path.Combine(root, "tests", "tools", "requirements-raster-fidelity.txt"));
        requirements.Should().Contain("scikit-image==0.24.0")
            .And.Contain("pypdf==5.1.0")
            .And.Contain("Pillow==11.1.0")
            .And.Contain("pdftoppm 25.07.0");
        var oracle = File.ReadAllText(Path.Combine(root, "tests", "tools", "raster_fidelity_oracle.py"));
        oracle.Should().Contain("EXPECTED_POPPLER_VERSION = \"25.07.0\"")
            .And.Contain("MIN_SSIM = 0.98")
            .And.Contain("MIN_INK_RATIO = 0.0005")
            .And.Contain("MIN_BOUNDING_BOX_AREA_RATIO = 0.90")
            .And.Contain("gaussian_weights=True")
            .And.Contain("use_sample_covariance=False");
    }

    private static async Task<JsonDocument?> TryRunPinnedOracleAsync(string sourcePath, byte[] output)
    {
        var root = RepositoryRoot();
        var work = Path.Combine(AppContext.BaseDirectory, "raster-oracle-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var outputPath = Path.Combine(work, "output.pdf");
        await File.WriteAllBytesAsync(outputPath, output);
        try
        {
            var python = Path.Combine(root, ".venv", "Scripts", "python.exe");
            if (!File.Exists(python)) python = OperatingSystem.IsWindows() ? "python" : "python3";
            var start = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["TMP"] = work;
            start.Environment["TEMP"] = work;
            start.ArgumentList.Add(Path.Combine(root, "tests", "tools", "raster_fidelity_oracle.py"));
            start.ArgumentList.Add(sourcePath);
            start.ArgumentList.Add(outputPath);
            start.ArgumentList.Add("--dpi");
            start.ArgumentList.Add("150");
            using var process = Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return JsonDocument.Parse(stdout);
            if (stderr.Contains("ModuleNotFoundError", StringComparison.Ordinal)
                || stderr.Contains("pdftoppm 25.07.0 is required", StringComparison.Ordinal))
                return null;
            process.ExitCode.Should().Be(0, stderr);
            return null;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static string RasterFixture(string name) =>
        Path.Combine(RepositoryRoot(), "tests", "fixtures", "bulk-scoring-requests", "raster", name);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "bulk-scoring-requests")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RejectedRasterizer : IDocumentRasterizer
    {
        public Task<RasterizationResult> RasterizeAsync(
            byte[] source,
            string mimeType,
            RasterizationOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RasterizationResult.Rejected(
                awaitableBytes: File.ReadAllBytes(RasterFixture("multi-page-blank.pdf")),
                BulkReasonCode.ConversionBlankPage));
    }

    private sealed class ExactSource(byte[] bytes) : ISharePointSource
    {
        public bool CanConvertToPdf(string extension) => true;
        public Task<SourceItemMetadata?> GetItemMetadataAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceItemMetadata?>(new("etag", bytes.LongLength));
        public Task<byte[]> DownloadAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.ToArray());
        public Task<byte[]> ConvertToPdfAsync(string driveId, string itemId, long maxBytes, SourceCallContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(bytes.ToArray());
        public Task<SourceFolder> ResolveFolderAsync(string folderUrl, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SourcePage> ReadNextPageAsync(SourceFolder root, string? cursor, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetAncestorItemIdsAsync(string driveId, string itemId, SourceCallContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingSignal : IScoringQueueSignal
    {
        public int Pulses { get; private set; }
        public void Pulse() => Pulses++;
        public Task WaitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
