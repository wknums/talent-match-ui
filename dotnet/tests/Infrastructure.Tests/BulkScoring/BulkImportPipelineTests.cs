using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using TalentMatch.Infrastructure.SharePoint;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

public sealed class BulkImportPipelineTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";
    private static readonly DateTime Start = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Without_rasterization_files_become_one_queued_application_each_with_the_original_bytes()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);

        var results = await h.ProcessAllAsync(request.Id);

        h.Logger.Errors.Should().BeEmpty("nothing should have needed a retry");
        results.Values.Should().OnlyContain(o => o == BulkItemOutcome.HandedOff);
        results.Should().HaveCount(11);
        var items = await h.ItemsAsync(request.Id);
        items.Should().OnlyContain(i => i.Status == BulkItemStatus.Scoring && i.ScoredVersion == BulkScoredVersion.Original && i.HandedOffAt != null);

        (await h.Db.Applications.CountAsync()).Should().Be(11);
        (await h.Db.ApplicationDocuments.CountAsync()).Should().Be(11, "exactly one document row per application");
        (await h.Db.Applications.AsNoTracking().Select(a => a.Status).Distinct().ToListAsync()).Should().Equal("Queued");
        h.Signal.Pulses.Should().Be(11);

        var pdf = items.Single(i => i.FileName == "bob-2page.pdf");
        pdf.ApplicationId.Should().Be(DeterministicIds.Application(pdf.Id));
        var doc = await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == pdf.ApplicationId);
        doc.Id.Should().Be(DeterministicIds.Document(pdf.Id));
        doc.Fingerprint.Should().Be(ContentFingerprint.Raw(h.Fixture("pdf-2page.pdf")));
        doc.FileType.Should().Be("application/pdf");
        (await h.Db.DocumentBlobs.AsNoTracking().SingleAsync(b => b.DocumentId == doc.Id)).Content.Should()
            .Be(Convert.ToBase64String(h.Fixture("pdf-2page.pdf")));
        (await h.Db.Applications.AsNoTracking().SingleAsync(a => a.Id == pdf.ApplicationId)).CandidateName.Should().Be("bob 2page");

        (await h.Db.BulkScoringItemContents.CountAsync()).Should().Be(0, "no converted rows exist and staged originals were removed (SC-011)");
        h.Raster.Calls.Should().BeEmpty();
        (await h.Repo.GetAsync(request.Id))!.ScoringCount.Should().Be(11);
    }

    [Fact]
    public async Task Duplicates_within_the_request_are_skipped_unless_allowed()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: false);

        var results = await h.ProcessAllAsync(request.Id);

        results.Values.Count(o => o == BulkItemOutcome.Skipped).Should().Be(2, "bob-copy and the nested alice are byte-identical");
        var skipped = (await h.ItemsAsync(request.Id)).Where(i => i.Status == BulkItemStatus.Skipped).ToList();
        skipped.Should().OnlyContain(i => i.ReasonCode == BulkReasonCode.DuplicateInRequest);
        (await h.Db.Applications.CountAsync()).Should().Be(9);
        (await h.Db.BulkScoringItemContents.CountAsync()).Should().Be(0, "a skipped duplicate keeps no staged bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_already_imported_for_the_job_is_skipped_including_pre_change_base64_fingerprints(bool legacyHash)
    {
        await using var h = await Harness.CreateAsync();
        var existing = new TalentMatch.Domain.Entities.Application { JobId = "job-1", CandidateRef = "c", Status = "Completed" };
        await h.Apps.AddAsync(existing);
        var bytes = h.Fixture("pdf-1page.pdf");
        await h.Apps.AddDocumentAsync(new ApplicationDocument
        {
            ApplicationId = existing.Id,
            FileName = "old.pdf",
            FileType = "application/pdf",
            FileSize = bytes.Length,
            Fingerprint = legacyHash ? ContentFingerprint.LegacyBase64(bytes) : ContentFingerprint.Raw(bytes),
            ContentBase64 = Convert.ToBase64String(bytes),
        });
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: false);

        var results = await h.ProcessAllAsync(request.Id);

        var items = await h.ItemsAsync(request.Id);
        items.Where(i => i.ReasonCode == BulkReasonCode.DuplicateInJob).Select(i => i.FileName).Should().Contain("alice-1page.pdf");
        results.Values.Should().Contain(BulkItemOutcome.Skipped);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Every_selected_prompt_test_file_is_imported_for_production_and_converted_when_requested(bool legacyHash, bool rasterize)
    {
        await using var h = await Harness.CreateAsync();
        var files = new[]
        {
            (Name: "frank.pdf", Type: "application/pdf", Bytes: h.Fixture("pdf-1page.pdf")),
            (Name: "gina.docx", Type: "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Bytes: h.Fixture("cv.docx")),
            (Name: "hugo.txt", Type: "text/plain", Bytes: h.Fixture("cv.txt")),
        };
        foreach (var file in files)
        {
            var testApplication = new TalentMatch.Domain.Entities.Application
            {
                JobId = "job-1",
                TestRunId = "test-run-1",
                Status = "Completed",
                FinalScore = 42,
            };
            await h.Apps.AddAsync(testApplication);
            await h.Apps.AddDocumentAsync(new ApplicationDocument
            {
                ApplicationId = testApplication.Id,
                FileName = file.Name,
                FileType = file.Type,
                FileSize = file.Bytes.Length,
                Fingerprint = legacyHash ? ContentFingerprint.LegacyBase64(file.Bytes) : ContentFingerprint.Raw(file.Bytes),
                ContentBase64 = Convert.ToBase64String(file.Bytes),
            });
        }
        var request = await h.ActiveRequestAsync(
            "https://tenant-a-my.sharepoint.com/personal/alex_tenant_a/Documents/Candidates",
            rasterize,
            allowDuplicates: false);

        var outcomes = await h.ProcessAllAsync(request.Id);

        outcomes.Should().HaveCount(files.Length);
        outcomes.Values.Should().OnlyContain(outcome => outcome == BulkItemOutcome.HandedOff);
        var items = await h.ItemsAsync(request.Id);
        items.Select(item => item.FileName).Should().BeEquivalentTo(files.Select(file => file.Name));
        foreach (var item in items)
        {
            var original = files.Single(file => file.Name == item.FileName);
            var converted = rasterize && original.Type != "text/plain";
            item.ScoredVersion.Should().Be(converted ? BulkScoredVersion.Converted : BulkScoredVersion.Original);
            var document = await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(document => document.ApplicationId == item.ApplicationId);
            document.Fingerprint.Should().Be(ContentFingerprint.Raw(original.Bytes));
            document.FileType.Should().Be(converted ? "application/pdf" : original.Type);
            var blob = await h.Db.DocumentBlobs.AsNoTracking().SingleAsync(blob => blob.DocumentId == document.Id);
            var scoredBytes = Convert.FromBase64String(blob.Content);
            if (converted)
                scoredBytes.Should().NotEqual(original.Bytes, "production must score the converted document, not reuse the prompt-test original");
            else
                scoredBytes.Should().Equal(original.Bytes);

            var run = new ScoringRun { ApplicationId = item.ApplicationId!, RunIndex = 1, TotalScore = 75 };
            await h.Apps.AddScoringRunAsync(run);
            await new ApplicationScoringFinalizer(h.Apps).FinalizeAsync(item.ApplicationId!, "job-1", [run], 1, 12, 65);
        }

        var production = await h.Apps.GetByJobIdAsync("job-1", null, 65, 80);
        production.Select(application => application.Id).Should().BeEquivalentTo(items.Select(item => item.ApplicationId));
        production.Should().OnlyContain(application =>
            application.TestRunId == null && application.Status == "Completed" && application.FinalScore == 75);
        var counts = await h.Apps.GetCountsByJobIdAsync("job-1", 65, 80);
        counts.Total.Should().Be(files.Length);
        counts.Complete.Should().Be(files.Length);
        h.Raster.Calls.Should().HaveCount(rasterize ? 2 : 0);
        var testApplications = await h.Db.Applications.AsNoTracking().Where(application => application.TestRunId != null).ToListAsync();
        testApplications.Should().HaveCount(files.Length).And.OnlyContain(application => application.FinalScore == 42);
        foreach (var application in testApplications)
        {
            var document = await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(document => document.ApplicationId == application.Id);
            var original = files.Single(file => file.Name == document.FileName);
            document.FileType.Should().Be(original.Type);
            var blob = await h.Db.DocumentBlobs.AsNoTracking().SingleAsync(blob => blob.DocumentId == document.Id);
            Convert.FromBase64String(blob.Content).Should().Equal(original.Bytes);
        }
    }

    [Fact]
    public async Task A_changed_deleted_or_revoked_file_fails_with_its_own_reason()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var claimed = await h.ClaimAllAsync();
        var byName = claimed.ToDictionary(i => i.FileName + i.SourcePath);
        h.Source.ChangeItem(byName["dana.md/"].SourceDriveItemId);
        h.Source.DeleteItem(byName["eli.txt/"].SourceDriveItemId);
        h.Source.Revoke("fixture-account-recruiter", byName["scan.png/"].SourceDriveItemId);

        foreach (var name in new[] { "dana.md/", "eli.txt/", "scan.png/" })
            await h.Processor.ProcessAsync(byName[name], (await h.Repo.GetAsync(request.Id))!, default);

        var items = (await h.ItemsAsync(request.Id)).ToDictionary(i => i.FileName + i.SourcePath);
        items["dana.md/"].ReasonCode.Should().Be(BulkReasonCode.SourceChanged);
        items["eli.txt/"].ReasonCode.Should().Be(BulkReasonCode.SourceMissing);
        items["scan.png/"].ReasonCode.Should().Be(BulkReasonCode.AccessDenied);
        items.Values.Where(i => i.Status == BulkItemStatus.Failed).Should().OnlyContain(i => i.ApplicationId == null);
    }

    [Fact]
    public async Task A_retried_file_with_no_recorded_etag_adopts_the_current_one_instead_of_failing_as_changed()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var claimed = await h.ClaimAllAsync();
        var retried = claimed.Single(i => i.FileName == "dana.md");
        h.Source.ChangeItem(retried.SourceDriveItemId);
        retried.SourceETag = string.Empty;

        var outcome = await h.Processor.ProcessAsync(retried, (await h.Repo.GetAsync(request.Id))!, default);

        outcome.Should().Be(BulkItemOutcome.HandedOff);
        var saved = (await h.ItemsAsync(request.Id)).Single(i => i.Id == retried.Id);
        saved.SourceETag.Should().NotBeEmpty("the worker recorded the version it actually read");
        saved.Status.Should().Be(BulkItemStatus.Scoring);
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_completes_and_one_byte_over_fails()
    {
        await using var h = await Harness.CreateAsync();
        var size = h.Fixture("pdf-1page.pdf").Length;
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true, perFile: 8_388_608);
        await h.SetLimitsAsync(request.Id, maxBytesPerFile: size);
        var claimed = (await h.ClaimAllAsync()).Where(i => i.FileName == "alice-1page.pdf").ToList();
        var current = (await h.Repo.GetAsync(request.Id))!;

        var atLimit = await h.Processor.ProcessAsync(claimed[0], current, default);
        await h.SetLimitsAsync(request.Id, maxBytesPerFile: size - 1);
        var over = await h.Processor.ProcessAsync(claimed[1], (await h.Repo.GetAsync(request.Id))!, default);

        atLimit.Should().Be(BulkItemOutcome.HandedOff);
        over.Should().Be(BulkItemOutcome.Failed);
        (await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == claimed[1].Id)).ReasonCode.Should().Be(BulkReasonCode.FileTooLarge);
    }

    [Fact]
    public async Task With_rasterization_the_converted_version_is_scored_and_the_original_is_retained()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);

        var results = await h.ProcessAllAsync(request.Id);

        var items = (await h.ItemsAsync(request.Id)).ToDictionary(i => i.FileName + i.SourcePath);
        var pdf = items["bob-2page.pdf/"];
        pdf.Status.Should().Be(BulkItemStatus.Scoring);
        pdf.ScoredVersion.Should().Be(BulkScoredVersion.Converted);
        pdf.PageCount.Should().Be(2);
        pdf.ConvertedSizeBytes.Should().BeGreaterThan(0);

        var doc = await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == pdf.ApplicationId);
        doc.FileType.Should().Be("application/pdf");
        doc.FileName.Should().Be("bob-2page.pdf");
        doc.Fingerprint.Should().Be(ContentFingerprint.Raw(h.Fixture("pdf-2page.pdf")), "the duplicate fingerprint is of the original bytes");
        doc.ContentSha256.Should().NotBe(doc.Fingerprint);
        (await h.Db.DocumentBlobs.AsNoTracking().SingleAsync(b => b.DocumentId == doc.Id)).Content.Should().StartWith(Convert.ToBase64String("%PDF-raster"u8.ToArray())[..8]);

        var contents = await h.Db.BulkScoringItemContents.AsNoTracking().ToListAsync();
        contents.Where(c => c.ItemId == pdf.Id).Select(c => c.Kind).Should()
            .BeEquivalentTo([BulkContentKind.Original, BulkContentKind.Converted],
                "FR-010C retains both versions after a successful conversion");
        (await h.Repo.GetItemsAsync(request.Id, new(1, 100, Status: BulkItemStatus.Scoring))).Items
            .Single(i => i.Id == pdf.Id).HasRetainedOriginal.Should().BeTrue();
        results.Values.Should().OnlyContain(o => o == BulkItemOutcome.HandedOff || o == BulkItemOutcome.Failed);
    }

    [Fact]
    public async Task Txt_bypasses_conversion_and_is_scored_as_the_original_with_nothing_retained()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);

        await h.ProcessAllAsync(request.Id);

        var txt = (await h.ItemsAsync(request.Id)).Single(i => i.FileName == "eli.txt");
        txt.Status.Should().Be(BulkItemStatus.Scoring);
        txt.ScoredVersion.Should().Be(BulkScoredVersion.Original);
        txt.PageCount.Should().BeNull();
        h.Raster.Calls.Should().NotContain(c => c.Mime == "text/plain");
        (await h.Db.BulkScoringItemContents.AsNoTracking().AnyAsync(c => c.ItemId == txt.Id)).Should().BeFalse();
        (await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == txt.ApplicationId)).FileType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Word_and_markdown_are_converted_by_the_source_then_rasterized_locally()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);

        await h.ProcessAllAsync(request.Id);

        h.Raster.Calls.Should().Contain(c => c.Mime == "application/pdf" && System.Text.Encoding.ASCII.GetString(c.Source, 0, 4) == "%PDF");
        var docx = (await h.ItemsAsync(request.Id)).Single(i => i.FileName == "carla.docx");
        docx.Status.Should().Be(BulkItemStatus.Scoring);
        (await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == docx.ApplicationId)).FileName.Should().Be("carla.pdf");
    }

    [Fact]
    public async Task Corrupt_encrypted_and_oversized_conversions_fail_without_falling_back_to_the_original()
    {
        await using var h = await Harness.CreateAsync();
        h.Raster.FailFor = bytes => bytes.AsSpan().IndexOf("Encrypt"u8) >= 0 ? new BulkConversionException(BulkReasonCode.ConversionFailed, "encrypted")
            : bytes.Length == 21 ? new BulkConversionException(BulkReasonCode.ConversionFailed, "corrupt") : null;
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);

        await h.ProcessAllAsync(request.Id);

        var items = (await h.ItemsAsync(request.Id)).ToDictionary(i => i.FileName + i.SourcePath);
        items["corrupt.pdf/"].ReasonCode.Should().Be(BulkReasonCode.ConversionFailed);
        items["encrypted.pdf/"].ReasonCode.Should().Be(BulkReasonCode.ConversionFailed);
        items["corrupt.pdf/"].ApplicationId.Should().BeNull("it is never scored in its original form");
        items["alice-1page.pdf/"].Status.Should().Be(BulkItemStatus.Scoring, "other items continue");
    }

    [Fact]
    public async Task An_oversized_conversion_is_retried_once_at_120_dpi_and_quality_70_before_it_fails()
    {
        await using var h = await Harness.CreateAsync();
        h.Raster.OutputSizeFor = o => o.Dpi >= 150 ? 5_000 : 3_000;
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        await h.SetLimitsAsync(request.Id, maxBytesPerFile: 4_000);

        await h.ProcessAllAsync(request.Id);

        var pdf = (await h.ItemsAsync(request.Id)).Single(i => i.FileName == "alice-1page.pdf" && i.SourcePath == "/");
        pdf.Status.Should().Be(BulkItemStatus.Scoring, "the smaller version fits");
        pdf.ConvertedSizeBytes.Should().Be(3_000);
        h.Raster.Options.Should().Contain(o => o.Dpi == 150 && o.JpegQuality == 80);
        h.Raster.Options.Should().Contain(o => o.Dpi == 120 && o.JpegQuality == 70);
        var doc = await h.Db.ApplicationDocuments.AsNoTracking().SingleAsync(d => d.ApplicationId == pdf.ApplicationId);
        doc.FileSize.Should().Be(3_000);
    }

    [Fact]
    public async Task A_conversion_within_the_limit_is_made_once_and_one_still_too_large_after_the_step_down_fails()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        await h.ProcessAllAsync(request.Id);
        h.Raster.Options.Should().OnlyContain(o => o.Dpi == 150, "nothing exceeded the limit, so nothing was converted twice");

        await using var big = await Harness.CreateAsync();
        big.Raster.OutputSize = 5_000;
        var second = await big.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        await big.SetLimitsAsync(second.Id, maxBytesPerFile: 4_000);
        await big.ProcessAllAsync(second.Id);

        var pdf = (await big.ItemsAsync(second.Id)).Single(i => i.FileName == "alice-1page.pdf" && i.SourcePath == "/");
        pdf.ReasonCode.Should().Be(BulkReasonCode.ConvertedTooLarge);
        big.Raster.Options.Count(o => o.Dpi == 120).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_converted_document_over_the_limit_fails_as_converted_too_large()
    {
        await using var h = await Harness.CreateAsync();
        h.Raster.OutputSize = 5_000;
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        await h.SetLimitsAsync(request.Id, maxBytesPerFile: 4_000);

        await h.ProcessAllAsync(request.Id);

        var pdf = (await h.ItemsAsync(request.Id)).Single(i => i.FileName == "alice-1page.pdf" && i.SourcePath == "/");
        pdf.Status.Should().Be(BulkItemStatus.Failed);
        pdf.ReasonCode.Should().Be(BulkReasonCode.ConvertedTooLarge);
        pdf.ApplicationId.Should().BeNull();
    }

    [Fact]
    public async Task Transient_failures_back_off_then_end_as_transient_exhausted()
    {
        await using var h = await Harness.CreateAsync();
        h.Rasterizing = new ThrowingRasterizer();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);

        var first = (await h.ClaimAllAsync()).First(i => i.FileName == "bob-2page.pdf");
        (await h.Processor.ProcessAsync(first, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.Retrying);

        var waiting = await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == first.Id);
        waiting.Status.Should().Be(BulkItemStatus.Waiting);
        waiting.NextAttemptAt.Should().BeAfter(Start.AddSeconds(3)).And.BeBefore(Start.AddSeconds(7));
        waiting.LeaseOwner.Should().BeNull();

        var maxAttempts = BulkScoringAccess.FrozenLimits(request).MaxAttempts;
        await h.Db.BulkScoringItems.Where(i => i.Id == first.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Attempt, maxAttempts - 1));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = (await h.Repo.ClaimDueItemsAsync("w", 100, h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(2))).Single(i => i.Id == first.Id);
        second.Attempt.Should().Be(maxAttempts);
        (await h.Processor.ProcessAsync(second, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.Failed);
        (await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == first.Id)).ReasonCode.Should().Be(BulkReasonCode.TransientExhausted);
    }

    [Fact]
    public async Task Atomic_handoff_does_not_use_the_legacy_publish_call_or_duplicate_anything()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var item = (await h.ClaimAllAsync()).First(i => i.FileName == "bob-2page.pdf");
        h.CrashOnPublish = true;

        (await h.Processor.ProcessAsync(item, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.HandedOff);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
        var handedOff = await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        handedOff.ApplicationCreatedAt.Should().NotBeNull();
        handedOff.HandedOffAt.Should().NotBeNull("the durable handoff is committed atomically");
        (await h.Db.Applications.CountAsync()).Should().Be(1);
        (await h.Db.ApplicationDocuments.CountAsync()).Should().Be(1);
        (await h.Db.Applications.AsNoTracking().SingleAsync()).Status.Should().Be("Queued");
    }

    [Fact]
    public async Task A_crash_after_the_source_was_stored_resumes_from_the_staged_copy_without_reading_the_source_again()
    {
        await using var h = await Harness.CreateAsync();
        h.Rasterizing = new ThrowingRasterizer();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        var item = (await h.ClaimAllAsync()).First(i => i.FileName == "bob-2page.pdf");

        (await h.Processor.ProcessAsync(item, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.Retrying);
        var stored = await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        stored.SourceStoredAt.Should().NotBeNull("the download marker survived");
        stored.ConvertedStoredAt.Should().BeNull();

        // If the worker went back to the source it would now find nothing, so success proves it did not.
        h.Source.DeleteItem(item.SourceDriveItemId);
        h.Rasterizing = null;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = (await h.Repo.ClaimDueItemsAsync("w2", 100, h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(2))).Single(i => i.Id == item.Id);

        (await h.Processor.ProcessAsync(again, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.HandedOff);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Atomic_handoff_retains_one_conversion_without_using_the_legacy_publish_call()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        var item = (await h.ClaimAllAsync()).First(i => i.FileName == "bob-2page.pdf");
        h.CrashOnPublish = true;

        (await h.Processor.ProcessAsync(item, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.HandedOff);
        var conversions = h.Raster.Calls.Count;
        conversions.Should().BeGreaterThan(0);
        (await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id))
            .HandedOffAt.Should().NotBeNull();
        h.Raster.Calls.Count.Should().Be(conversions);
        (await h.Db.Applications.CountAsync()).Should().Be(1);
        (await h.Db.ApplicationDocuments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_cancel_requested_before_download_stops_the_item_at_the_step_boundary()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var claimed = await h.ClaimAllAsync();
        await h.Db.BulkScoringRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedAt, (DateTime?)Start));

        var outcome = await h.Processor.ProcessAsync(claimed[0], (await h.Repo.GetAsync(request.Id))!, default);

        outcome.Should().Be(BulkItemOutcome.Cancelled);
        (await h.Db.Applications.CountAsync()).Should().Be(0);
        (await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == claimed[0].Id)).ReasonCode.Should().Be(BulkReasonCode.CancelledByUser);
    }

    [Fact]
    public async Task A_transient_token_error_backs_off_the_file_instead_of_lapsing_or_failing_it()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var item = (await h.ClaimAllAsync())[0];
        h.Access.Transient = true;

        var outcome = await h.Processor.ProcessAsync(item, (await h.Repo.GetAsync(request.Id))!, default);

        outcome.Should().Be(BulkItemOutcome.Retrying);
        var waiting = await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        (waiting.Status, waiting.ReasonCode).Should().Be((BulkItemStatus.Waiting, (BulkReasonCode?)null));
        waiting.NextAttemptAt.Should().BeAfter(Start);

        h.Access.Transient = false;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var again = (await h.Repo.ClaimDueItemsAsync("w2", 100, h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(2))).Single(i => i.Id == item.Id);
        (await h.Processor.ProcessAsync(again, (await h.Repo.GetAsync(request.Id))!, default)).Should().Be(BulkItemOutcome.HandedOff);
    }

    [Fact]
    public async Task A_lapsed_authorization_is_not_an_item_failure_it_surfaces_to_the_worker()
    {
        await using var h = await Harness.CreateAsync();
        var request = await h.ActiveRequestAsync(Candidates, rasterize: false, allowDuplicates: true);
        var item = (await h.ClaimAllAsync())[0];
        h.Access.Lapsed = true;

        var act = async () => await h.Processor.ProcessAsync(item, (await h.Repo.GetAsync(request.Id))!, default);

        await act.Should().ThrowAsync<SourceAuthorizationLapsedException>();
        (await h.Db.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Status.Should().Be(BulkItemStatus.Importing);
    }

    [Fact]
    public async Task Staged_content_lives_under_the_request_prefix_and_is_deleted_with_it()
    {
        await using var h = await Harness.CreateAsync(remote: true);
        var request = await h.ActiveRequestAsync(Candidates, rasterize: true, allowDuplicates: true);
        await h.ProcessAllAsync(request.Id);

        h.Blobs.Names.Should().NotBeEmpty().And.OnlyContain(n => n.StartsWith($"bulk/{request.Id}/"));
        var retained = await h.Db.BulkScoringItemContents.AsNoTracking().ToListAsync();
        retained.Should().OnlyContain(c => c.BlobUri != null && c.ContentBase64 == null, "blob storage is the single location when configured");

        await h.Content.DeleteByRequestAsync(request.Id);

        h.Blobs.Names.Should().BeEmpty();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public BulkScoringRepository Repo { get; }
        public ApplicationRepository Apps { get; }
        public MockSharePointSource Source { get; }
        public ToggleAccess Access { get; } = new();
        public FakeRasterizer Raster { get; } = new();
        public FakeBlobStore Blobs { get; }
        public BulkContentStore Content { get; }
        public Clock Clock { get; } = new(Start);
        public CapturingLogger Logger { get; } = new();
        public CountingSignal Signal { get; } = new();
        public BulkScoringOptions Options { get; }
        public IDocumentRasterizer? Rasterizing { get; set; }
        public bool CrashOnPublish { get; set; }

        private Harness(SqliteConnection connection, AppDbContext db, bool remote)
        {
            _connection = connection;
            Db = db;
            Repo = new BulkScoringRepository(db, Clock);
            Apps = new ApplicationRepository(db);
            Source = new MockSharePointSource(Access);
            Blobs = new FakeBlobStore(remote);
            Content = new BulkContentStore(Blobs);
            Options = BulkScoringOptions.Create(k => k == "MICROSOFT365_SOURCE_MODE" ? "mock" : null, false, false);
        }

        public BulkItemProcessor Processor => new(
            Repo,
            CrashOnPublish ? new CrashingApplications(Apps) : Apps,
            Source,
            Content,
            Rasterizing ?? Raster,
            Options,
            Clock,
            Logger,
            Signal);

        public static async Task<Harness> CreateAsync(bool remote = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Jobs.Add(new Job { Id = "job-1", Title = "Job" });
            await db.SaveChangesAsync();
            return new Harness(connection, db, remote);
        }

        public byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixtureRoot(), "files", name));

        private static string FixtureRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "bulk-scoring-requests");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("tests/fixtures/bulk-scoring-requests was not found.");
        }

        public async Task<BulkScoringRequest> ActiveRequestAsync(string url, bool rasterize, bool allowDuplicates, long perFile = 8_388_608)
        {
            var context = new SourceCallContext(SourcePhase.Interactive, "recruiter@contoso.com");
            var folder = await Source.ResolveFolderAsync(url, context);
            var steps = new DiscoveryStepService(Source, new EligibilityEvaluator(Source), Options, Clock);
            var request = new BulkScoringRequest
            {
                JobId = "job-1",
                OwnerActorId = "recruiter@contoso.com",
                SourceKind = folder.Kind,
                SourceTenantId = folder.TenantId,
                SourceAccountBindingHash = "account-binding",
                SourceDriveId = folder.DriveId,
                SourceFolderItemId = folder.ItemId,
                SourceFolderName = folder.Name,
                SourceFolderPath = folder.Path,
                SourceWebUrl = folder.WebUrl,
                RasterDpi = Options.RasterDpi,
            };
            var limits = BulkRequestLimits.Snapshot(Options, perFile, 30_000);
            request.MaxFilesPerRequest = limits.MaxFilesPerRequest;
            request.MaxBytesPerFile = limits.MaxBytesPerFile;
            request.MaxTotalBytes = limits.MaxTotalBytes;
            request.DiscoveryStepMaxFiles = limits.DiscoveryStepFiles;
            request.DiscoveryStepMaxEntries = limits.DiscoveryStepMaxEntries;
            request.DiscoveryStepMaxSeconds = limits.DiscoveryStepMaxSeconds;

            var outcome = await steps.RunStepAsync(new DiscoveryStepInput(request.Id, folder, null, context, perFile, 30_000, long.MaxValue, 0));
            var draft = await Repo.CreateDraftAsync(request, outcome.ToStepResult(Clock.GetUtcNow().UtcDateTime.AddMinutes(30)));
            while (outcome.Status == BulkDiscoveryStatus.MoreAvailable)
            {
                outcome = await steps.RunStepAsync(new DiscoveryStepInput(draft.Id, folder, outcome.NextCursorJson, context, perFile, 30_000, long.MaxValue, draft.DiscoveredCount));
                draft = await Repo.ApplyDiscoveryStepAsync(draft.Id, draft.ConcurrencyVersion, outcome.ToStepResult(Clock.GetUtcNow().UtcDateTime.AddMinutes(30)));
            }

            var active = await Repo.ConfirmAsync(draft.Id, draft.ConcurrencyVersion, rasterize, allowDuplicates, "cfg-1", null, Clock.GetUtcNow().UtcDateTime);
            Db.ChangeTracker.Clear();
            return active;
        }

        public async Task SetLimitsAsync(string requestId, long? maxBytesPerFile = null)
        {
            var request = (await Repo.GetAsync(requestId))!;
            await Db.BulkScoringRequests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MaxBytesPerFile, maxBytesPerFile ?? request.MaxBytesPerFile));
        }

        public Task<IReadOnlyList<BulkScoringItem>> ClaimAllAsync() =>
            Repo.ClaimDueItemsAsync("worker-1", 1_000, Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(10));

        public async Task<Dictionary<string, BulkItemOutcome>> ProcessAllAsync(string requestId)
        {
            var results = new Dictionary<string, BulkItemOutcome>();
            foreach (var item in await ClaimAllAsync())
                results[item.Id] = await Processor.ProcessAsync(item, (await Repo.GetAsync(requestId))!, default);
            return results;
        }

        public async Task<List<BulkScoringItem>> ItemsAsync(string requestId)
        {
            Db.ChangeTracker.Clear();
            return await Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId).OrderBy(i => i.Ordinal).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingLogger : ILogger<BulkItemProcessor>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception.ToString());
        }
    }

    private sealed class Clock(DateTime start) : TimeProvider
    {
        private DateTime _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }

    private sealed class CountingSignal : IScoringQueueSignal
    {
        public int Pulses { get; private set; }
        public void Pulse() => Pulses++;
        public Task WaitAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ToggleAccess : ISharePointAccessContext
    {
        public bool Lapsed { get; set; }
        public bool Transient { get; set; }
        public string SourceTenantId => "t";
        public Task<string> GetGraphTokenAsync(SourceCallContext context, CancellationToken cancellationToken = default) =>
            Transient && context.Phase == SourcePhase.Background
                ? throw new SourceTransientException("the identity provider is temporarily unavailable")
                : Lapsed && context.Phase == SourcePhase.Background
                    ? throw new SourceAuthorizationLapsedException("token_unavailable")
                    : Task.FromResult("token");
    }

    private sealed class FakeRasterizer : IDocumentRasterizer
    {
        public List<(string Mime, byte[] Source)> Calls { get; } = [];
        public Func<byte[], Exception?>? FailFor { get; set; }
        public int OutputSize { get; set; } = 64;
        public List<RasterizationOptions> Options { get; } = [];
        public Func<RasterizationOptions, int>? OutputSizeFor { get; set; }

        public Task<RasterizationResult> RasterizeAsync(byte[] source, string mimeType, RasterizationOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add((mimeType, source));
            Options.Add(options);
            if (FailFor?.Invoke(source) is { } failure) throw failure;
            var output = new byte[OutputSizeFor?.Invoke(options) ?? OutputSize];
            "%PDF-raster"u8.CopyTo(output);
            return Task.FromResult(new RasterizationResult(output, 2));
        }
    }

    private sealed class ThrowingRasterizer : IDocumentRasterizer
    {
        public Task<RasterizationResult> RasterizeAsync(byte[] source, string mimeType, RasterizationOptions options, CancellationToken cancellationToken = default) =>
            throw new SourceTransientException("temporary", TimeSpan.Zero);
    }

    private sealed class CrashingApplications(IApplicationRepository inner) : IApplicationRepository
    {
        public Task PublishUploadedAsync(IReadOnlyCollection<string> applicationIds, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated crash before publication");
        public Task<IReadOnlyList<TalentMatch.Domain.Entities.Application>> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default) => inner.GetByJobIdAsync(jobId, cancellationToken);
        public Task<IReadOnlyList<TalentMatch.Domain.Entities.Application>> GetByJobIdAsync(string jobId, string? list, double longlistThreshold, double shortlistThreshold, bool includeTestCases = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApplicationCounts> GetCountsByJobIdAsync(string jobId, double longlistThreshold, double shortlistThreshold, bool includeTestCases = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TalentMatch.Domain.Entities.Application?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task<ApplicationDocument?> FindDocumentByFingerprintAsync(string jobId, string fingerprint, CancellationToken cancellationToken = default) => inner.FindDocumentByFingerprintAsync(jobId, fingerprint, cancellationToken);
        public Task AddAsync(TalentMatch.Domain.Entities.Application application, CancellationToken cancellationToken = default) => inner.AddAsync(application, cancellationToken);
        public Task UpdateAsync(TalentMatch.Domain.Entities.Application application, CancellationToken cancellationToken = default) => inner.UpdateAsync(application, cancellationToken);
        public Task AddDocumentAsync(ApplicationDocument document, CancellationToken cancellationToken = default) => inner.AddDocumentAsync(document, cancellationToken);
        public Task<bool> DocumentExistsAsync(string documentId, CancellationToken cancellationToken = default) => inner.DocumentExistsAsync(documentId, cancellationToken);
        public Task<IReadOnlyList<ApplicationDocument>> GetDocumentsAsync(string applicationId, CancellationToken cancellationToken = default) => inner.GetDocumentsAsync(applicationId, cancellationToken);
        public Task SetDocumentBlobReferenceAsync(string documentId, string blobUri, string contentSha256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddScoringRunAsync(ScoringRun run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScoringRun>> GetScoringRunsAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ScoringRun?> GetScoringRunByIdAsync(string scoringRunId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateScoringRunAsync(ScoringRun run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAggregatedResultAsync(AggregatedResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AggregatedResult?> GetAggregatedResultAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtractionAsync(ExtractionArtifact extraction, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExtractionArtifact?> GetExtractionAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetManualReviewAsync(ManualReviewData review, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManualReviewData?> GetManualReviewAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeBlobStore(bool remote) : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _blobs = [];
        public IReadOnlyCollection<string> Names => _blobs.Keys.ToArray();
        public bool IsRemote => remote;

        public Task<BlobUploadResult> PutAsync(string jobId, string applicationId, string documentId, string fileName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> ReadByUriAsync(string blobUri, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(_blobs.GetValueOrDefault(blobUri.Replace("https://fake/", "")));

        public Task<BlobUploadResult> PutObjectAsync(string blobName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default)
        {
            var sha = ContentFingerprint.Raw(bytes);
            if (!remote) return Task.FromResult(new BlobUploadResult(null, sha));
            _blobs[blobName] = bytes;
            return Task.FromResult(new BlobUploadResult("https://fake/" + blobName, sha));
        }

        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            foreach (var key in _blobs.Keys.Where(k => k.StartsWith(prefix)).ToArray()) _blobs.Remove(key);
            return Task.CompletedTask;
        }

        public Task DeleteByUriAsync(string blobUri, CancellationToken cancellationToken = default)
        {
            _blobs.Remove(blobUri.Replace("https://fake/", ""));
            return Task.CompletedTask;
        }
    }
}
