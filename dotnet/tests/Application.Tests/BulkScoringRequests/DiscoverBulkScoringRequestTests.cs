using FluentAssertions;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using Xunit;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

public sealed class DiscoverBulkScoringRequestTests
{
    private static readonly SourceFolder Root = new(BulkSourceKind.SharePoint, "d", "root", "Candidates", "/Candidates", "https://x", "t");
    private static readonly SourceCallContext Context = new(SourcePhase.Interactive, "actor");
    private const long PerFile = 8_388_608;

    private static BulkScoringOptions Options(int stepFiles = 1000, int maxEntries = 10_000, int maxSeconds = 45) =>
        BulkScoringOptions.Create(k => k switch
        {
            "BULK_DISCOVERY_STEP_FILES" => stepFiles.ToString(),
            "BULK_DISCOVERY_STEP_MAX_ENTRIES" => maxEntries.ToString(),
            "BULK_DISCOVERY_STEP_MAX_SECONDS" => maxSeconds.ToString(),
            _ => null,
        }, false, false);

    private static DiscoveryStepService Service(ScriptedSource source, BulkScoringOptions? options = null, TimeProvider? clock = null) =>
        new(source, new EligibilityEvaluator(source), options ?? Options(), clock ?? TimeProvider.System);

    private static DiscoveryStepInput Input(string? cursor, int ordinal = 0, int filesRemaining = 30_000, long bytesRemaining = 53_687_091_200,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? existing = null,
        int? stepFiles = null, int? stepMaxEntries = null, int? stepMaxSeconds = null) =>
        new("req", Root, cursor, Context, PerFile, filesRemaining, bytesRemaining, ordinal, existing,
            stepFiles, stepMaxEntries, stepMaxSeconds is null ? null : TimeSpan.FromSeconds(stepMaxSeconds.Value));

    private static List<SourcePage> EligiblePages(int files, int perPage = 200)
    {
        var pages = new List<SourcePage>();
        for (var start = 0; start < files; start += perPage)
            pages.Add(ScriptedSource.Page(Enumerable.Range(start, Math.Min(perPage, files - start)).Select(i => ScriptedSource.Pdf($"f{i:00000}")).ToList()));
        return pages;
    }

    [Fact]
    public async Task Steps_include_at_most_1000_eligible_files_and_resume_where_they_stopped()
    {
        var source = new ScriptedSource(EligiblePages(2_500));
        var service = Service(source);

        var step1 = await service.RunStepAsync(Input(null));
        step1.NewItems.Should().HaveCount(1_000);
        step1.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        step1.StoppedBy.Should().Be(DiscoveryStopReason.StepFull);

        var step2 = await service.RunStepAsync(Input(step1.NextCursorJson, ordinal: 1_000));
        step2.NewItems.Should().HaveCount(1_000);
        step2.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);

        var step3 = await service.RunStepAsync(Input(step2.NextCursorJson, ordinal: 2_000));
        step3.NewItems.Should().HaveCount(500);
        step3.Status.Should().Be(BulkDiscoveryStatus.Complete);
        step3.NextCursorJson.Should().BeNull();

        var all = step1.NewItems.Concat(step2.NewItems).Concat(step3.NewItems).ToList();
        all.Select(i => i.SourceDriveItemId).Should().OnlyHaveUniqueItems();
        all.Select(i => i.Ordinal).Should().Equal(Enumerable.Range(0, 2_500));
    }

    [Fact]
    public async Task A_page_larger_than_the_step_carries_its_remainder_into_the_next_step()
    {
        var source = new ScriptedSource([ScriptedSource.Page(Enumerable.Range(0, 1_500).Select(i => ScriptedSource.Pdf($"f{i:0000}")).ToList())]);
        var service = Service(source);

        var step1 = await service.RunStepAsync(Input(null));
        step1.NewItems.Should().HaveCount(1_000);
        step1.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        step1.NextCursorJson.Should().NotBeNull("the carried files keep the cursor alive even though the source is exhausted");

        var step2 = await service.RunStepAsync(Input(step1.NextCursorJson, ordinal: 1_000));
        step2.NewItems.Should().HaveCount(500);
        step2.Status.Should().Be(BulkDiscoveryStatus.Complete);
        step1.NewItems.Concat(step2.NewItems).Select(i => i.SourceDriveItemId).Should().OnlyHaveUniqueItems().And.HaveCount(1_500);
    }

    [Fact]
    public async Task Excluded_entries_are_tallied_by_reason_and_never_stored()
    {
        var files = new List<SourceFile>
        {
            ScriptedSource.Pdf("ok1"), ScriptedSource.Pdf("ok2"),
            ScriptedSource.Zip("z1"), ScriptedSource.Zip("z2"), ScriptedSource.Zip("z3"),
            ScriptedSource.Pdf("big", PerFile + 1),
            ScriptedSource.Pdf("empty", 0),
            new("d", "pkg", "notes.pdf", "/", 10, "e", null, true),
            new("d", "doc", "cv.docx", "/", 10, "e", null, false),
        };
        var source = new ScriptedSource([ScriptedSource.Page(files, inaccessible: 2, extraEntries: 3)]);

        var step = await Service(source).RunStepAsync(Input(null));

        step.NewItems.Select(i => i.FileName).Should().BeEquivalentTo(["ok1.pdf", "ok2.pdf", "cv.docx"]);
        step.ExcludedTallies[BulkReasonCode.UnsupportedType].Should().Be(4, "3 zips + 1 package");
        step.ExcludedTallies[BulkReasonCode.FileTooLarge].Should().Be(1);
        step.ExcludedTallies[BulkReasonCode.EmptyFile].Should().Be(1);
        step.InaccessibleDelta.Should().Be(2);
        step.EntriesScannedDelta.Should().Be(9 + 2 + 3);
        step.NewItems.Single(i => i.FileName == "cv.docx").ExcludedWhenRasterized.Should().BeFalse("the source can convert docx");
        // included + excluded + inaccessible account for every file entry scanned (folders are the extra 3)
        (step.NewItems.Count + step.ExcludedTallies.Values.Sum() + step.InaccessibleDelta + 3).Should().Be(step.EntriesScannedDelta);
    }

    [Fact]
    public async Task The_scan_guard_stops_a_step_over_non_candidate_files_and_the_next_step_continues()
    {
        var pages = Enumerable.Range(0, 100)
            .Select(p => ScriptedSource.Page(Enumerable.Range(0, 200).Select(i => ScriptedSource.Zip($"z{p:000}-{i:000}")).ToList()))
            .Append(ScriptedSource.Page([ScriptedSource.Pdf("last")]))
            .ToList();
        var source = new ScriptedSource(pages);
        var service = Service(source);

        var step1 = await service.RunStepAsync(Input(null));
        step1.NewItems.Should().BeEmpty();
        step1.StoppedBy.Should().Be(DiscoveryStopReason.ScanGuard);
        step1.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        step1.EntriesScannedDelta.Should().Be(10_000);
        step1.NextCursorJson.Should().NotBeNull("the cursor holds the unscanned remainder");

        var step2 = await service.RunStepAsync(Input(step1.NextCursorJson));
        step2.EntriesScannedDelta.Should().Be(10_000);
        step2.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        var step3 = await service.RunStepAsync(Input(step2.NextCursorJson));
        step3.Status.Should().Be(BulkDiscoveryStatus.Complete);
        step3.NewItems.Should().ContainSingle(i => i.FileName == "last.pdf");
    }

    [Fact]
    public async Task The_scan_guard_also_stops_a_step_after_the_time_budget()
    {
        var clock = new ManualClock();
        var source = new ScriptedSource(EligiblePages(2_000, perPage: 100), _ => clock.Advance(TimeSpan.FromSeconds(30)));

        var step = await Service(source, clock: clock).RunStepAsync(Input(null));

        step.StoppedBy.Should().Be(DiscoveryStopReason.ScanGuard);
        source.Reads.Should().Be(2, "30 s + 30 s crosses the 45 s budget");
        step.NewItems.Should().HaveCount(200);
        step.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
    }

    [Fact]
    public async Task The_cumulative_file_limit_is_reached_exactly_without_an_error()
    {
        var source = new ScriptedSource(EligiblePages(2_500));
        var service = Service(source);

        var step1 = await service.RunStepAsync(Input(null, filesRemaining: 1_500));
        var step2 = await service.RunStepAsync(Input(step1.NextCursorJson, ordinal: 1_000, filesRemaining: 500));

        step1.NewItems.Should().HaveCount(1_000);
        step1.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        step2.NewItems.Should().HaveCount(500);
        step2.Status.Should().Be(BulkDiscoveryStatus.LimitReached);
        step2.NextCursorJson.Should().BeNull();
        step2.StoppedBy.Should().Be(DiscoveryStopReason.LimitReached);
    }

    [Fact]
    public async Task Exhausting_the_tree_exactly_at_the_limit_is_complete_not_limit_reached()
    {
        var source = new ScriptedSource(EligiblePages(500));

        var step = await Service(source).RunStepAsync(Input(null, filesRemaining: 500));

        step.NewItems.Should().HaveCount(500);
        step.Status.Should().Be(BulkDiscoveryStatus.Complete);
    }

    [Fact]
    public async Task The_total_byte_limit_stops_discovery_at_the_boundary()
    {
        var files = Enumerable.Range(0, 10).Select(i => ScriptedSource.Pdf($"f{i}", 1_000)).ToList();
        var source = new ScriptedSource([ScriptedSource.Page(files)]);

        var step = await Service(source).RunStepAsync(Input(null, bytesRemaining: 4_500));

        step.NewItems.Should().HaveCount(4);
        step.Status.Should().Be(BulkDiscoveryStatus.LimitReached);
    }

    [Fact]
    public async Task Count_limit_accepts_the_exact_boundary_and_stops_before_one_over()
    {
        var source = new ScriptedSource(EligiblePages(1_001, perPage: 1_001));

        var step = await Service(source).RunStepAsync(Input(null, filesRemaining: 1_000));

        step.NewItems.Should().HaveCount(1_000);
        step.NewItems.Should().OnlyContain(item => item.Ordinal < 1_000);
        step.Status.Should().Be(BulkDiscoveryStatus.LimitReached);
        step.StoppedBy.Should().Be(DiscoveryStopReason.LimitReached);
        source.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Aggregate_limit_accepts_the_exact_boundary_and_stops_before_one_over()
    {
        var source = new ScriptedSource([
            ScriptedSource.Page([
                ScriptedSource.Pdf("first", 2_000),
                ScriptedSource.Pdf("exact", 3_000),
                ScriptedSource.Pdf("over", 1),
            ])
        ]);

        var step = await Service(source).RunStepAsync(Input(null, bytesRemaining: 5_000));

        step.NewItems.Select(item => item.FileName).Should().Equal("first.pdf", "exact.pdf");
        step.NewItems.Sum(item => item.SizeBytes).Should().Be(5_000);
        step.Status.Should().Be(BulkDiscoveryStatus.LimitReached);
        step.StoppedBy.Should().Be(DiscoveryStopReason.LimitReached);
    }

    [Fact]
    public async Task Frozen_step_and_scan_guards_override_changed_process_options()
    {
        var source = new ScriptedSource(EligiblePages(20, perPage: 2));
        var service = Service(source, Options(stepFiles: 999, maxEntries: 999, maxSeconds: 999));

        var step = await service.RunStepAsync(Input(null, stepFiles: 4, stepMaxEntries: 4, stepMaxSeconds: 45));

        step.NewItems.Should().HaveCount(4);
        step.EntriesScannedDelta.Should().Be(4);
        step.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        source.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Scan_guard_never_reads_another_page_after_the_frozen_entry_budget_is_met()
    {
        var source = new ScriptedSource([
            ScriptedSource.Page([ScriptedSource.Zip("a"), ScriptedSource.Zip("b")]),
            ScriptedSource.Page([ScriptedSource.Pdf("must-not-be-read")]),
        ]);

        var step = await Service(source, Options(maxEntries: 999))
            .RunStepAsync(Input(null, stepMaxEntries: 2));

        step.NewItems.Should().BeEmpty();
        step.EntriesScannedDelta.Should().Be(2);
        step.StoppedBy.Should().Be(DiscoveryStopReason.ScanGuard);
        step.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
        source.Reads.Should().Be(1);
    }

    [Fact]
    public async Task A_folder_relisted_after_an_expired_skip_token_does_not_double_count()
    {
        // Step 1: one page of folder F, stopped by a tiny scan guard. Step 2: the folder restarts and lists the same files again.
        var files = Enumerable.Range(0, 20).Select(i => ScriptedSource.Pdf($"f{i:00}")).Concat([ScriptedSource.Zip("z1"), ScriptedSource.Zip("z2")]).ToList();
        var first = ScriptedSource.Page(files, "F", inaccessible: 3);
        var again = ScriptedSource.Page(files, "F", inaccessible: 3, restarted: true);
        var service = Service(new ScriptedSource([first, again]), Options(maxEntries: 20));

        var step1 = await service.RunStepAsync(Input(null));
        var storedIds = step1.NewItems.Select(i => i.SourceDriveItemId).ToHashSet();
        var step2 = await service.RunStepAsync(Input(step1.NextCursorJson, ordinal: 20,
            existing: (ids, _) => Task.FromResult<IReadOnlySet<string>>(ids.Where(storedIds.Contains).ToHashSet())));

        step1.NewItems.Should().HaveCount(20);
        step1.ExcludedTallies[BulkReasonCode.UnsupportedType].Should().Be(2);
        step2.NewItems.Should().BeEmpty("already stored items are ignored");
        step2.EntriesScannedDelta.Should().Be(0, "-25 reversed + 25 re-scanned");
        step2.InaccessibleDelta.Should().Be(0);
        step2.ExcludedTallies[BulkReasonCode.UnsupportedType].Should().Be(0);
        step2.Status.Should().Be(BulkDiscoveryStatus.Complete);
    }

    [Fact]
    public async Task A_fully_scanned_empty_folder_reports_complete_with_nothing_included()
    {
        var source = new ScriptedSource([ScriptedSource.Page([ScriptedSource.Zip("only")])]);

        var step = await Service(source).RunStepAsync(Input(null));

        step.NewItems.Should().BeEmpty();
        step.Status.Should().Be(BulkDiscoveryStatus.Complete);
        step.ExcludedTallies[BulkReasonCode.UnsupportedType].Should().Be(1);
    }

    [Fact]
    public async Task The_per_file_limit_is_applied_exactly()
    {
        var source = new ScriptedSource([ScriptedSource.Page([ScriptedSource.Pdf("exact", PerFile), ScriptedSource.Pdf("over", PerFile + 1)])]);

        var step = await Service(source).RunStepAsync(Input(null));

        step.NewItems.Should().ContainSingle(i => i.FileName == "exact.pdf");
        step.ExcludedTallies[BulkReasonCode.FileTooLarge].Should().Be(1);
    }

    [Fact]
    public async Task A_smaller_configured_step_size_is_honored()
    {
        var service = Service(new ScriptedSource(EligiblePages(100, perPage: 10)), Options(stepFiles: 25));

        var step = await service.RunStepAsync(Input(null));

        step.NewItems.Should().HaveCount(25);
        step.Status.Should().Be(BulkDiscoveryStatus.MoreAvailable);
    }
}
