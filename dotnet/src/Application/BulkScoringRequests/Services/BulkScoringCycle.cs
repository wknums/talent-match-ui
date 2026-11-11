using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

// One scheduling cycle of the bulk worker, split from the hosted service so it can be tested without a
// host. Claim/run handle items; the sweep tracks scoring outcomes through the existing pipeline's
// Application status (without adding any hook to extraction or scoring), finalizes requests, and keeps
// the per-request authorization lifecycle (lapse, discard, age limit).
public sealed class BulkScoringCycle(
    IBulkScoringRepository repository,
    IApplicationRepository applications,
    IBulkRequestAuthorizationRepository authorizations,
    BulkItemProcessor processor,
    BulkScoringOptions options,
    TimeProvider clock,
    ILogger<BulkScoringCycle> logger,
    IRequestAuthorizationStore? authorizationStore = null,
    BulkTelemetry? telemetry = null,
    BulkAuthorizationLapseService? lapseService = null,
    BulkAuthorizationCleanupService? cleanupService = null)
{
    public const string WorkerServiceActor = "bulk-scoring-worker";
    public const string ScoringReconciliationServiceActor = "bulk-scoring-reconciliation-service";
    public const string CountReconciliationServiceActor = "bulk-count-reconciliation-service";
    public const string FinalizationServiceActor = "bulk-request-finalization-service";
    public const string NotificationServiceActor = "bulk-notification-service";
    public const int SweepBatchSize = 200;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private BulkAuthorizationLapseService LapseService { get; } = lapseService
        ?? new(
            repository,
            authorizations,
            clock,
            NullLogger<BulkAuthorizationLapseService>.Instance,
            authorizationStore,
            telemetry);

    private BulkAuthorizationCleanupService CleanupService { get; } = cleanupService
        ?? new(repository, authorizations, authorizationStore, telemetry);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BulkScoringItem>> ClaimAsync(string workerId, CancellationToken ct)
    {
        await repository.ReclaimExpiredLeasesAsync(Now, ct);
        return await repository.ClaimDueItemsAsync(workerId, options.ImportConcurrency, Now, LeaseDuration, ct);
    }

    public async Task<BulkItemOutcome?> RunItemAsync(BulkScoringItem item, CancellationToken ct)
    {
        var request = await repository.GetAsync(item.RequestId, ct);
        if (request is null) return null;
        if (item.SourceStoredAt is null
            && await LapseService.GetLapseReasonAsync(request.Id, ct) is { } lapseReason)
        {
            await LapseService.LapseAsync(request.Id, lapseReason, ct);
            return BulkItemOutcome.Failed;
        }
        try
        {
            return await processor.ProcessAsync(item, request, ct);
        }
        catch (SourceAuthorizationLapsedException ex)
        {
            await LapseService.LapseAsync(request.Id, ex.Reason, ct);
            return BulkItemOutcome.Failed;
        }
    }

    // A lapse is not an item failure: the authorization is marked lapsed, every remaining item that has
    // not been handed to scoring fails with a clear sign-in-again reason, and nothing else is tried.
    public Task LapseAsync(string requestId, string reason, CancellationToken ct) =>
        LapseService.LapseAsync(requestId, reason, ct);

    public async Task SweepAsync(CancellationToken ct)
    {
        await SweepScoringItemsAsync(ct);

        foreach (var id in await repository.ListActiveRequestIdsAsync(ct))
            if (await repository.TryFinalizeAsync(id, Now, ct) is { } done)
            {
                logger.LogInformation("Bulk request {RequestId} finished with outcome {Outcome}", done.Id, done.Outcome);
                if (telemetry is not null) await telemetry.RequestCompletedAsync(done);
            }

        // The worker sweeps every five seconds, comfortably inside the cleanup service's fifteen-minute bound.
        await CleanupService.SweepAsync(ct);

        // A request running longer than the authorization may be used is treated as lapsed.
        foreach (var id in await authorizations.ListActiveObtainedBeforeAsync(Now - options.AuthorizationMaxAge, ct))
            await LapseAsync(id, "max_age_exceeded", ct);

        await repository.PurgeExpiredDraftsAsync(Now, ct);
    }

    private async Task SweepScoringItemsAsync(CancellationToken ct)
    {
        var requests = new Dictionary<string, BulkScoringRequest?>();
        foreach (var item in await repository.GetScoringItemsBatchAsync(SweepBatchSize, ct))
        {
            var application = await applications.GetByIdAsync(item.ApplicationId!, ct);
            if (application is null)
            {
                item.TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.ScoringFailed, "The application no longer exists.");
            }
            else if (application.Status is "Completed" or "NeedsManualReview")
            {
                item.TransitionTo(BulkItemStatus.Completed, Now);
            }
            else if (application.Status is "Failed" or "ScoringFailed" or "ExtractionFailed")
            {
                item.TransitionTo(BulkItemStatus.Failed, Now, BulkReasonCode.ScoringFailed, application.LastError is { Length: > 0 } e ? Truncate(e) : "Scoring failed.");
            }
            else
            {
                continue;
            }
            if (!requests.TryGetValue(item.RequestId, out var request))
                requests[item.RequestId] = request = await repository.GetAsync(item.RequestId, ct);
            if (request is null)
                continue;

            if (repository is IAtomicBulkScoringItemRepository atomicRepository)
            {
                await atomicRepository.SaveItemWithAuditAsync(
                    item,
                    BulkAuditWriter.Service(
                        item.Status == BulkItemStatus.Completed
                            ? BulkAuditEventTypes.BulkItemCompleted
                            : BulkAuditEventTypes.BulkItemFailed,
                        ScoringReconciliationServiceActor,
                        request.OwnerActorId,
                        request.CorrelationId,
                        Now,
                        "bulk_item",
                        item.Id,
                        new { state = item.Status.ToWire(), attempt = item.Attempt }),
                    ct);
            }
            else
            {
                await repository.SaveItemAsync(item, ct);
            }
            if (telemetry is not null)
            {
                await telemetry.ItemAsync(
                    request,
                    item,
                    item.Status == BulkItemStatus.Completed ? "completed" : "failed",
                    0);
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
