using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

public sealed class BulkAuthorizationLapseService(
    IBulkScoringRepository repository,
    IBulkRequestAuthorizationRepository authorizations,
    TimeProvider clock,
    ILogger<BulkAuthorizationLapseService> logger,
    IRequestAuthorizationStore? authorizationStore = null,
    BulkTelemetry? telemetry = null)
{
    private const string Message =
        "Your sign-in lapsed while this request was running. Sign in again, then use Retry failed.";

    private static readonly SemaphoreSlim TransitionGate = new(1, 1);

    public async Task<string?> GetLapseReasonAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var authorization = await authorizations.GetAsync(requestId, cancellationToken);
        return authorization switch
        {
            null => "authorization_missing",
            { IsActive: true } => null,
            _ => authorization.LapseReason ?? "authorization_not_active",
        };
    }

    public async Task LapseAsync(
        string requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await TransitionGate.WaitAsync(cancellationToken);
        try
        {
            var authorization = await authorizations.GetAsync(requestId, cancellationToken);
            var transitioned = authorization?.IsActive ?? false;
            if (transitioned && authorizationStore is not null)
                await authorizationStore.MarkLapsedAsync(requestId, reason, cancellationToken);

            var failed = await repository.FailNonTerminalItemsAsync(
                requestId,
                BulkReasonCode.AuthorizationLapsed,
                Message,
                clock.GetUtcNow().UtcDateTime,
                cancellationToken);

            if (!transitioned)
                return;

            logger.LogWarning(
                "Authorization for bulk request {RequestId} lapsed ({Reason}); {Count} source-dependent items failed",
                requestId,
                reason,
                failed);
            if (telemetry is not null)
            {
                var request = await repository.GetAsync(requestId, cancellationToken);
                await telemetry.AuthorizationAsync(
                    "lapsed",
                    requestId,
                    request?.JobId,
                    BulkTelemetry.SystemActor,
                    reason,
                    failed,
                    request?.CorrelationId);
            }
        }
        finally
        {
            TransitionGate.Release();
        }
    }
}
