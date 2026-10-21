using System.Data;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class BulkScoringRepository(AppDbContext context, TimeProvider clock)
    : IBulkScoringRepository, IAtomicBulkScoringHandoffRepository, IAtomicBulkScoringItemRepository
{
    private const string StaleVersion = "stale_version";

    public Task<BulkScoringRequest> CreateDraftAsync(
        BulkScoringRequest draft,
        BulkDiscoveryStepResult firstStep,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            draft.Status = BulkRequestStatus.Draft;
            draft.ConcurrencyVersion = 1;
            await context.BulkScoringRequests.AddAsync(draft, ct);
            await ApplyStepAsync(draft, firstStep, ct);
            await context.BulkAuditEvents.AddRangeAsync(
                DiscoveryAudits(draft, firstStep, includeDraftCreated: true),
                ct);
            await context.SaveChangesAsync(ct);
            return draft;
        }, cancellationToken);

    public Task<BulkScoringRequest> ApplyDiscoveryStepAsync(
        string requestId,
        int expectedConcurrencyVersion,
        BulkDiscoveryStepResult step,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await context.BulkScoringRequests.SingleAsync(x => x.Id == requestId, ct);
            if (request.ConcurrencyVersion != expectedConcurrencyVersion) throw new InvalidOperationException(StaleVersion);
            request.EnsureCanDiscoverMore(clock.GetUtcNow().UtcDateTime);
            await ApplyStepAsync(request, step, ct);
            request.ConcurrencyVersion++;
            await context.BulkAuditEvents.AddRangeAsync(
                DiscoveryAudits(request, step, includeDraftCreated: false),
                ct);
            await context.SaveChangesAsync(ct);
            return request;
        }, cancellationToken);

    public Task<BulkScoringRequest> ConfirmAsync(
        string requestId,
        int expectedConcurrencyVersion,
        bool convertToRasterPdf,
        bool allowDuplicates,
        string? scoringConfigVersionId,
        BulkRequestAuthorization? authorization,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await ConfirmCoreAsync(
                requestId,
                expectedConcurrencyVersion,
                null,
                convertToRasterPdf,
                allowDuplicates,
                scoringConfigVersionId,
                authorization,
                utcNow,
                ct);
            await context.SaveChangesAsync(ct);
            return request;
        }, cancellationToken);

    public Task<BulkScoringRequest> ConfirmAtomicallyAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string submitActorId,
        bool convertToRasterPdf,
        bool allowDuplicates,
        string? jobConfigVersionId,
        DateTime utcNow,
        Func<CancellationToken, Task<BulkRequestAuthorization>> transferAuthorization,
        Func<BulkRequestAuthorization, CancellationToken, Task> rollbackAuthorization,
        IReadOnlyList<ProcessingEvent> auditEvents,
        Func<ProcessingEvent, CancellationToken, Task> persistAudit,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var authorization = await transferAuthorization(ct);
            BulkScoringRequest request;
            try
            {
                request = await ConfirmCoreAsync(
                    requestId,
                    expectedConcurrencyVersion,
                    submitActorId,
                    convertToRasterPdf,
                    allowDuplicates,
                    jobConfigVersionId,
                    authorization,
                    utcNow,
                    ct);
                var correlationId = auditEvents.FirstOrDefault()?.CorrelationId
                    ?? request.CorrelationId;
                await context.BulkAuditEvents.AddAsync(
                    BulkAuditWriter.User(
                        BulkAuditEventTypes.BulkRequestConfirmed,
                        submitActorId,
                        correlationId,
                        utcNow,
                        "bulk_request",
                        request.Id,
                        new
                        {
                            state = request.Status.ToWire(),
                            itemCount = request.DiscoveredCount,
                            configurationVersion = request.ConcurrencyVersion,
                            request.ConvertToRasterPdf,
                            request.AllowDuplicates,
                        }),
                    ct);
                if (authorization is not null && request.ConnectionId is null)
                {
                    await context.BulkAuditEvents.AddAsync(
                        BulkAuditWriter.User(
                            BulkAuditEventTypes.RequestAuthorizationEstablished,
                            submitActorId,
                            correlationId,
                            utcNow,
                            "request_authorization",
                            request.Id,
                            new { state = authorization.Status.ToWire() }),
                        ct);
                }
                await context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                await rollbackAuthorization(authorization, CancellationToken.None);
                throw new BulkConfirmationPersistenceException(ex);
            }

            foreach (var auditEvent in auditEvents)
                await persistAudit(auditEvent, ct);

            return request;
        }, cancellationToken);

    private static IReadOnlyList<BulkAuditEvent> DiscoveryAudits(
        BulkScoringRequest request,
        BulkDiscoveryStepResult step,
        bool includeDraftCreated)
    {
        var events = new List<BulkAuditEvent>(step.NewItems.Count + 2);
        var occurredAt = DateTime.SpecifyKind(
            request.CreatedAt == default ? DateTime.UtcNow : request.CreatedAt,
            DateTimeKind.Utc);
        if (includeDraftCreated)
        {
            events.Add(BulkAuditWriter.User(
                BulkAuditEventTypes.PreviewDraftCreated,
                request.OwnerActorId,
                request.CorrelationId,
                occurredAt,
                "bulk_request",
                request.Id,
                new { state = request.Status.ToWire(), configurationVersion = request.ConcurrencyVersion }));
        }
        events.Add(BulkAuditWriter.User(
            BulkAuditEventTypes.PreviewDiscoveryCommitted,
            request.OwnerActorId,
            request.CorrelationId,
            occurredAt,
            "bulk_request",
            request.Id,
            new
            {
                state = step.Status.ToWire(),
                itemCount = step.NewItems.Count,
                excludedCount = step.ExcludedTallies.Values.Sum(),
                inaccessibleCount = step.InaccessibleDelta,
                configurationVersion = request.ConcurrencyVersion,
            }));
        events.AddRange(step.NewItems.Select(item => BulkAuditWriter.User(
            BulkAuditEventTypes.BulkItemDiscovered,
            request.OwnerActorId,
            request.CorrelationId,
            occurredAt,
            "bulk_item",
            item.Id,
            new
            {
                state = item.Status.ToWire(),
                byteCount = item.SizeBytes,
                configurationVersion = request.ConcurrencyVersion,
            })));
        return events;
    }

    private async Task<BulkScoringRequest> ConfirmCoreAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string? submitActorId,
        bool convertToRasterPdf,
        bool allowDuplicates,
        string? jobConfigVersionId,
        BulkRequestAuthorization? authorization,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var request = await context.BulkScoringRequests.SingleAsync(x => x.Id == requestId, cancellationToken);
        if (request.ConcurrencyVersion != expectedConcurrencyVersion)
            throw new InvalidOperationException(StaleVersion);

        if (convertToRasterPdf)
        {
            var dropped = await context.BulkScoringItems
                .Where(i => i.RequestId == requestId && i.ExcludedWhenRasterized)
                .ExecuteDeleteAsync(cancellationToken);
            if (dropped > 0)
                request.AdjustExcluded(BulkReasonCode.NotConvertible, dropped);
        }

        var (byStatus, bytes) = await LoadCountsAsync(requestId, cancellationToken);
        request.ApplyCounts(byStatus, bytes);
        if (submitActorId is not null)
            request.OwnerActorId = submitActorId;
        request.Confirm(convertToRasterPdf, allowDuplicates, jobConfigVersionId, utcNow);

        if (authorization is not null)
        {
            if (!authorization.IsBoundTo(
                    requestId,
                    request.OwnerActorId,
                    request.SourceTenantId,
                    request.SourceAccountBindingHash,
                    request.SourceDriveId,
                    request.SourceFolderItemId))
                throw new InvalidOperationException("The authorization is not bound to this request and folder.");
            if (context.Entry(authorization).State == EntityState.Detached)
                await context.BulkRequestAuthorizations.AddAsync(authorization, cancellationToken);
            request.Authorization = authorization;
        }

        request.ConcurrencyVersion++;
        return request;
    }

    public Task<BulkScoringRequest> CreateRetryRequestAsync(
        BulkScoringRequest retry,
        IReadOnlyList<BulkScoringItem> items,
        BulkRequestAuthorization? authorization,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var item in items) item.RequestId = retry.Id;
            retry.ApplyItemCounts(items);
            await context.BulkScoringRequests.AddAsync(retry, ct);
            await context.BulkScoringItems.AddRangeAsync(items, ct);
            if (authorization is not null) await context.BulkRequestAuthorizations.AddAsync(authorization, ct);
            await context.SaveChangesAsync(ct);
            return retry;
        }, cancellationToken);

    public Task<BulkScoringRequest> CreateRetryRequestWithAuditAsync(
        BulkScoringRequest retry,
        IReadOnlyList<BulkScoringItem> items,
        BulkRequestAuthorization? authorization,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var item in items) item.RequestId = retry.Id;
            retry.ApplyItemCounts(items);
            await context.BulkScoringRequests.AddAsync(retry, ct);
            await context.BulkScoringItems.AddRangeAsync(items, ct);
            if (authorization is not null) await context.BulkRequestAuthorizations.AddAsync(authorization, ct);
            await context.BulkAuditEvents.AddRangeAsync(auditEvents, ct);
            await context.SaveChangesAsync(ct);
            return retry;
        }, cancellationToken);

    public async Task<bool> DeleteDraftAsync(string requestId, string ownerActorId, CancellationToken cancellationToken = default)
    {
        var removed = await context.BulkScoringRequests
            .Where(x => x.Id == requestId && x.OwnerActorId == ownerActorId && x.Status == BulkRequestStatus.Draft)
            .ExecuteDeleteAsync(cancellationToken);
        return removed > 0;
    }

    public Task<bool> DeleteDraftWithAuditAsync(
        string requestId,
        string ownerActorId,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await context.BulkScoringRequests.SingleOrDefaultAsync(
                x => x.Id == requestId
                    && x.OwnerActorId == ownerActorId
                    && x.Status == BulkRequestStatus.Draft,
                ct);
            if (request is null) return false;
            context.BulkScoringRequests.Remove(request);
            await context.BulkAuditEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> PurgeExpiredDraftsAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var ids = await context.BulkScoringRequests.AsNoTracking()
            .Where(x => x.Status == BulkRequestStatus.Draft && x.ExpiresAt != null && x.ExpiresAt <= utcNow)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count > 0)
            await context.BulkScoringRequests
                .Where(x => ids.Contains(x.Id) && x.Status == BulkRequestStatus.Draft)
                .ExecuteDeleteAsync(cancellationToken);
        return ids;
    }

    public Task<BulkScoringRequest?> GetAsync(string requestId, CancellationToken cancellationToken = default) =>
        context.BulkScoringRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestId, cancellationToken);

    public Task<bool> HasUsedScoringConfigVersionDifferentAsync(
        string requestId,
        string capturedVersionId,
        CancellationToken cancellationToken = default) =>
        context.BulkScoringItems.AsNoTracking().AnyAsync(
            item => item.RequestId == requestId
                && item.ScoringConfigVersionId != null
                && item.ScoringConfigVersionId != capturedVersionId,
            cancellationToken);

    public async Task<BulkPage<BulkScoringRequest>> ListByJobAsync(string jobId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.BulkScoringRequests.AsNoTracking()
            .Where(x => x.JobId == jobId && x.Status != BulkRequestStatus.Draft);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows, page, pageSize, total);
    }

    public async Task<IReadOnlyList<BulkScoringRequest>> ListUnacknowledgedAsync(string ownerActorId, CancellationToken cancellationToken = default) =>
        await context.BulkScoringRequests.AsNoTracking()
            .Where(x => x.OwnerActorId == ownerActorId && x.Status == BulkRequestStatus.Completed && x.AcknowledgedAt == null)
            .OrderByDescending(x => x.TerminalAt)
            .ToListAsync(cancellationToken);

    public async Task<BulkPage<BulkScoringItem>> GetItemsAsync(string requestId, BulkItemQuery query, CancellationToken cancellationToken = default)
    {
        var items = context.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId);
        if (query.Status is { } status) items = items.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(query.NameContains))
        {
            var escaped = query.NameContains.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            items = items.Where(i => EF.Functions.Like(i.FileName, $"%{escaped}%", "\\"));
        }

        var total = await items.CountAsync(cancellationToken);
        var rows = await items.OrderBy(i => i.Ordinal)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(i => new { Item = i, HasOriginal = i.Contents.Any(c => c.Kind == BulkContentKind.Original) })
            .ToListAsync(cancellationToken);
        return new(rows.Select(r => { r.Item.HasRetainedOriginal = r.HasOriginal; return r.Item; }).ToList(), query.Page, query.PageSize, total);
    }

    public Task<BulkScoringItem?> GetItemAsync(string requestId, string itemId, CancellationToken cancellationToken = default) =>
        context.BulkScoringItems.AsNoTracking().SingleOrDefaultAsync(i => i.RequestId == requestId && i.Id == itemId, cancellationToken);

    public Task<BulkScoringItemContent?> GetContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default) =>
        context.BulkScoringItemContents.AsNoTracking().SingleOrDefaultAsync(c => c.ItemId == itemId && c.Kind == kind, cancellationToken);

    public async Task<IReadOnlyList<BulkScoringItem>> GetRetryableItemsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var candidates = await context.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId
                && (i.Status == BulkItemStatus.Failed || i.Status == BulkItemStatus.Cancelled)
                && i.HandedOffAt == null)
            .OrderBy(i => i.Ordinal)
            .ToListAsync(cancellationToken);
        return candidates.Where(i => i.IsRetryable).ToList();
    }

    public async Task<BulkInclusionTotals> GetInclusionTotalsAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var items = context.BulkScoringItems.AsNoTracking().Where(i => i.RequestId == requestId);
        var all = await items.GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(i => i.SizeBytes) }).SingleOrDefaultAsync(cancellationToken);
        var raster = await items.Where(i => !i.ExcludedWhenRasterized)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(i => i.SizeBytes) }).SingleOrDefaultAsync(cancellationToken);
        return new(all?.Count ?? 0, all?.Bytes ?? 0, raster?.Count ?? 0, raster?.Bytes ?? 0);
    }

    public async Task<IReadOnlySet<string>> FindExistingSourceItemIdsAsync(
        string requestId, IReadOnlyCollection<string> sourceItemIds, CancellationToken cancellationToken = default)
    {
        var ids = sourceItemIds.ToList();
        return (await context.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId && ids.Contains(i.SourceDriveItemId))
            .Select(i => i.SourceDriveItemId).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task<IReadOnlyList<BulkScoringItem>> ClaimDueItemsAsync(
        string leaseOwner,
        int maxItems,
        DateTime utcNow,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var requestIds = await context.BulkScoringRequests.AsNoTracking()
            .Where(r => r.Status == BulkRequestStatus.Active && r.CancelRequestedAt == null)
            .Select(r => r.Id).ToListAsync(cancellationToken);
        return await InRequestsTransactionAsync<IReadOnlyList<BulkScoringItem>>(requestIds, async ct =>
        {
            var requests = await context.BulkScoringRequests.AsNoTracking()
                .Where(r => r.Status == BulkRequestStatus.Active
                    && r.CancelRequestedAt == null && requestIds.Contains(r.Id))
                .OrderBy(r => r.ConfirmedAt)
                .Select(r => new { r.Id, r.CorrelationId, r.OwnerActorId })
                .ToListAsync(ct);
            if (requests.Count == 0 || maxItems <= 0) return [];

            var perRequest = Math.Max(1, maxItems / requests.Count);
            var candidates = new List<string>();
            foreach (var request in requests)
            {
                if (candidates.Count >= maxItems) break;
                var ids = await DueItemIds(request.Id, utcNow)
                    .Take(Math.Min(perRequest, maxItems - candidates.Count))
                    .ToListAsync(ct);
                candidates.AddRange(ids);
            }
            foreach (var request in requests)
            {
                if (candidates.Count >= maxItems) break;
                var more = await DueItemIds(request.Id, utcNow)
                    .Where(id => !candidates.Contains(id))
                    .Take(maxItems - candidates.Count)
                    .ToListAsync(ct);
                candidates.AddRange(more);
            }

            var leaseUntil = utcNow + leaseDuration;
            var claimed = new List<string>();
            foreach (var id in candidates)
            {
                var updated = await context.BulkScoringItems
                    .Where(i => i.Id == id && i.Status == BulkItemStatus.Waiting)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Status, BulkItemStatus.Importing)
                        .SetProperty(i => i.LeaseOwner, leaseOwner)
                        .SetProperty(i => i.LeaseUntil, leaseUntil)
                        .SetProperty(i => i.Attempt, i => i.Attempt + 1)
                        .SetProperty(i => i.UpdatedAt, utcNow), ct);
                if (updated == 1) claimed.Add(id);
            }

            if (claimed.Count == 0) return [];
            var items = await context.BulkScoringItems.AsNoTracking()
                .Where(i => claimed.Contains(i.Id))
                .ToListAsync(ct);
            var requestById = requests.ToDictionary(request => request.Id);
            foreach (var item in items)
            {
                var request = requestById[item.RequestId];
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkItemClaimed,
                        leaseOwner,
                        request.OwnerActorId,
                        request.CorrelationId,
                        utcNow,
                        "bulk_item",
                        item.Id,
                        new { state = BulkItemStatus.Importing.ToWire(), attempt = item.Attempt }),
                    ensureUnique: false,
                    ct);
            }
            foreach (var requestId in items.Select(i => i.RequestId).Distinct())
                await RecomputeCountsAsync(requestId, ct);
            await context.SaveChangesAsync(ct);
            return items;
        }, cancellationToken);
    }

    public async Task<int> ReclaimExpiredLeasesAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var requestIds = await context.BulkScoringItems.AsNoTracking()
            .Where(i => (i.Status == BulkItemStatus.Importing || i.Status == BulkItemStatus.Converting)
                && i.LeaseUntil != null && i.LeaseUntil < utcNow)
            .Select(i => i.RequestId).Distinct().ToListAsync(cancellationToken);
        return await InRequestsTransactionAsync(requestIds, async ct =>
        {
            var expired = await (
                from item in context.BulkScoringItems.AsNoTracking()
                join request in context.BulkScoringRequests.AsNoTracking() on item.RequestId equals request.Id
                where (item.Status == BulkItemStatus.Importing || item.Status == BulkItemStatus.Converting)
                    && item.LeaseUntil != null && item.LeaseUntil < utcNow
                    && requestIds.Contains(item.RequestId)
                select new { Item = item, request.CorrelationId, request.OwnerActorId })
                .ToListAsync(ct);
            if (expired.Count == 0) return 0;

            var ids = expired.Select(row => row.Item.Id).ToList();
            var count = await context.BulkScoringItems.Where(item => ids.Contains(item.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, BulkItemStatus.Waiting)
                    .SetProperty(i => i.LeaseOwner, (string?)null)
                    .SetProperty(i => i.LeaseUntil, (DateTime?)null)
                    .SetProperty(i => i.UpdatedAt, utcNow), ct);
            foreach (var row in expired)
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkItemLeaseRecovered,
                        "bulk-lease-recovery-service",
                        row.OwnerActorId,
                        row.CorrelationId,
                        utcNow,
                        "bulk_item",
                        row.Item.Id,
                        new { state = BulkItemStatus.Waiting.ToWire(), attempt = row.Item.Attempt }),
                    ensureUnique: false,
                    ct);
            foreach (var requestId in expired.Select(row => row.Item.RequestId).Distinct())
                await RecomputeCountsAsync(requestId, ct);
            await context.SaveChangesAsync(ct);
            return count;
        }, cancellationToken);
    }

    public Task<bool> RenewLeaseAsync(
        string itemId,
        string leaseOwner,
        DateTime utcNow,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        InItemTransactionAsync(itemId, async ct =>
        {
            var item = await (
                from candidate in context.BulkScoringItems.AsNoTracking()
                join request in context.BulkScoringRequests.AsNoTracking() on candidate.RequestId equals request.Id
                where candidate.Id == itemId
                    && candidate.LeaseOwner == leaseOwner
                    && (candidate.Status == BulkItemStatus.Importing || candidate.Status == BulkItemStatus.Converting)
                select new { Item = candidate, request.CorrelationId, request.OwnerActorId })
                .SingleOrDefaultAsync(ct);
            if (item is null) return false;

            var updated = await context.BulkScoringItems
                .Where(candidate => candidate.Id == itemId
                    && candidate.LeaseOwner == leaseOwner
                    && (candidate.Status == BulkItemStatus.Importing || candidate.Status == BulkItemStatus.Converting))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.LeaseUntil, utcNow + leaseDuration)
                    .SetProperty(candidate => candidate.UpdatedAt, utcNow), ct);
            if (updated != 1) return false;
            await AddAuditIfMissingAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.BulkItemLeaseRenewed,
                    leaseOwner,
                    item.OwnerActorId,
                    item.CorrelationId,
                    utcNow,
                    "bulk_item",
                    itemId,
                    new { state = item.Item.Status.ToWire(), attempt = item.Item.Attempt }),
                ensureUnique: false,
                ct);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    public async Task ReleaseLeasesAsync(string leaseOwner, CancellationToken cancellationToken = default)
    {
        var requestIds = await context.BulkScoringItems.AsNoTracking()
            .Where(i => i.LeaseOwner == leaseOwner
                && (i.Status == BulkItemStatus.Importing || i.Status == BulkItemStatus.Converting))
            .Select(i => i.RequestId).Distinct().ToListAsync(cancellationToken);
        await InRequestsTransactionAsync(requestIds, async ct =>
        {
            var owned = await (
                from item in context.BulkScoringItems.AsNoTracking()
                join request in context.BulkScoringRequests.AsNoTracking() on item.RequestId equals request.Id
                where item.LeaseOwner == leaseOwner
                    && (item.Status == BulkItemStatus.Importing || item.Status == BulkItemStatus.Converting)
                    && requestIds.Contains(item.RequestId)
                select new { Item = item, request.CorrelationId, request.OwnerActorId })
                .ToListAsync(ct);
            if (owned.Count == 0) return;

            var ids = owned.Select(row => row.Item.Id).ToList();
            await context.BulkScoringItems.Where(item => ids.Contains(item.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, BulkItemStatus.Waiting)
                    .SetProperty(i => i.LeaseOwner, (string?)null)
                    .SetProperty(i => i.LeaseUntil, (DateTime?)null)
                    .SetProperty(i => i.Attempt, i => i.Attempt > 0 ? i.Attempt - 1 : 0), ct);
            foreach (var row in owned)
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkItemLeaseReleased,
                        leaseOwner,
                        row.OwnerActorId,
                        row.CorrelationId,
                        clock.GetUtcNow().UtcDateTime,
                        "bulk_item",
                        row.Item.Id,
                        new { state = BulkItemStatus.Waiting.ToWire(), attempt = Math.Max(0, row.Item.Attempt - 1) }),
                    ensureUnique: false,
                    ct);
            foreach (var requestId in owned.Select(row => row.Item.RequestId).Distinct())
                await RecomputeCountsAsync(requestId, ct);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);
    }

    public Task SaveItemAsync(BulkScoringItem item, CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            var request = await context.BulkScoringRequests.AsNoTracking()
                .Where(candidate => candidate.Id == item.RequestId)
                .Select(candidate => new
                {
                    candidate.CorrelationId,
                    candidate.OwnerActorId,
                })
                .SingleAsync(ct);
            var countsChanged = await PersistItemAsync(item, ct);
            var terminalEventType = item.Status switch
            {
                BulkItemStatus.Completed => BulkAuditEventTypes.BulkItemCompleted,
                BulkItemStatus.Failed => BulkAuditEventTypes.BulkItemFailed,
                BulkItemStatus.Skipped => BulkAuditEventTypes.BulkItemSkipped,
                BulkItemStatus.Cancelled => BulkAuditEventTypes.BulkItemCancelled,
                _ => null,
            };
            if (terminalEventType is not null)
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        terminalEventType,
                        "bulk-item-reconciliation-service",
                        request.OwnerActorId,
                        request.CorrelationId,
                        clock.GetUtcNow().UtcDateTime,
                        "bulk_item",
                        item.Id,
                        new { state = item.Status.ToWire(), attempt = item.Attempt },
                        BulkAuditBoundary.Reconciliation),
                    ensureUnique: true,
                    ct);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);

    public Task SaveItemWithAuditAsync(
        BulkScoringItem item,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            var countsChanged = await PersistItemAsync(item, ct);
            await context.ProcessingEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);

    public Task EnsureItemAuditAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            var countsChanged = await PersistItemAsync(item, ct);
            await AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct);
            await context.SaveChangesAsync(ct);
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }, cancellationToken);

    public Task SaveItemWithAuditAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            var countsChanged = await PersistItemAsync(item, ct);
            await AddAuditIfMissingAsync(auditEvent, IsIdempotencyMarker(auditEvent.EventType), ct);
            await context.SaveChangesAsync(ct);
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }, cancellationToken);

    public Task SaveItemAndContentsWithAuditsAsync(
        BulkScoringItem item,
        IReadOnlyCollection<BulkScoringItemContent> contents,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            var countsChanged = await PersistItemAsync(item, ct);
            foreach (var content in contents)
            {
                content.ValidateInvariants();
                await context.BulkScoringItemContents
                    .Where(existing => existing.ItemId == content.ItemId && existing.Kind == content.Kind)
                    .ExecuteDeleteAsync(ct);
                // A replay must not traverse navigation objects retained from the rolled-back attempt.
                content.Item = null;
                context.Entry(content).State = EntityState.Added;
            }
            foreach (var auditEvent in auditEvents)
                await AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct);
            await context.SaveChangesAsync(ct);
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }, cancellationToken);

    public Task CreateApplicationAndDocumentAsync(
        BulkScoringItem item,
        TalentMatch.Domain.Entities.Application application,
        ApplicationDocument document,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(item.RequestId, async ct =>
        {
            if (!await context.Applications.AnyAsync(existing => existing.Id == application.Id, ct))
                await context.Applications.AddAsync(application, ct);
            if (!await context.ApplicationDocuments.AnyAsync(existing => existing.Id == document.Id, ct))
            {
                document.UploadTimestamp ??= clock.GetUtcNow().UtcDateTime;
                await context.ApplicationDocuments.AddAsync(document, ct);
            }
            if (!string.IsNullOrEmpty(document.ContentBase64)
                && !await context.DocumentBlobs.AnyAsync(existing => existing.DocumentId == document.Id, ct))
            {
                await context.DocumentBlobs.AddAsync(new DocumentBlob
                {
                    DocumentId = document.Id,
                    Content = document.ContentBase64,
                }, ct);
            }
            var countsChanged = await PersistItemAsync(item, ct);
            foreach (var auditEvent in auditEvents)
                await AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct);
            await context.SaveChangesAsync(ct);
            if (countsChanged)
                await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
        }, cancellationToken);

    public Task RemoveContentWithAuditAsync(
        string itemId,
        BulkContentKind kind,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InItemTransactionAsync(itemId, async ct =>
        {
            await context.BulkScoringItemContents
                .Where(content => content.ItemId == itemId && content.Kind == kind)
                .ExecuteDeleteAsync(ct);
            await AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);

    public Task<string> HandoffToScoringAsync(
        BulkScoringItem item,
        ProcessingEvent auditEvent,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        HandoffCoreAsync(item, async ct => { await context.ProcessingEvents.AddAsync(auditEvent, ct); },
            utcNow, cancellationToken);

    public Task<string> HandoffToScoringAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        HandoffCoreAsync(item, ct => AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct),
            utcNow, cancellationToken);

    private async Task<string> HandoffCoreAsync(
        BulkScoringItem item,
        Func<CancellationToken, Task> persistAudit,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var saved = await InRequestTransactionAsync(item.RequestId, async ct =>
        {
            // Retry from committed state, not from the object mutated by a failed attempt.
            var current = await context.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id, ct);
            if (current.HandedOffAt is not null)
            {
                if (string.IsNullOrWhiteSpace(current.ScoringConfigVersionId))
                    throw new InvalidOperationException("The handed-off bulk item has no scoring configuration.");
                return current;
            }
            var currentVersionId = await (
                from request in context.BulkScoringRequests
                join job in context.Jobs on request.JobId equals job.Id
                where request.Id == item.RequestId
                select job.CurrentConfigVersionId ?? request.CapturedScoringConfigVersionId)
                .SingleAsync(ct);

            if (string.IsNullOrWhiteSpace(currentVersionId))
                throw new InvalidOperationException("The job has no current scoring configuration.");

            current.ScoringConfigVersionId = currentVersionId;
            current.TransitionTo(BulkItemStatus.Scoring, utcNow);
            var applicationId = current.ApplicationId
                ?? throw new InvalidOperationException("The bulk item has no application to hand off.");
            await context.Applications
                .Where(application => application.Id == applicationId && application.Status == "Uploading")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(application => application.Status, "Queued")
                    .SetProperty(application => application.UpdatedAt, utcNow), ct);
            if (!await context.Applications.AnyAsync(
                    application => application.Id == applicationId && application.Status == "Queued",
                    ct))
                throw new InvalidOperationException("The uploaded application could not be made ready for scoring.");

            await PersistItemAsync(current, ct);
            await persistAudit(ct);
            await context.SaveChangesAsync(ct);
            await RecomputeCountsAsync(item.RequestId, ct, item.Id);
            await context.SaveChangesAsync(ct);
            context.ChangeTracker.Clear();
            return current;
        }, cancellationToken);
        item.Status = saved.Status;
        item.ScoringConfigVersionId = saved.ScoringConfigVersionId;
        item.HandedOffAt = saved.HandedOffAt;
        item.UpdatedAt = saved.UpdatedAt;
        return saved.ScoringConfigVersionId!;
    }

    public async Task AddContentAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default)
    {
        content.ValidateInvariants();
        await InItemTransactionAsync(content.ItemId, async ct =>
        {
            await context.BulkScoringItemContents
                .Where(c => c.ItemId == content.ItemId && c.Kind == content.Kind)
                .ExecuteDeleteAsync(ct);
            content.Item = null;
            context.Entry(content).State = EntityState.Added;
            await context.SaveChangesAsync(ct);
            context.Entry(content).State = EntityState.Detached;
        }, cancellationToken);
    }

    public Task RemoveContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default) =>
        InItemTransactionAsync(itemId, async ct =>
        {
            await context.BulkScoringItemContents.Where(c => c.ItemId == itemId && c.Kind == kind).ExecuteDeleteAsync(ct);
        }, cancellationToken);

    public async Task<bool> TryClaimFingerprintAsync(string itemId, string fingerprint, CancellationToken cancellationToken = default)
    {
        try
        {
            return await InItemTransactionAsync(itemId, async ct =>
            {
                var item = await context.BulkScoringItems.SingleAsync(i => i.Id == itemId, ct);
                item.Fingerprint = fingerprint;
                item.FingerprintClaimed = true;
                await context.SaveChangesAsync(ct);
                context.Entry(item).State = EntityState.Detached;
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await context.BulkScoringItems.AsNoTracking().AnyAsync(
                item => item.Id != itemId
                    && item.Fingerprint == fingerprint
                    && item.FingerprintClaimed,
                cancellationToken))
                return false;
            throw;
        }
    }

    public async Task<bool> TryClaimFingerprintAsync(
        string itemId,
        string fingerprint,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InItemTransactionAsync(itemId, async ct =>
            {
                var item = await context.BulkScoringItems.SingleAsync(i => i.Id == itemId, ct);
                item.Fingerprint = fingerprint;
                item.FingerprintClaimed = true;
                await context.ProcessingEvents.AddAsync(auditEvent, ct);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await context.BulkScoringItems.AsNoTracking().AnyAsync(
                item => item.Id != itemId
                    && item.Fingerprint == fingerprint
                    && item.FingerprintClaimed,
                cancellationToken))
                return false;
            throw;
        }
    }

    public async Task<bool> TryClaimFingerprintWithAuditAsync(
        string itemId,
        string fingerprint,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await InItemTransactionAsync(itemId, async ct =>
            {
                var item = await context.BulkScoringItems.SingleAsync(i => i.Id == itemId, ct);
                item.Fingerprint = fingerprint;
                item.FingerprintClaimed = true;
                await AddAuditIfMissingAsync(auditEvent, ensureUnique: true, ct);
                await context.SaveChangesAsync(ct);
                context.ChangeTracker.Clear();
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (await context.BulkScoringItems.AsNoTracking().AnyAsync(
                item => item.Id != itemId
                    && item.Fingerprint == fingerprint
                    && item.FingerprintClaimed,
                cancellationToken))
                return false;
            throw;
        }
    }

    public async Task<int> FailNonTerminalItemsAsync(
        string requestId,
        BulkReasonCode reason,
        string message,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        return await InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await context.BulkScoringRequests.AsNoTracking()
                .Where(candidate => candidate.Id == requestId)
                .Select(candidate => new
                {
                    candidate.CorrelationId,
                    candidate.OwnerActorId,
                })
                .SingleAsync(ct);
            var items = await context.BulkScoringItems.AsNoTracking()
                .Where(i => i.RequestId == requestId
                    && i.SourceStoredAt == null
                    && (i.Status == BulkItemStatus.Waiting
                        || i.Status == BulkItemStatus.Importing
                        || i.Status == BulkItemStatus.Converting))
                .Select(item => new { item.Id, item.Attempt })
                .ToListAsync(ct);
            if (items.Count == 0) return 0;

            var ids = items.Select(item => item.Id).ToList();
            var failed = await context.BulkScoringItems
                .Where(item => ids.Contains(item.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, BulkItemStatus.Failed)
                    .SetProperty(i => i.ReasonCode, (BulkReasonCode?)reason)
                    .SetProperty(i => i.ReasonMessage, message)
                    .SetProperty(i => i.LeaseOwner, (string?)null)
                    .SetProperty(i => i.LeaseUntil, (DateTime?)null)
                    .SetProperty(i => i.TerminalAt, (DateTime?)utcNow)
                    .SetProperty(i => i.UpdatedAt, utcNow), ct);
            foreach (var item in items)
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkItemFailed,
                        "bulk-authorization-lapse-service",
                        request.OwnerActorId,
                        request.CorrelationId,
                        utcNow,
                        "bulk_item",
                        item.Id,
                        new
                        {
                            state = BulkItemStatus.Failed.ToWire(),
                            attempt = item.Attempt,
                            reason = reason.ToWire(),
                        }),
                    ensureUnique: true,
                    ct);
            await RecomputeCountsAsync(requestId, ct);
            await context.SaveChangesAsync(ct);
            return failed;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<BulkScoringItem>> GetScoringItemsBatchAsync(int maxItems, CancellationToken cancellationToken = default) =>
        await context.BulkScoringItems.AsNoTracking()
            .Where(i => i.Status == BulkItemStatus.Scoring && i.ApplicationId != null)
            .OrderBy(i => i.UpdatedAt)
            .Take(maxItems)
            .ToListAsync(cancellationToken);

    public Task<BulkScoringRequest?> TryFinalizeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync<BulkScoringRequest?>(requestId, async ct =>
        {
            var request = await context.BulkScoringRequests.SingleOrDefaultAsync(x => x.Id == requestId, ct);
            if (request is not { Status: BulkRequestStatus.Active }) return null;

            var (byStatus, bytes) = await LoadCountsAsync(requestId, ct);
            request.ApplyCounts(byStatus, bytes);
            await AddAuditIfMissingAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.BulkRequestCountsReconciled,
                    BulkScoringCycle.CountReconciliationServiceActor,
                    request.OwnerActorId,
                    request.CorrelationId,
                    utcNow,
                    "bulk_request",
                    request.Id,
                    new
                    {
                        state = "reconciled",
                        request.WaitingCount,
                        request.ImportingCount,
                        request.ConvertingCount,
                        request.ScoringCount,
                        request.CompletedCount,
                        request.FailedCount,
                        request.SkippedCount,
                        request.CancelledCount,
                    }),
                ensureUnique: false,
                ct);
            if (request.WaitingCount + request.ImportingCount + request.ConvertingCount + request.ScoringCount != 0)
            {
                await context.SaveChangesAsync(ct); // persists any corrected count drift
                return null;
            }

            request.Complete(utcNow);
            request.ConcurrencyVersion++;
            await AddAuditIfMissingAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.BulkRequestTerminalFinalized,
                    BulkScoringCycle.FinalizationServiceActor,
                    request.OwnerActorId,
                    request.CorrelationId,
                    utcNow,
                    "bulk_request",
                    request.Id,
                    new
                    {
                        state = request.Status.ToWire(),
                        outcome = request.Outcome?.ToWire(),
                        request.CompletedCount,
                        request.FailedCount,
                        request.SkippedCount,
                        request.CancelledCount,
                    }),
                ensureUnique: true,
                ct);
            await AddAuditIfMissingAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.BulkNotificationCreated,
                    BulkScoringCycle.NotificationServiceActor,
                    request.OwnerActorId,
                    request.CorrelationId,
                    utcNow,
                    "bulk_request",
                    request.Id,
                    new
                    {
                        state = "created",
                        outcome = request.Outcome?.ToWire(),
                    }),
                ensureUnique: true,
                ct);
            await context.SaveChangesAsync(ct);
            return request;
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> ListActiveRequestIdsAsync(CancellationToken cancellationToken = default) =>
        await context.BulkScoringRequests.AsNoTracking()
            .Where(r => r.Status == BulkRequestStatus.Active)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

    public Task<BulkScoringRequest> RequestCancelAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string actorId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await context.BulkScoringRequests.SingleAsync(x => x.Id == requestId, ct);
            if (request.ConcurrencyVersion != expectedConcurrencyVersion) throw new InvalidOperationException(StaleVersion);
            request.RequestCancel(actorId, utcNow);

            // Waiting files stop immediately; importing/converting stop at their next step boundary;
            // files already scoring finish in the existing pipeline.
            var waitingItems = await context.BulkScoringItems.AsNoTracking()
                .Where(i => i.RequestId == requestId && i.Status == BulkItemStatus.Waiting)
                .Select(i => new { i.Id, i.Attempt })
                .ToListAsync(ct);
            await context.BulkScoringItems
                .Where(i => i.RequestId == requestId && i.Status == BulkItemStatus.Waiting)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, BulkItemStatus.Cancelled)
                    .SetProperty(i => i.ReasonCode, (BulkReasonCode?)BulkReasonCode.CancelledByUser)
                    .SetProperty(i => i.TerminalAt, (DateTime?)utcNow)
                    .SetProperty(i => i.UpdatedAt, utcNow), ct);

            foreach (var item in waitingItems)
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkItemCancelled,
                        BulkScoringCycle.WorkerServiceActor,
                        actorId,
                        request.CorrelationId,
                        utcNow,
                        "bulk_item",
                        item.Id,
                        new
                        {
                            state = BulkItemStatus.Cancelled.ToWire(),
                            attempt = item.Attempt,
                            reason = BulkReasonCode.CancelledByUser.ToWire(),
                        }),
                    ensureUnique: true,
                    ct);

            var (byStatus, bytes) = await LoadCountsAsync(requestId, ct);
            request.ApplyCounts(byStatus, bytes);
            await AddAuditIfMissingAsync(
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.BulkRequestCountsReconciled,
                    BulkScoringCycle.CountReconciliationServiceActor,
                    actorId,
                    request.CorrelationId,
                    utcNow,
                    "bulk_request",
                    request.Id,
                    new
                    {
                        state = "reconciled",
                        request.WaitingCount,
                        request.ImportingCount,
                        request.ConvertingCount,
                        request.ScoringCount,
                        request.CompletedCount,
                        request.FailedCount,
                        request.SkippedCount,
                        request.CancelledCount,
                    }),
                ensureUnique: false,
                ct);
            if (request.ImportingCount + request.ConvertingCount + request.ScoringCount == 0)
            {
                request.Complete(utcNow);
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkRequestTerminalFinalized,
                        BulkScoringCycle.FinalizationServiceActor,
                        actorId,
                        request.CorrelationId,
                        utcNow,
                        "bulk_request",
                        request.Id,
                        new
                        {
                            state = request.Status.ToWire(),
                            outcome = request.Outcome?.ToWire(),
                            request.CompletedCount,
                            request.FailedCount,
                            request.SkippedCount,
                            request.CancelledCount,
                        }),
                    ensureUnique: true,
                    ct);
                await AddAuditIfMissingAsync(
                    BulkAuditWriter.Service(
                        BulkAuditEventTypes.BulkNotificationCreated,
                        BulkScoringCycle.NotificationServiceActor,
                        actorId,
                        request.CorrelationId,
                        utcNow,
                        "bulk_request",
                        request.Id,
                        new
                        {
                            state = "created",
                            outcome = request.Outcome?.ToWire(),
                        }),
                    ensureUnique: true,
                    ct);
            }
            request.ConcurrencyVersion++;
            await context.SaveChangesAsync(ct);
            return request;
        }, cancellationToken);

    public Task<BulkScoringRequest> RequestCancelWithAuditAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string actorId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            var request = await RequestCancelAsync(
                requestId,
                expectedConcurrencyVersion,
                actorId,
                utcNow,
                ct);
            await context.BulkAuditEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            return request;
        }, cancellationToken);

    public Task AcknowledgeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default) =>
        context.BulkScoringRequests.Where(x => x.Id == requestId && x.AcknowledgedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.AcknowledgedAt, (DateTime?)utcNow)
                .SetProperty(x => x.AcknowledgedByActorId, x => x.OwnerActorId), cancellationToken);

    public Task AcknowledgeWithAuditAsync(
        string requestId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        InRequestTransactionAsync(requestId, async ct =>
        {
            auditEvent.Validate();
            var updated = await context.BulkScoringRequests
                .Where(x => x.Id == requestId
                    && x.OwnerActorId == auditEvent.ActorId
                    && x.Status == BulkRequestStatus.Completed
                    && x.AcknowledgedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.AcknowledgedAt, (DateTime?)utcNow)
                    .SetProperty(x => x.AcknowledgedByActorId, auditEvent.ActorId), ct);
            if (updated == 1)
            {
                await context.BulkAuditEvents.AddAsync(auditEvent, ct);
                await context.SaveChangesAsync(ct);
            }
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> ListRequestIdsForJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        await context.BulkScoringRequests.AsNoTracking().Where(x => x.JobId == jobId).Select(x => x.Id).ToListAsync(cancellationToken);

    public Task DeleteForJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        InJobTransactionAsync(jobId, async ct =>
        {
            await context.BulkScoringRequests.Where(x => x.JobId == jobId).ExecuteDeleteAsync(ct);
        }, cancellationToken);

    public Task BeginJobCleanupAsync(
        string jobId,
        string deletingActorId,
        string correlationId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        InJobTransactionAsync(jobId, async ct =>
        {
            var requestIds = await context.BulkScoringRequests.AsNoTracking()
                .Where(request => request.JobId == jobId)
                .Select(request => request.Id)
                .ToListAsync(ct);
            foreach (var requestId in requestIds)
                await AddAuditIfMissingAsync(
                    CleanupAudit(
                        BulkAuditEventTypes.BulkContentCleanupReconciliationPending,
                        "bulk-content-cleanup-reconciliation-service",
                        deletingActorId,
                        correlationId,
                        utcNow,
                        "bulk_request",
                        requestId,
                        new { state = "pending" }),
                    ensureUnique: true,
                    ct);

            await AddAuditIfMissingAsync(
                CleanupAudit(
                    BulkAuditEventTypes.BulkJobCleanupPending,
                    "bulk-job-cleanup-service",
                    deletingActorId,
                    correlationId,
                    utcNow,
                    "job",
                    jobId,
                    new { state = "pending", requestCount = requestIds.Count }),
                ensureUnique: true,
                ct);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);

    public Task<bool> CompleteJobCleanupAsync(
        string jobId,
        string deletingActorId,
        string correlationId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        InJobTransactionAsync(jobId, async ct =>
        {
            var requests = await context.BulkScoringRequests.AsNoTracking()
                .Where(request => request.JobId == jobId)
                .Select(request => new { request.Id })
                .ToListAsync(ct);
            var requestIds = requests.Select(request => request.Id).ToList();
            var itemIds = await context.BulkScoringItems.AsNoTracking()
                .Where(item => requestIds.Contains(item.RequestId))
                .Select(item => item.Id)
                .ToListAsync(ct);

            foreach (var requestId in requestIds)
            {
                foreach (var (eventType, contentKind) in new[]
                         {
                             (BulkAuditEventTypes.BulkOriginalContentDeleted, "original"),
                             (BulkAuditEventTypes.BulkConvertedContentDeleted, "converted"),
                             (BulkAuditEventTypes.BulkTransientContentDeleted, "transient"),
                         })
                    await AddAuditIfMissingAsync(
                        CleanupAudit(
                            eventType,
                            "bulk-job-cleanup-service",
                            deletingActorId,
                            correlationId,
                            utcNow,
                            "bulk_request",
                            requestId,
                            new { state = "deleted", contentKind }),
                        ensureUnique: true,
                        ct);

                await AddAuditIfMissingAsync(
                    CleanupAudit(
                        BulkAuditEventTypes.BulkRequestReconciliationCorrected,
                        "bulk-job-cleanup-reconciliation-service",
                        deletingActorId,
                        correlationId,
                        utcNow,
                        "bulk_request",
                        requestId,
                        new { state = "deleted_with_job" }),
                    ensureUnique: true,
                    ct);
                await AddAuditIfMissingAsync(
                    CleanupAudit(
                        BulkAuditEventTypes.BulkContentCleanupReconciliationCompleted,
                        "bulk-content-cleanup-reconciliation-service",
                        deletingActorId,
                        correlationId,
                        utcNow,
                        "bulk_request",
                        requestId,
                        new { state = "completed" }),
                    ensureUnique: true,
                    ct);
            }

            foreach (var itemId in itemIds)
                await AddAuditIfMissingAsync(
                    CleanupAudit(
                        BulkAuditEventTypes.BulkItemReconciliationCorrected,
                        "bulk-job-cleanup-reconciliation-service",
                        deletingActorId,
                        correlationId,
                        utcNow,
                        "bulk_item",
                        itemId,
                        new { state = "deleted_with_job" }),
                    ensureUnique: true,
                    ct);

            await AddAuditIfMissingAsync(
                CleanupAudit(
                    BulkAuditEventTypes.BulkJobCleanupCompleted,
                    "bulk-job-cleanup-service",
                    deletingActorId,
                    correlationId,
                    utcNow,
                    "job",
                    jobId,
                    new
                    {
                        state = "completed",
                        requestCount = requestIds.Count,
                        itemCount = itemIds.Count,
                    }),
                ensureUnique: true,
                ct);
            await context.SaveChangesAsync(ct);

            var deleted = await context.Jobs.Where(job => job.Id == jobId).ExecuteDeleteAsync(ct);
            return deleted == 1;
        }, cancellationToken);

    private static BulkAuditEvent CleanupAudit(
        string eventType,
        string serviceActorId,
        string deletingActorId,
        string correlationId,
        DateTime utcNow,
        string subjectType,
        string subjectId,
        object safeFacts) =>
        BulkAuditWriter.Service(
            eventType,
            serviceActorId,
            deletingActorId,
            correlationId,
            utcNow,
            subjectType,
            subjectId,
            safeFacts,
            BulkAuditBoundary.Reconciliation);

    private IQueryable<string> DueItemIds(string requestId, DateTime utcNow) =>
        context.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId && i.Status == BulkItemStatus.Waiting
                && (i.NextAttemptAt == null || i.NextAttemptAt <= utcNow))
            .OrderBy(i => i.Ordinal)
            .Select(i => i.Id);

    private async Task ApplyStepAsync(BulkScoringRequest request, BulkDiscoveryStepResult step, CancellationToken ct)
    {
        var offered = step.NewItems.GroupBy(i => i.SourceDriveItemId).Select(g => g.First()).ToList();
        var existing = new HashSet<string>();
        if (offered.Count > 0 && context.Entry(request).State != EntityState.Added)
        {
            var ids = offered.Select(i => i.SourceDriveItemId).ToList();
            existing = (await context.BulkScoringItems.AsNoTracking()
                .Where(i => i.RequestId == request.Id && ids.Contains(i.SourceDriveItemId))
                .Select(i => i.SourceDriveItemId).ToListAsync(ct)).ToHashSet();
        }

        var fresh = offered.Where(i => !existing.Contains(i.SourceDriveItemId)).ToList();
        foreach (var item in fresh)
        {
            item.RequestId = request.Id;
            item.Status = BulkItemStatus.Waiting;
        }
        await context.BulkScoringItems.AddRangeAsync(fresh, ct);

        request.DiscoveredCount += fresh.Count;
        request.WaitingCount += fresh.Count;
        request.TotalEligibleBytes += fresh.Sum(i => i.SizeBytes);
        foreach (var (reason, delta) in step.ExcludedTallies)
            if (delta != 0) request.AdjustExcluded(reason, delta);
        request.InaccessibleCount = Math.Max(0, request.InaccessibleCount + step.InaccessibleDelta);
        request.EntriesScanned = Math.Max(0, request.EntriesScanned + step.EntriesScannedDelta);
        request.DiscoveryStepCount++;
        request.DiscoveryStatus = step.Status;
        request.DiscoveryCursorJson = step.CursorJson;
        request.ExpiresAt = step.ExpiresAt;
    }

    private async Task<(Dictionary<BulkItemStatus, int> ByStatus, long Bytes)> LoadCountsAsync(string requestId, CancellationToken ct)
    {
        var rows = await context.BulkScoringItems.AsNoTracking()
            .Where(i => i.RequestId == requestId)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.SizeBytes) })
            .ToListAsync(ct);
        return (rows.ToDictionary(r => r.Status, r => r.Count), rows.Sum(r => r.Bytes));
    }

    private async Task RecomputeCountsAsync(
        string requestId,
        CancellationToken ct,
        string? auditSubjectId = null)
    {
        var (byStatus, bytes) = await LoadCountsAsync(requestId, ct);
        var c = new BulkScoringRequest();
        c.ApplyCounts(byStatus, bytes);
        await context.BulkScoringRequests
            .Where(r => r.Id == requestId && r.Status != BulkRequestStatus.Completed)
            .ExecuteUpdateAsync(s => s
            .SetProperty(r => r.DiscoveredCount, c.DiscoveredCount)
            .SetProperty(r => r.WaitingCount, c.WaitingCount)
            .SetProperty(r => r.ImportingCount, c.ImportingCount)
            .SetProperty(r => r.ConvertingCount, c.ConvertingCount)
            .SetProperty(r => r.ScoringCount, c.ScoringCount)
            .SetProperty(r => r.CompletedCount, c.CompletedCount)
            .SetProperty(r => r.FailedCount, c.FailedCount)
            .SetProperty(r => r.SkippedCount, c.SkippedCount)
            .SetProperty(r => r.CancelledCount, c.CancelledCount)
            .SetProperty(r => r.TotalEligibleBytes, c.TotalEligibleBytes), ct);

        var request = await context.BulkScoringRequests.AsNoTracking()
            .Where(candidate => candidate.Id == requestId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.CorrelationId,
                candidate.OwnerActorId,
            })
            .SingleAsync(ct);
        await AddAuditIfMissingAsync(
            BulkAuditWriter.Service(
                BulkAuditEventTypes.BulkRequestCountsReconciled,
                "bulk-count-reconciliation-service",
                request.OwnerActorId,
                request.CorrelationId,
                clock.GetUtcNow().UtcDateTime,
                auditSubjectId is null ? "bulk_request" : "bulk_item",
                auditSubjectId ?? request.Id,
                new
                {
                    state = "reconciled",
                    c.WaitingCount,
                    c.ImportingCount,
                    c.ConvertingCount,
                    c.ScoringCount,
                    c.CompletedCount,
                    c.FailedCount,
                    c.SkippedCount,
                    c.CancelledCount,
                }),
            ensureUnique: false,
            ct);
    }

    private async Task<bool> PersistItemAsync(BulkScoringItem item, CancellationToken ct)
    {
        var persisted = await context.BulkScoringItems.AsNoTracking().SingleAsync(i => i.Id == item.Id, ct);
        var countsChanged = persisted.Status != item.Status || persisted.SizeBytes != item.SizeBytes;
        var tracked = context.BulkScoringItems.Local.FirstOrDefault(i => i.Id == item.Id) ?? persisted;
        if (context.Entry(tracked).State == EntityState.Detached)
            context.Attach(tracked);
        context.Entry(tracked).CurrentValues.SetValues(item);
        if (persisted.LeaseOwner == item.LeaseOwner && persisted.LeaseUntil > item.LeaseUntil)
            tracked.LeaseUntil = persisted.LeaseUntil;
        return countsChanged;
    }

    private Task InRequestTransactionAsync(
        string requestId, Func<CancellationToken, Task> operation, CancellationToken ct) =>
        InRequestsTransactionAsync([requestId], operation, ct);

    private Task<T> InRequestTransactionAsync<T>(
        string requestId, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        InRequestsTransactionAsync([requestId], operation, ct);

    private Task InRequestsTransactionAsync(
        IReadOnlyCollection<string> requestIds, Func<CancellationToken, Task> operation, CancellationToken ct) =>
        InRequestsTransactionAsync(requestIds, async token => { await operation(token); return true; }, ct);

    private Task<T> InRequestsTransactionAsync<T>(
        IReadOnlyCollection<string> requestIds, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            // Request-first ordering protects exact counts without serializable audit-table scans.
            // No download, conversion, or scoring API call belongs inside this boundary.
            if (context.Database.IsSqlServer())
                foreach (var id in requestIds.Distinct().Order(StringComparer.Ordinal))
                    await context.Database.ExecuteSqlInterpolatedAsync($"""
                        SELECT [Id] FROM [talentmatch].[BulkScoringRequests] WITH (UPDLOCK, ROWLOCK)
                        WHERE [Id] = {id}
                        """, token);
            return await operation(token);
        }, ct, context.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : null);

    private async Task InItemTransactionAsync(
        string itemId, Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        await InItemTransactionAsync(itemId, async token => { await operation(token); return true; }, ct);
    }

    private async Task<T> InItemTransactionAsync<T>(
        string itemId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var requestId = await context.BulkScoringItems.AsNoTracking().Where(i => i.Id == itemId)
            .Select(i => i.RequestId).SingleOrDefaultAsync(ct);
        return await InRequestsTransactionAsync(requestId is null ? [] : [requestId], operation, ct);
    }

    private async Task InJobTransactionAsync(
        string jobId, Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        await InJobTransactionAsync(jobId, async token => { await operation(token); return true; }, ct);
    }

    private async Task<T> InJobTransactionAsync<T>(
        string jobId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var requestIds = await context.BulkScoringRequests.AsNoTracking().Where(r => r.JobId == jobId)
            .Select(r => r.Id).ToListAsync(ct);
        return await InRequestsTransactionAsync(requestIds, operation, ct);
    }

    private async Task AddAuditIfMissingAsync(
        BulkAuditEvent auditEvent,
        bool ensureUnique,
        CancellationToken ct)
    {
        auditEvent.Validate();
        if (ensureUnique
            && (context.BulkAuditEvents.Local.Any(existing =>
                    existing.SubjectId == auditEvent.SubjectId
                    && existing.EventType == auditEvent.EventType)
                || await context.BulkAuditEvents.AsNoTracking().AnyAsync(existing =>
                    existing.SubjectId == auditEvent.SubjectId
                    && existing.EventType == auditEvent.EventType,
                    ct)))
            return;
        await context.BulkAuditEvents.AddAsync(auditEvent, ct);
    }

    private static bool IsIdempotencyMarker(string eventType) =>
        eventType is BulkAuditEventTypes.BulkOriginalContentStaged
            or BulkAuditEventTypes.BulkConvertedContentStaged
            or BulkAuditEventTypes.BulkItemConversionStarted
            or BulkAuditEventTypes.BulkItemConversionCompleted
            or BulkAuditEventTypes.BulkItemApplicationCreated
            or BulkAuditEventTypes.BulkItemDocumentCreated
            or BulkAuditEventTypes.BulkItemScoringHandoffQueued;
}
