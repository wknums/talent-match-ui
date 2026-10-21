using TalentMatch.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class BulkImportSettingsRepository(AppDbContext context) : IBulkImportSettingsRepository
{
    public Task<BulkImportSettings?> GetPersistedAsync(CancellationToken cancellationToken = default) =>
        context.BulkImportSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == BulkImportSettings.SingletonId, cancellationToken);

    public Task<BulkImportSettings> SaveAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            settings = await StageSettingsAsync(settings, expectedConcurrencyVersion, ct);
            await context.SaveChangesAsync(ct);
            return settings;
        }, cancellationToken);

    public Task<BulkImportSettings> SaveWithAuditAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            settings = await StageSettingsAsync(settings, expectedConcurrencyVersion, ct);
            await context.ProcessingEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            return settings;
        }, cancellationToken);

    public Task<BulkImportSettings> SaveWithAuditAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        ProcessingEvent compatibilityEvent,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            settings = await StageSettingsAsync(settings, expectedConcurrencyVersion, ct);
            await context.ProcessingEvents.AddAsync(compatibilityEvent, ct);
            await context.BulkAuditEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            return settings;
        }, cancellationToken);

    private async Task<BulkImportSettings> StageSettingsAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        CancellationToken cancellationToken)
    {
        var existing = await context.BulkImportSettings
            .SingleOrDefaultAsync(x => x.Id == BulkImportSettings.SingletonId, cancellationToken);
        if (existing is null)
        {
            if (expectedConcurrencyVersion != 0) throw new InvalidOperationException("stale_version");
            settings.Id = BulkImportSettings.SingletonId;
            settings.ConcurrencyVersion = 1;
            await context.BulkImportSettings.AddAsync(settings, cancellationToken);
            return settings;
        }

        if (existing.ConcurrencyVersion != expectedConcurrencyVersion)
            throw new InvalidOperationException("stale_version");
        existing.MaxFilesPerRequest = settings.MaxFilesPerRequest;
        existing.UpdatedAt = settings.UpdatedAt;
        existing.UpdatedByActorId = settings.UpdatedByActorId;
        existing.ConcurrencyVersion++;
        return existing;
    }
}

public sealed class BulkRequestAuthorizationRepository(AppDbContext context) : IBulkRequestAuthorizationRepository
{
    public Task<BulkRequestAuthorization?> GetAsync(string requestId, CancellationToken cancellationToken = default) =>
        context.BulkRequestAuthorizations.SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);

    public async Task SaveAsync(BulkRequestAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var entry = context.Entry(authorization);
        var priorStatus = entry.State == EntityState.Detached
            ? authorization.Status
            : entry.Property(x => x.Status).OriginalValue;
        var priorLastUsedAt = entry.State == EntityState.Detached
            ? authorization.LastUsedAt
            : entry.Property(x => x.LastUsedAt).OriginalValue;
        if (entry.State == EntityState.Detached)
            context.BulkRequestAuthorizations.Update(authorization);
        var now = DateTime.SpecifyKind(
            authorization.DiscardedAt
                ?? authorization.LapsedAt
                ?? authorization.LastUsedAt
                ?? authorization.ObtainedAt,
            DateTimeKind.Utc);
        var correlationId = Guid.NewGuid().ToString();
        if (authorization.Status != priorStatus)
        {
            var eventType = authorization.Status switch
            {
                BulkAuthorizationStatus.Lapsed
                    or BulkAuthorizationStatus.Revoked
                    or BulkAuthorizationStatus.AccessLost =>
                    BulkAuditEventTypes.RequestAuthorizationLapsed,
                BulkAuthorizationStatus.Discarded => BulkAuditEventTypes.RequestAuthorizationDiscarded,
                _ => null,
            };
            if (eventType is not null)
                await context.BulkAuditEvents.AddAsync(
                    BulkAuditWriter.Service(
                        eventType,
                        eventType == BulkAuditEventTypes.RequestAuthorizationDiscarded
                            ? "bulk-authorization-timer"
                            : "bulk-scoring-worker",
                        authorization.ProductActorId,
                        correlationId,
                        now,
                        "request_authorization",
                        authorization.RequestId,
                        new { state = authorization.Status.ToWire(), reasonCode = authorization.LapseReason }),
                    cancellationToken);
        }
        else if (authorization.LastUsedAt is not null
                 && authorization.LastUsedAt != priorLastUsedAt)
        {
            await context.BulkAuditEvents.AddAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.RequestAuthorizationUsed,
                    "bulk-scoring-worker",
                    authorization.ProductActorId,
                    correlationId,
                    now,
                    "request_authorization",
                    authorization.RequestId,
                    new { state = authorization.Status.ToWire() }),
                cancellationToken);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListActiveObtainedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
        await context.BulkRequestAuthorizations.AsNoTracking()
            .Where(a => a.Status == BulkAuthorizationStatus.Active && a.ObtainedAt < cutoffUtc)
            .Select(a => a.RequestId)
            .ToListAsync(cancellationToken);

    // A request that finished, or was cancelled with no file left that still needs to be read, needs no authorization.
    public async Task<IReadOnlyList<string>> ListDiscardableAsync(CancellationToken cancellationToken = default) =>
        await context.BulkRequestAuthorizations.AsNoTracking()
            .Where(a => a.Status == BulkAuthorizationStatus.Active
                && (a.Request!.Status == BulkRequestStatus.Completed
                    || (a.Request.CancelRequestedAt != null
                        && a.Request.WaitingCount + a.Request.ImportingCount + a.Request.ConvertingCount == 0)))
            .Select(a => a.RequestId)
            .ToListAsync(cancellationToken);
}
