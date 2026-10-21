using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

/// <summary>
/// Idempotently finishes a durable pending job-content cleanup decision. Scheduling is owned by
/// operations; this service never invents a deleting user or correlation identifier.
/// </summary>
public sealed class BulkJobCleanupReconciler(
    IBulkScoringRepository repository,
    IBulkContentStore contentStore,
    TimeProvider clock)
{
    public async Task<bool> ReconcileAsync(
        string jobId,
        string deletingActorId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var requestIds = await repository.ListRequestIdsForJobAsync(jobId, cancellationToken);
        try
        {
            foreach (var requestId in requestIds)
                await contentStore.DeleteByRequestAsync(requestId, cancellationToken);
        }
        catch
        {
            return false;
        }

        return await repository.CompleteJobCleanupAsync(
            jobId,
            deletingActorId,
            correlationId,
            clock.GetUtcNow().UtcDateTime,
            cancellationToken);
    }
}
