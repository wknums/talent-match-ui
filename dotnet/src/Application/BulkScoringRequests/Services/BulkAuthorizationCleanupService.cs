using System.Collections.Concurrent;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.BulkScoringRequests.Services;

/// <summary>
/// Retryable boundary for removing request-bound authorization material once no source read can
/// still need it. The durable stores perform the transition idempotently; the local completion set
/// also prevents a stale repository projection from repeating it within one cleanup cycle.
/// </summary>
public sealed class BulkAuthorizationCleanupService(
    IBulkScoringRepository repository,
    IBulkRequestAuthorizationRepository authorizations,
    IRequestAuthorizationStore? authorizationStore = null,
    BulkTelemetry? telemetry = null)
{
    public static readonly TimeSpan MaximumCleanupDelay = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, byte> _completed = new(StringComparer.Ordinal);

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        if (authorizationStore is null)
            return;

        foreach (var requestId in await authorizations.ListDiscardableAsync(cancellationToken))
        {
            if (!_completed.TryAdd(requestId, 0))
                continue;

            try
            {
                await authorizationStore.DiscardAsync(requestId, cancellationToken);
            }
            catch
            {
                _completed.TryRemove(requestId, out _);
                throw;
            }

            if (telemetry is not null)
            {
                var request = await repository.GetAsync(requestId, cancellationToken);
                await telemetry.AuthorizationAsync(
                    "discarded",
                    requestId,
                    request?.JobId,
                    "bulk-authorization-timer",
                    "request_terminal",
                    correlationId: request?.CorrelationId);
            }
        }
    }
}
