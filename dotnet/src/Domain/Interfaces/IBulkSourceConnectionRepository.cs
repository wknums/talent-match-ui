using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Interfaces;

public interface IBulkSourceConnectionRepository
{
    Task AddAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default);
    Task AddWithAuditAsync(
        BulkSourceConnection connection,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        AddAsync(connection, cancellationToken);
    Task<int> CountUnexpiredPendingAsync(
        string productActorId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
    Task<BulkSourceConnection?> GetOwnedAsync(
        string connectionId,
        string productActorId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<BulkSourceConnection?>(null);
    Task<BulkSourceConnection?> GetByStateHashAsync(
        string stateHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<BulkSourceConnection?>(null);
    Task<BulkSourceConnection?> GetBoundAsync(
        string connectionId,
        string productActorId,
        string jobId,
        string canonicalUrlHash,
        CancellationToken cancellationToken = default);
    Task<BulkSourceConnection?> GetForTransferAsync(
        string connectionId,
        string productActorId,
        string jobId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<BulkSourceConnection?>(null);
    Task SaveAsync(BulkSourceConnection connection, CancellationToken cancellationToken = default);
    Task<bool> ExpireAsync(string connectionId, DateTime utcNow, CancellationToken cancellationToken = default);
    Task<bool> AbandonAsync(
        string connectionId,
        string productActorId,
        string jobId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
    Task<bool> AbandonWithAuditAsync(
        string connectionId,
        string productActorId,
        string jobId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        AbandonAsync(connectionId, productActorId, jobId, utcNow, cancellationToken);
    Task<BulkRequestAuthorization> TransferToRequestAsync(
        string connectionId,
        string productActorId,
        string jobId,
        string canonicalUrlHash,
        string requestId,
        byte[] protectedPayload,
        byte[] wrappedDataKey,
        string keyVersion,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

public interface IBulkRequestAuthorizationRepository
{
    Task<BulkRequestAuthorization?> GetAsync(string requestId, CancellationToken cancellationToken = default);
    Task SaveAsync(BulkRequestAuthorization authorization, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListActiveObtainedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListDiscardableAsync(CancellationToken cancellationToken = default);
}

public interface IBulkSourceAdmissionRepository
{
    Task<bool> TryAdmitConnectionStartAsync(
        string actorSubjectHash,
        string ipSubjectHash,
        DateTime utcNow,
        int actorStartsPerMinute,
        int ipStartsPerMinute,
        CancellationToken cancellationToken = default);
    Task<bool> TryAdmitCallbackAsync(
        string ipSubjectHash,
        DateTime utcNow,
        int callbacksPerMinute,
        CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredAsync(DateTime utcNow, CancellationToken cancellationToken = default);
}
