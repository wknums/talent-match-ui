using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.HostedServices;

/// <summary>
/// Polls due ScoringBatches and drives submit/poll/finalize against the AWReason
/// platform. Mirrors server/workers/reconciler.ts. Skipped at runtime when
/// scoring mode is sequential.
/// </summary>
public class PlatformScoringReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlatformScoringReconciler> _logger;

    private static readonly int TickMs =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_RECONCILE_INTERVAL_MS"), out var t) ? Math.Max(2000, t) : 15000;
    private static readonly int MaxInflight =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_MAX_INFLIGHT_PER_TICK"), out var t) ? Math.Max(1, t) : 50;
    private static readonly int LeaseSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("AWR_PLATFORM_LEASE_SECONDS"), out var t) ? Math.Max(10, t) : 60;

    private readonly string _owner = $"{Environment.MachineName}-{Environment.ProcessId}";

    public PlatformScoringReconciler(IServiceScopeFactory scopeFactory, ILogger<PlatformScoringReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ProcessJobCommandHandler.ResolveScoringMode() != "platform")
        {
            _logger.LogInformation("[PlatformScoringReconciler] Sequential mode — reconciler disabled.");
            return;
        }
        _logger.LogInformation("[PlatformScoringReconciler] Starting (owner={Owner}, intervalMs={Interval}).", _owner, TickMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        try { await TickAsync(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "Reconciler initial tick failed"); }

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Reconciler tick failed"); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
        var candidates = await repo.FindDueCandidatesAsync(MaxInflight, ct);
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var won = await repo.AcquireLeaseAsync(candidate.Id, _owner, LeaseSeconds, ct);
            if (!won) continue;
            // Reload after lease (state may have changed)
            var batch = await repo.GetByIdAsync(candidate.Id, ct);
            if (batch is null) continue;
            try { await HandleOneAsync(batch, ct); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciler HandleOne crashed for batch {BatchId}", batch.Id);
                await repo.IncrementAttemptAsync(batch.Id, ex.Message, NextBackoff(batch.Attempt + 1), ct);
            }
        }
    }

    private async Task HandleOneAsync(ScoringBatch batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
        var svc = scope.ServiceProvider.GetRequiredService<IPlatformScoringService>();

        // Cancellation has priority
        if (batch.CancelRequested && batch.Status != "completed" && batch.Status != "cancelled" && batch.Status != "failed")
        {
            if (batch.Status == "submitted") await svc.CancelSubmissionAsync(batch, ct);
            await repo.MarkCancelledAsync(batch.Id, ct);
            return;
        }

        if (batch.Status == "pending")
        {
            var outcome = await svc.SubmitBatchAsync(batch, ct);
            switch (outcome.Status)
            {
                case PlatformSubmitStatus.Submitted:
                case PlatformSubmitStatus.IdempotentHit:
                case PlatformSubmitStatus.Cancelled:
                    // submit handler already persisted state
                    return;
                case PlatformSubmitStatus.Transient:
                    await repo.IncrementAttemptAsync(batch.Id, outcome.Error ?? "transient",
                        NextBackoff(batch.Attempt + 1, outcome.RetryAfterMs), ct);
                    return;
                case PlatformSubmitStatus.PermanentFailure:
                default:
                    await HandleSubmitPermanentFailureAsync(batch, outcome.Error ?? "permanent failure", ct);
                    return;
            }
        }

        if (batch.Status == "submitted")
        {
            var outcome = await svc.PollBatchAsync(batch, ct);
            switch (outcome.Status)
            {
                case PlatformPollStatus.StillRunning:
                    await repo.SetNextPollAtAsync(batch.Id,
                        DateTime.UtcNow.AddMilliseconds(outcome.RetryAfterMs ?? 10000), ct);
                    return;
                case PlatformPollStatus.Completed:
                case PlatformPollStatus.Failed:
                case PlatformPollStatus.Cancelled:
                    return;
            }
        }
    }

    private static DateTime NextBackoff(int attempt, int? retryAfterMs = null)
    {
        if (retryAfterMs.HasValue && retryAfterMs.Value > 0)
            return DateTime.UtcNow.AddMilliseconds(retryAfterMs.Value);
        var baseMs = 5000.0;
        var maxMs = 60000.0;
        var ms = Math.Min(maxMs, baseMs * Math.Pow(2, Math.Max(0, attempt - 1)));
        return DateTime.UtcNow.AddMilliseconds(ms);
    }

    private async Task HandleSubmitPermanentFailureAsync(ScoringBatch batch, string error, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScoringBatchRepository>();
        var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
        var dlqRepo = scope.ServiceProvider.GetRequiredService<IFailureQueueRepository>();

        var ids = DeserializeIds(batch.ApplicationIdsJson);

        await repo.MarkFailedAsync(batch.Id, error, ct);
        await repo.ApplyTransitionAsync(batch.JobId, "pending", "failed", 0, ids.Count, ct);

        foreach (var applicationId in ids)
        {
            try
            {
                var app = await appRepo.GetByIdAsync(applicationId, ct);
                if (app != null)
                {
                    app.Status = "ScoringFailed";
                    app.LastError = error;
                    await appRepo.UpdateAsync(app, ct);
                }

                await dlqRepo.AddAsync(new FailureQueueItem
                {
                    EntityType = "Application",
                    EntityId = applicationId,
                    FailureReason = error,
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconciler failed to mark application {ApplicationId} as ScoringFailed for permanently failed batch {BatchId}.",
                    applicationId, batch.Id);
            }
        }
    }

    private static List<string> DeserializeIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
