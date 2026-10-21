using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Infrastructure.Persistence;

namespace TalentMatch.Web.Server.Operations;

public sealed record BulkSourceMigrationInventory(
    int ActiveRequests,
    int DraftRequests,
    int ActiveAuthorizations,
    int CompletedHistoryRequests)
{
    public bool IsDrained => ActiveRequests == 0 && DraftRequests == 0 && ActiveAuthorizations == 0;
}

/// <summary>
/// Inventories the superseded runtime before the v3 schema migration. It never translates old
/// OBO/app-only material into a delegated connector cache. Migration is allowed only after
/// operators have drained work or explicitly discarded credentials and settled the requests.
/// </summary>
public sealed class BulkSourceMigrationPreflight(AppDbContext context, TimeProvider clock)
{
    public const string MigrationServiceActor = "bulk-source-migration-service";

    public async Task<BulkSourceMigrationInventory> InventoryAsync(
        CancellationToken cancellationToken = default) =>
        new(
            await context.BulkScoringRequests.CountAsync(
                request => request.Status == BulkRequestStatus.Active, cancellationToken),
            await context.BulkScoringRequests.CountAsync(
                request => request.Status == BulkRequestStatus.Draft, cancellationToken),
            await context.BulkRequestAuthorizations.CountAsync(
                authorization => authorization.Status == BulkAuthorizationStatus.Active, cancellationToken),
            await context.BulkScoringRequests.CountAsync(
                request => request.Status == BulkRequestStatus.Completed, cancellationToken));

    public async Task<BulkSourceMigrationInventory> RequireDrainedAsync(
        CancellationToken cancellationToken = default)
    {
        var inventory = await InventoryAsync(cancellationToken);
        if (!inventory.IsDrained)
            throw new InvalidOperationException(
                "delegated_v3_old_active_work_requires_drain_or_discard");
        return inventory;
    }

    public async Task<int> DiscardOldAuthorizationMaterialAsync(
        bool operatorConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!operatorConfirmed)
            throw new InvalidOperationException("Explicit operator confirmation is required.");

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var active = await context.BulkRequestAuthorizations
            .Where(authorization => authorization.Status == BulkAuthorizationStatus.Active)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var authorization in active)
        {
            authorization.Discard(now);
            await context.BulkAuditEvents.AddAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.RequestAuthorizationDiscarded,
                    MigrationServiceActor,
                    authorization.ProductActorId,
                    Guid.NewGuid().ToString(),
                    now,
                    "bulk_request_authorization",
                    authorization.RequestId,
                    new { reasonCode = "migration_protected_material_discarded" }),
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return active.Count;
    }

    public async Task VerifyCompletedHistoryAsync(
        int expectedCompletedRequests,
        CancellationToken cancellationToken = default)
    {
        var actual = await context.BulkScoringRequests.AsNoTracking().CountAsync(
            request => request.Status == BulkRequestStatus.Completed,
            cancellationToken);
        if (actual != expectedCompletedRequests)
            throw new InvalidOperationException(
                $"delegated_v3_completed_history_mismatch:{expectedCompletedRequests}:{actual}");
    }
}
