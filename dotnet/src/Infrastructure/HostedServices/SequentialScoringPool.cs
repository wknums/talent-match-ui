using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Services;

namespace TalentMatch.Infrastructure.HostedServices;

public sealed class SequentialScoringPool(
    IServiceScopeFactory scopeFactory,
    IScoringQueueSignal signal,
    SequentialScoringOptions options,
    ILogger<SequentialScoringPool> logger) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Sequential scoring pool disabled: platform mode or no sequential endpoint configured.");
            return;
        }

        if (options.MaxParallel < 1 || options.PollInterval <= TimeSpan.Zero
            || options.HeartbeatInterval <= TimeSpan.Zero || options.LeaseDuration <= options.HeartbeatInterval)
            throw new InvalidOperationException("Invalid sequential scoring pool capacity or timing configuration.");

        logger.LogInformation(
            "Sequential scoring pool started with {Slots} document slots per app instance (owner={Owner}).",
            options.MaxParallel, _owner);
        var active = new List<Task>();
        var nextRecovery = DateTime.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var completed in active.Where(task => task.IsCompleted).ToArray())
                {
                    await completed;
                    active.Remove(completed);
                }

                try
                {
                    if (DateTime.UtcNow >= nextRecovery)
                    {
                        using var recoveryScope = scopeFactory.CreateScope();
                        await recoveryScope.ServiceProvider.GetRequiredService<ISequentialScoringQueueRepository>()
                            .RecoverExpiredAsync(DateTime.UtcNow, stoppingToken);
                        nextRecovery = DateTime.UtcNow.Add(options.HeartbeatInterval);
                    }

                    while (active.Count < options.MaxParallel && !stoppingToken.IsCancellationRequested)
                    {
                        using var scope = scopeFactory.CreateScope();
                        var work = await scope.ServiceProvider.GetRequiredService<ISequentialScoringQueueRepository>()
                            .TryClaimAsync(_owner, DateTime.UtcNow, options.LeaseDuration, stoppingToken);
                        if (work is null)
                            break;
                        active.Add(ProcessOneAsync(work, stoppingToken));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sequential scoring queue scan failed; queued work remains in the database.");
                }

                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var wake = signal.WaitAsync(waitCancellation.Token);
                var poll = Task.Delay(options.PollInterval, waitCancellation.Token);
                await Task.WhenAny(active.Append(wake).Append(poll));
                await waitCancellation.CancelAsync();
                try { await wake; }
                catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(active);
        }
    }

    private async Task ProcessOneAsync(SequentialScoringWork work, CancellationToken stoppingToken)
    {
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(work, workCancellation);
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISequentialApplicationScorer>()
                .ScoreAsync(work, workCancellation.Token);
            logger.LogInformation("Sequential scoring completed for application {ApplicationId}.", work.ApplicationId);
        }
        catch (Exception ex)
        {
            var error = stoppingToken.IsCancellationRequested
                ? "Scoring was interrupted by an application restart or shutdown. Retry this application explicitly."
                : workCancellation.IsCancellationRequested
                    ? "Scoring was interrupted because its processing lease could not be maintained. Retry explicitly."
                    : ex.Message;
            logger.LogError(ex, "Sequential scoring stopped for application {ApplicationId}: {Reason}",
                work.ApplicationId, error);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ISequentialScoringQueueRepository>()
                    .FailAsync(work.ApplicationId, work.Owner, error,
                        ex is ScoringRetriesExhaustedException exhausted ? exhausted.FailureCount : 0,
                        cleanup.Token);
            }
            catch (Exception persistenceError)
            {
                logger.LogError(persistenceError,
                    "Could not persist failure for {ApplicationId}; expired-lease recovery will retry persistence.",
                    work.ApplicationId);
            }
        }
        finally
        {
            await workCancellation.CancelAsync();
            await heartbeat;
        }
    }

    private async Task MaintainLeaseAsync(SequentialScoringWork work, CancellationTokenSource workCancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.HeartbeatInterval, workCancellation.Token);
                using var scope = scopeFactory.CreateScope();
                var renewed = await scope.ServiceProvider.GetRequiredService<ISequentialScoringQueueRepository>()
                    .RenewLeaseAsync(work.ApplicationId, work.Owner, DateTime.UtcNow,
                        options.LeaseDuration, workCancellation.Token);
                if (!renewed)
                    throw new InvalidOperationException("The scoring lease is no longer owned by this process.");
            }
        }
        catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to renew scoring lease for application {ApplicationId}.", work.ApplicationId);
            await workCancellation.CancelAsync();
        }
    }
}
