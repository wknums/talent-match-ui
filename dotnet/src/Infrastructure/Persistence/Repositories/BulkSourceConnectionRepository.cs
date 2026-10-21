using Microsoft.EntityFrameworkCore;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Domain.Entities.BulkScoringRequests;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class BulkSourceConnectionRepository(AppDbContext context) : IBulkSourceConnectionRepository
{
    public async Task AddAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default)
    {
        await context.BulkSourceConnections.AddAsync(connection, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task AddWithAuditAsync(
        BulkSourceConnection connection,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            await context.BulkSourceConnections.AddAsync(connection, ct);
            await context.BulkAuditEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
        }, cancellationToken);

    public Task<int> CountUnexpiredPendingAsync(
        string productActorId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceConnections.CountAsync(
            connection => connection.ProductActorId == productActorId
                && connection.ExpiresAt > utcNow
                && (connection.Status == BulkSourceConnectionStatus.Authorizing
                    || connection.Status == BulkSourceConnectionStatus.Authorized
                    || connection.Status == BulkSourceConnectionStatus.Verified),
            cancellationToken);

    public Task<BulkSourceConnection?> GetOwnedAsync(
        string connectionId,
        string productActorId,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceConnections.SingleOrDefaultAsync(
            connection => connection.Id == connectionId
                && connection.ProductActorId == productActorId,
            cancellationToken);

    public Task<BulkSourceConnection?> GetByStateHashAsync(
        string stateHash,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceConnections.SingleOrDefaultAsync(
            connection => connection.StateHash == stateHash
                && connection.Status == BulkSourceConnectionStatus.Authorizing,
            cancellationToken);

    public Task<BulkSourceConnection?> GetBoundAsync(
        string connectionId,
        string productActorId,
        string jobId,
        string canonicalUrlHash,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceConnections.SingleOrDefaultAsync(
            connection => connection.Id == connectionId
                && connection.ProductActorId == productActorId
                && connection.JobId == jobId
                && connection.CanonicalUrlHash == canonicalUrlHash,
            cancellationToken);

    public Task<BulkSourceConnection?> GetForTransferAsync(
        string connectionId,
        string productActorId,
        string jobId,
        CancellationToken cancellationToken = default) =>
        context.BulkSourceConnections.SingleOrDefaultAsync(
            connection => connection.Id == connectionId
                && connection.ProductActorId == productActorId
                && connection.JobId == jobId,
            cancellationToken);

    public async Task SaveAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default)
    {
        var entry = context.Entry(connection);
        var priorStatus = entry.State == EntityState.Detached
            ? connection.Status
            : entry.Property(x => x.Status).OriginalValue;
        if (entry.State == EntityState.Detached)
            context.BulkSourceConnections.Update(connection);
        var audit = StatusTransitionAudit(connection, priorStatus);
        if (audit is not null)
            await context.BulkAuditEvents.AddAsync(audit, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> ExpireAsync(
        string connectionId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        MutateTerminalAsync(connectionId, null, null, connection => connection.Expire(utcNow), cancellationToken);

    public Task<bool> AbandonAsync(
        string connectionId,
        string productActorId,
        string jobId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        MutateTerminalAsync(connectionId, productActorId, jobId, connection => connection.Abandon(utcNow), cancellationToken);

    public Task<bool> AbandonWithAuditAsync(
        string connectionId,
        string productActorId,
        string jobId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var connection = await context.BulkSourceConnections.SingleOrDefaultAsync(
                row => row.Id == connectionId
                    && row.ProductActorId == productActorId
                    && row.JobId == jobId,
                ct);
            if (connection is null || !connection.IsPending) return false;
            connection.Abandon(utcNow);
            connection.ConcurrencyVersion++;
            await context.BulkAuditEvents.AddAsync(auditEvent, ct);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    public Task<BulkRequestAuthorization> TransferToRequestAsync(
        string connectionId,
        string productActorId,
        string jobId,
        string canonicalUrlHash,
        string requestId,
        byte[] protectedPayload,
        byte[] wrappedDataKey,
        string keyVersion,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var connection = await GetBoundAsync(connectionId, productActorId, jobId, canonicalUrlHash, ct)
                ?? throw new InvalidOperationException("source_connection_not_found");
            if (connection.Status != BulkSourceConnectionStatus.Verified
                || connection.SourceTenantId is null
                || connection.SourceAccountBindingHash is null
                || connection.SourceDriveId is null
                || connection.SourceFolderItemId is null
                || connection.ProtectedPayload is null
                || connection.WrappedDataKey is null
                || connection.KeyVersion is null)
                throw new InvalidOperationException("source_connection_not_verified");

            if (await context.BulkRequestAuthorizations.AnyAsync(a => a.RequestId == requestId, ct))
                throw new InvalidOperationException("request_authorization_already_exists");

            var authorization = BulkRequestAuthorization.Establish(
                requestId,
                productActorId,
                connection.SourceTenantId,
                connection.SourceAccountBindingHash,
                connection.SourceDriveId,
                connection.SourceFolderItemId,
                protectedPayload,
                wrappedDataKey,
                keyVersion,
                utcNow);
            var requestAlreadyExists = await context.BulkScoringRequests
                .AnyAsync(request => request.Id == requestId, ct);
            if (requestAlreadyExists)
                await context.BulkRequestAuthorizations.AddAsync(authorization, ct);
            connection.Consume(requestId, utcNow);
            connection.ConcurrencyVersion++;
            var correlationId = await context.BulkScoringRequests
                .Where(request => request.Id == requestId)
                .Select(request => request.CorrelationId)
                .SingleOrDefaultAsync(ct)
                ?? await context.BulkAuditEvents
                    .Where(audit => audit.SubjectType == "source_connection"
                        && audit.SubjectId == connectionId
                        && audit.EventType == BulkAuditEventTypes.SourceConnectionCreated)
                    .OrderBy(audit => audit.OccurredAt)
                    .Select(audit => audit.CorrelationId)
                    .FirstAsync(ct);
            await context.BulkAuditEvents.AddRangeAsync(
                [
                    BulkAuditWriter.User(
                        BulkAuditEventTypes.SourceConnectionConsumed,
                        productActorId,
                        correlationId,
                        utcNow,
                        "source_connection",
                        connectionId,
                        new { state = BulkSourceConnectionStatus.Consumed.ToWire() }),
                    BulkAuditWriter.User(
                        BulkAuditEventTypes.RequestAuthorizationEstablished,
                        productActorId,
                        correlationId,
                        utcNow,
                        "request_authorization",
                        requestId,
                        new { state = BulkAuthorizationStatus.Active.ToWire() }),
                ],
                ct);
            if (requestAlreadyExists)
                await context.SaveChangesAsync(ct);
            return authorization;
        }, cancellationToken);

    private Task<bool> MutateTerminalAsync(
        string connectionId,
        string? productActorId,
        string? jobId,
        Action<BulkSourceConnection> mutation,
        CancellationToken cancellationToken) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var connection = await context.BulkSourceConnections.SingleOrDefaultAsync(
                row => row.Id == connectionId
                    && (productActorId == null || row.ProductActorId == productActorId)
                    && (jobId == null || row.JobId == jobId),
                ct);
            if (connection is null || !connection.IsPending) return false;
            var priorStatus = connection.Status;
            mutation(connection);
            connection.ConcurrencyVersion++;
            var audit = StatusTransitionAudit(connection, priorStatus);
            if (audit is not null)
                await context.BulkAuditEvents.AddAsync(audit, ct);
            await context.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    private static BulkAuditEvent? StatusTransitionAudit(
        BulkSourceConnection connection,
        BulkSourceConnectionStatus priorStatus)
    {
        if (connection.Status == priorStatus) return null;
        var correlationId = Guid.NewGuid().ToString();
        var occurredAt = DateTime.SpecifyKind(
            connection.DiscardedAt ?? connection.LastActivityAt,
            DateTimeKind.Utc);
        return connection.Status switch
        {
            BulkSourceConnectionStatus.Authorized or BulkSourceConnectionStatus.Verified =>
                BulkAuditWriter.User(
                    BulkAuditEventTypes.SourceConnectionCompleted,
                    connection.ProductActorId,
                    correlationId,
                    occurredAt,
                    "source_connection",
                    connection.Id,
                    new { state = connection.Status.ToWire() }),
            BulkSourceConnectionStatus.Failed =>
                BulkAuditWriter.User(
                    BulkAuditEventTypes.SourceConnectionFailed,
                    connection.ProductActorId,
                    correlationId,
                    occurredAt,
                    "source_connection",
                    connection.Id,
                    new { state = connection.Status.ToWire(), reasonCode = connection.FailureCode }),
            BulkSourceConnectionStatus.Expired =>
                BulkAuditWriter.Service(
                    BulkAuditEventTypes.SourceConnectionExpired,
                    "bulk-source-connection-expirer",
                    connection.ProductActorId,
                    correlationId,
                    occurredAt,
                    "source_connection",
                    connection.Id,
                    new { state = connection.Status.ToWire() }),
            _ => null,
        };
    }
}
