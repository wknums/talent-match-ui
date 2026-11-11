using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.HostedServices;

// Durable background worker for bulk scoring requests. All state lives in SQL and every item holds a
// lease, so closing the browser, shutting down the recruiter's machine, restarting the host, or running
// more than one instance cannot lose or duplicate work (research R1). Mirrors the platform reconciler.
public sealed class BulkScoringWorker(
    IServiceScopeFactory scopes,
    BulkScoringOptions options,
    DatabaseInitializationGate databaseGate,
    ILogger<BulkScoringWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"bulk-{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..48];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try { await databaseGate.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        logger.LogInformation("[BulkScoringWorker] Starting (worker={Worker}, concurrency={Concurrency}).", _workerId, options.ImportConcurrency);

        var lastSweep = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimedAny = false;
            try
            {
                IReadOnlyList<Domain.Entities.BulkScoringRequests.BulkScoringItem> claimed;
                using (var scope = scopes.CreateScope())
                    claimed = await scope.ServiceProvider.GetRequiredService<BulkScoringCycle>().ClaimAsync(_workerId, stoppingToken);
                claimedAny = claimed.Count > 0;

                await Parallel.ForEachAsync(
                    claimed,
                    new ParallelOptions { MaxDegreeOfParallelism = options.ImportConcurrency, CancellationToken = stoppingToken },
                    (item, token) => RunClaimedItemAsync(item, token));

                if (DateTime.UtcNow - lastSweep >= SweepInterval)
                {
                    using var sweepScope = scopes.CreateScope();
                    await sweepScope.ServiceProvider.GetRequiredService<BulkScoringCycle>().SweepAsync(stoppingToken);
                    lastSweep = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk scoring cycle failed");
            }

            if (!claimedAny)
            {
                try { await Task.Delay(IdlePoll, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async ValueTask RunClaimedItemAsync(
        Domain.Entities.BulkScoringRequests.BulkScoringItem item,
        CancellationToken cancellationToken)
    {
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseUntilStoppedAsync(item.Id, renewalCancellation.Token);
        try
        {
            using var itemScope = scopes.CreateScope();
            await itemScope.ServiceProvider.GetRequiredService<BulkScoringCycle>()
                .RunItemAsync(item, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bulk item {ItemId} could not be run", item.Id);
        }
        finally
        {
            await renewalCancellation.CancelAsync();
            try { await renewal; }
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested) { }
        }
    }

    private async Task RenewLeaseUntilStoppedAsync(string itemId, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromTicks(BulkScoringCycle.LeaseDuration.Ticks / 3);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            using var scope = scopes.CreateScope();
            var renewed = await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>()
                .RenewLeaseAsync(
                    itemId,
                    _workerId,
                    DateTime.UtcNow,
                    BulkScoringCycle.LeaseDuration,
                    cancellationToken);
            if (!renewed) return;
        }
    }

    // Graceful shutdown hands claimed work back without consuming a retry attempt.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!options.Enabled) return;
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBulkScoringRepository>().ReleaseLeasesAsync(_workerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The leases expire on their own. The logging providers may already be disposed during host shutdown,
            // and a logging failure must not turn a graceful stop into an error.
            try { logger.LogWarning(ex, "Could not release bulk worker leases; they will expire on their own"); }
            catch (Exception) { }
        }
    }
}
