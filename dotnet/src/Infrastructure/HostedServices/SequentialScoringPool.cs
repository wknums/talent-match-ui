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
    DatabaseInitializationGate databaseGate,
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

        try { await databaseGate.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        logger.LogInformation(
            "Sequential scoring pool started with {Slots} document slots per app instance (lease {LeaseSeconds}s, renewed every {HeartbeatSeconds}s, owner={Owner}).",
            options.MaxParallel, (int)options.LeaseDuration.TotalSeconds, (int)options.HeartbeatInterval.TotalSeconds, _owner);
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
            workCancellation.Token.ThrowIfCancellationRequested();
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
        var leaseValidUntil = work.LeaseUntil ?? DateTime.UtcNow.Add(options.LeaseDuration);
        try
        {
            while (true)
            {
                // Leave one heartbeat of headroom for cancellation and failure persistence.
                // This deadline also bounds database retries and their backoff, not only the heartbeat delay.
                var remaining = leaseValidUntil - options.HeartbeatInterval - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    await workCancellation.CancelAsync();
                    return;
                }
                workCancellation.CancelAfter(remaining);
                await Task.Delay(options.HeartbeatInterval, workCancellation.Token);
                var attemptedAt = DateTime.UtcNow;
                bool renewed;
                try
                {
                    renewed = await RenewWithRetriesAsync(work, workCancellation.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !workCancellation.IsCancellationRequested)
                {
                    // Every attempt failed. Scoring continues while the lease is still comfortably valid, because the
                    // database may simply be briefly unavailable; the next heartbeat tries again.
                    if (DateTime.UtcNow < leaseValidUntil - options.HeartbeatInterval)
                    {
                        logger.LogWarning(ex,
                            "Could not renew the scoring lease for application {ApplicationId}; the lease is valid until {ValidUntil:O}, trying again at the next heartbeat.",
                            work.ApplicationId, leaseValidUntil);
                        continue;
                    }

                    throw;
                }

                if (!renewed)
                    throw new InvalidOperationException("The scoring lease is no longer owned by this process.");
                leaseValidUntil = attemptedAt.Add(options.LeaseDuration);
            }
        }
        catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to renew scoring lease for application {ApplicationId}.", work.ApplicationId);
            await workCancellation.CancelAsync();
        }
    }

    // Returns false when the lease is no longer ours (never retried: another owner may hold it).
    // Throws the last error when every attempt fails.
    private async Task<bool> RenewWithRetriesAsync(SequentialScoringWork work, CancellationToken ct)
    {
        var attempts = Math.Max(1, options.RenewAttempts);
        var delay = options.RenewRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCancellation.CancelAfter(options.HeartbeatInterval);
                try
                {
                    return await scope.ServiceProvider.GetRequiredService<ISequentialScoringQueueRepository>()
                        .RenewLeaseAsync(work.ApplicationId, work.Owner, DateTime.UtcNow, options.LeaseDuration,
                            attemptCancellation.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested
                    && attemptCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException("The scoring lease renewal exceeded one heartbeat interval.", ex);
                }
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Scoring lease renewal for application {ApplicationId} failed (attempt {Attempt} of {Attempts}); retrying in {Delay}.",
                    work.ApplicationId, attempt, attempts, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.RenewRetryMaxDelay.Ticks));
            }
        }
    }
}
