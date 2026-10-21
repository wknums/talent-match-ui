using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Entities.BulkScoringRequests;

namespace TalentMatch.Domain.Interfaces;

public interface IBulkImportSettingsRepository
{
    Task<BulkImportSettings?> GetPersistedAsync(CancellationToken cancellationToken = default);

    // Fails with a concurrency conflict when expectedConcurrencyVersion is stale.
    Task<BulkImportSettings> SaveAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        CancellationToken cancellationToken = default);

    Task<BulkImportSettings> SaveWithAuditAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        ProcessingEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Atomic bulk settings audit persistence is not supported by this repository.");

    Task<BulkImportSettings> SaveWithAuditAsync(
        BulkImportSettings settings,
        int expectedConcurrencyVersion,
        ProcessingEvent compatibilityEvent,
        BulkAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        SaveWithAuditAsync(settings, expectedConcurrencyVersion, compatibilityEvent, cancellationToken);
}
