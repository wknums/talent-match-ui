using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;
using static TalentMatch.Infrastructure.Tests.BulkScoring.BulkScoringWorkerTests;

namespace TalentMatch.Infrastructure.Tests.BulkScoring;

// The worker only observes what the existing pipeline did to each application: it never hooks into extraction or scoring.
public sealed class BulkFinalizationTests
{
    private const string Candidates = "https://tenant-a.sharepoint.com/sites/hr/Shared Documents/Candidates";

    private static async Task<(WorkerHost Host, string RequestId, List<BulkScoringItem> Items)> AllScoringAsync()
    {
        var host = await WorkerHost.CreateAsync();
        var requestId = await host.ConfirmAsync(Candidates, allowDuplicates: true);
        await host.StartWorkerAsync();
        await Eventually(async () => (await host.RequestAsync(requestId)).ScoringCount == 11, "every file reaches scoring");
        await host.StopWorkerAsync();
        var items = await host.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId).OrderBy(i => i.Ordinal).ToListAsync();
        return (host, requestId, items);
    }

    private static Task SetStatusAsync(WorkerHost host, BulkScoringItem item, string status, string? error = null) =>
        host.Db.Applications.Where(a => a.Id == item.ApplicationId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status).SetProperty(a => a.LastError, error));

    [Fact]
    public async Task Pipeline_results_become_item_results_and_the_request_finishes_as_partial()
    {
        var (host, requestId, items) = await AllScoringAsync();
        await using var _ = host;

        for (var i = 0; i < items.Count; i++)
        {
            var status = i switch { < 3 => "Completed", < 5 => "NeedsManualReview", < 7 => "ScoringFailed", < 9 => "ExtractionFailed", _ => "Scoring" };
            await SetStatusAsync(host, items[i], status, status.EndsWith("Failed") ? "model refused" : null);
        }

        using var scope = host.Provider.CreateScope();
        var cycle = scope.ServiceProvider.GetRequiredService<BulkScoringCycle>();
        await cycle.SweepAsync(CancellationToken.None);

        var after = await host.Db.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId).OrderBy(i => i.Ordinal).ToListAsync();
        after.Take(5).Should().OnlyContain(i => i.Status == BulkItemStatus.Completed, "Completed and NeedsManualReview are both finished work");
        after.Skip(5).Take(4).Should().OnlyContain(i => i.Status == BulkItemStatus.Failed && i.ReasonCode == BulkReasonCode.ScoringFailed);
        after.Skip(5).Take(4).Should().OnlyContain(i => i.ReasonMessage == "model refused");
        after.Skip(9).Should().OnlyContain(i => i.Status == BulkItemStatus.Scoring, "applications still being scored are left alone");
        (await host.RequestAsync(requestId)).Status.Should().Be(BulkRequestStatus.Active, "two files are still being scored");

        foreach (var item in after.Skip(9)) await SetStatusAsync(host, item, "Completed");
        await cycle.SweepAsync(CancellationToken.None);

        var done = await host.RequestAsync(requestId);
        done.Status.Should().Be(BulkRequestStatus.Completed);
        done.Outcome.Should().Be(BulkRequestOutcome.Partial);
        (done.CompletedCount, done.FailedCount, done.ScoringCount).Should().Be((7, 4, 0));
    }

    [Fact]
    public async Task A_request_whose_every_file_failed_scoring_finishes_as_failed()
    {
        var (host, requestId, items) = await AllScoringAsync();
        await using var _ = host;
        foreach (var item in items) await SetStatusAsync(host, item, "ScoringFailed");

        using var scope = host.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BulkScoringCycle>().SweepAsync(CancellationToken.None);

        (await host.RequestAsync(requestId)).Outcome.Should().Be(BulkRequestOutcome.Failed);
    }

    [Fact]
    public async Task The_sweep_reads_a_bounded_batch_so_a_huge_request_cannot_monopolize_a_tick()
    {
        var (host, _, _) = await AllScoringAsync();
        await using var _ = host;
        using var scope = host.Provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>();

        (await repository.GetScoringItemsBatchAsync(4)).Should().HaveCount(4);
        (await repository.GetScoringItemsBatchAsync(BulkScoringCycle.SweepBatchSize)).Should().HaveCount(11);
        BulkScoringCycle.SweepBatchSize.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task Counts_always_add_up_after_discovery_and_after_every_state_change()
    {
        var (host, requestId, items) = await AllScoringAsync();
        await using var _ = host;
        await SetStatusAsync(host, items[0], "Completed");
        using var scope = host.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BulkScoringCycle>().SweepAsync(CancellationToken.None);

        var request = await host.RequestAsync(requestId);
        request.ValidateInvariants();
        request.DiscoveredCount.Should().Be(request.WaitingCount + request.ImportingCount + request.ConvertingCount + request.ScoringCount
            + request.CompletedCount + request.FailedCount + request.SkippedCount + request.CancelledCount);
        (await host.Db.BulkScoringItems.CountAsync(i => i.RequestId == requestId)).Should().Be(request.DiscoveredCount, "included items equal item rows");
        request.DiscoveredCount.Should().Be(11);
        // Every entry the discovery looked at is either an included file, an excluded one, or counted as unreadable.
        request.DiscoveredCount.Should().BeLessThanOrEqualTo(request.EntriesScanned);
        (request.DiscoveredCount + request.ExcludedCount).Should().Be(request.EntriesScanned - request.InaccessibleCount - FolderEntries);
    }

    // The mock Candidates tree has this many sub-folders; folders are scanned entries but are not files.
    private const int FolderEntries = 1;
}
