using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Interfaces;

// One discovery step, persisted atomically with the request so a crash never leaves partial tallies.
public sealed record BulkDiscoveryStepResult(
    IReadOnlyList<BulkScoringItem> NewItems,
    IReadOnlyDictionary<BulkReasonCode, int> ExcludedTallies,
    int InaccessibleDelta,
    int EntriesScannedDelta,
    BulkDiscoveryStatus Status,
    string? CursorJson,
    DateTime ExpiresAt);

public sealed record BulkItemQuery(
    int Page,
    int PageSize,
    BulkItemStatus? Status = null,
    string? NameContains = null);

public sealed record BulkPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record BulkInclusionTotals(int Count, long Bytes, int CountIfRasterized, long BytesIfRasterized);

public sealed class BulkConfirmationPersistenceException(Exception innerException)
    : Exception(innerException.Message, innerException);

public interface IBulkScoringRepository
{
    // Discovery and confirmation.
    Task<BulkScoringRequest> CreateDraftAsync(
        BulkScoringRequest draft,
        BulkDiscoveryStepResult firstStep,
        CancellationToken cancellationToken = default);

    Task<BulkScoringRequest> ApplyDiscoveryStepAsync(
        string requestId,
        int expectedConcurrencyVersion,
        BulkDiscoveryStepResult step,
        CancellationToken cancellationToken = default);

    // Activates the draft, drops items that are excluded when rasterizing (if requested), and stores the
    // request's delegated authorization in one transaction: no authorization, no active request.
    Task<BulkScoringRequest> ConfirmAsync(
        string requestId,
        int expectedConcurrencyVersion,
        bool convertToRasterPdf,
        bool allowDuplicates,
        string? jobConfigVersionId,
        BulkRequestAuthorization? authorization,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    // Runs authorization transfer, activation/item filtering, and audit persistence inside one
    // transaction. Returning from this method is the durability/queryability boundary (SC-002).
    Task<BulkScoringRequest> ConfirmAtomicallyAsync(
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
        CancellationToken cancellationToken = default);

    Task<BulkScoringRequest> CreateRetryRequestAsync(
        BulkScoringRequest retry,
        IReadOnlyList<BulkScoringItem> items,
        BulkRequestAuthorization? authorization,
        CancellationToken cancellationToken = default);

    Task<BulkScoringRequest> CreateRetryRequestWithAuditAsync(
        BulkScoringRequest retry,
        IReadOnlyList<BulkScoringItem> items,
        BulkRequestAuthorization? authorization,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default) =>
        CreateRetryRequestAsync(retry, items, authorization, cancellationToken);

    Task<bool> DeleteDraftAsync(string requestId, string ownerActorId, CancellationToken cancellationToken = default);
    Task<bool> DeleteDraftWithAuditAsync(
        string requestId,
        string ownerActorId,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        DeleteDraftAsync(requestId, ownerActorId, cancellationToken);

    // Returns the purged request identifiers so callers can discard staged content.
    Task<IReadOnlyList<string>> PurgeExpiredDraftsAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    // Reads, always scoped by an authorized job or owner.
    Task<BulkScoringRequest?> GetAsync(string requestId, CancellationToken cancellationToken = default);

    Task<BulkPage<BulkScoringRequest>> ListByJobAsync(string jobId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkScoringRequest>> ListUnacknowledgedAsync(string ownerActorId, CancellationToken cancellationToken = default);

    Task<BulkPage<BulkScoringItem>> GetItemsAsync(string requestId, BulkItemQuery query, CancellationToken cancellationToken = default);

    Task<BulkScoringItem?> GetItemAsync(string requestId, string itemId, CancellationToken cancellationToken = default);

    Task<bool> HasUsedScoringConfigVersionDifferentAsync(
        string requestId,
        string capturedVersionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<BulkScoringItemContent?> GetContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkScoringItem>> GetRetryableItemsAsync(string requestId, CancellationToken cancellationToken = default);

    // Totals behind the dialog's counts. The "if rasterized" figures exclude files that cannot be converted,
    // so toggling the option needs no rescan.
    Task<BulkInclusionTotals> GetInclusionTotalsAsync(string requestId, CancellationToken cancellationToken = default);

    // After a folder is re-listed: which of these source items are already stored for the request.
    Task<IReadOnlySet<string>> FindExistingSourceItemIdsAsync(
        string requestId,
        IReadOnlyCollection<string> sourceItemIds,
        CancellationToken cancellationToken = default);

    // Worker: claim due items fairly across active requests.
    Task<IReadOnlyList<BulkScoringItem>> ClaimDueItemsAsync(
        string leaseOwner,
        int maxItems,
        DateTime utcNow,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<int> ReclaimExpiredLeasesAsync(DateTime utcNow, CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        string itemId,
        string leaseOwner,
        DateTime utcNow,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task ReleaseLeasesAsync(string leaseOwner, CancellationToken cancellationToken = default);

    // Persists the item and recomputes the request counts in the same transaction.
    Task SaveItemAsync(BulkScoringItem item, CancellationToken cancellationToken = default);

    Task SaveItemWithAuditAsync(
        BulkScoringItem item,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        SaveItemAsync(item, cancellationToken);

    Task AddContentAsync(BulkScoringItemContent content, CancellationToken cancellationToken = default);

    Task RemoveContentAsync(string itemId, BulkContentKind kind, CancellationToken cancellationToken = default);

    // Claims the fingerprint inside the request (filtered unique index); false when already claimed.
    Task<bool> TryClaimFingerprintAsync(string itemId, string fingerprint, CancellationToken cancellationToken = default);

    Task<bool> TryClaimFingerprintAsync(
        string itemId,
        string fingerprint,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        TryClaimFingerprintAsync(itemId, fingerprint, cancellationToken);

    // Lapse: fail every non-terminal item that has not crossed the durable source boundary.
    Task<int> FailNonTerminalItemsAsync(
        string requestId,
        BulkReasonCode reason,
        string message,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BulkScoringItem>> GetScoringItemsBatchAsync(int maxItems, CancellationToken cancellationToken = default);

    // Marks the request completed when every item is terminal and verifies counts (corrects drift).
    Task<BulkScoringRequest?> TryFinalizeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListActiveRequestIdsAsync(CancellationToken cancellationToken = default);

    Task<BulkScoringRequest> RequestCancelAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string actorId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task<BulkScoringRequest> RequestCancelWithAuditAsync(
        string requestId,
        int expectedConcurrencyVersion,
        string actorId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        RequestCancelAsync(requestId, expectedConcurrencyVersion, actorId, utcNow, cancellationToken);

    Task AcknowledgeAsync(string requestId, DateTime utcNow, CancellationToken cancellationToken = default);
    Task AcknowledgeWithAuditAsync(
        string requestId,
        DateTime utcNow,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        AcknowledgeAsync(requestId, utcNow, cancellationToken);

    // Job delete: request identifiers (for blob-prefix deletion) and cascade removal of rows.
    Task<IReadOnlyList<string>> ListRequestIdsForJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task DeleteForJobAsync(string jobId, CancellationToken cancellationToken = default);

    Task BeginJobCleanupAsync(
        string jobId,
        string deletingActorId,
        string correlationId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // Returns true when the provider atomically persisted the final cleanup events and deleted the
    // job (including all feature cascades). The default preserves non-EF test repositories.
    Task<bool> CompleteJobCleanupAsync(
        string jobId,
        string deletingActorId,
        string correlationId,
        DateTime utcNow,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
