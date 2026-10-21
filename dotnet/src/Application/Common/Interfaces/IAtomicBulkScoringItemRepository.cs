using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Application.Common.Interfaces;

public interface IAtomicBulkScoringItemRepository
{
    Task EnsureItemAuditAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task SaveItemWithAuditAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task SaveItemAndContentsWithAuditsAsync(
        BulkScoringItem item,
        IReadOnlyCollection<BulkScoringItemContent> contents,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimFingerprintWithAuditAsync(
        string itemId,
        string fingerprint,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task CreateApplicationAndDocumentAsync(
        BulkScoringItem item,
        TalentMatch.Domain.Entities.Application application,
        ApplicationDocument document,
        IReadOnlyCollection<BulkAuditEvent> auditEvents,
        CancellationToken cancellationToken = default);

    Task RemoveContentWithAuditAsync(
        string itemId,
        BulkContentKind kind,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default);

    Task<string> HandoffToScoringAsync(
        BulkScoringItem item,
        BulkAuditEvent auditEvent,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
