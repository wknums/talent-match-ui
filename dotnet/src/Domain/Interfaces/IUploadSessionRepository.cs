using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IUploadSessionRepository
{
    Task<UploadSession> CreateAsync(
        UploadSession session,
        IReadOnlyCollection<UploadItem> items,
        ProcessingEvent createdEvent,
        int expectedSettingsConcurrencyVersion,
        CancellationToken cancellationToken = default);
    Task<UploadSession?> GetOwnedAsync(
        string sessionId,
        string ownerActorId,
        bool includeItems = true,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadSession>> ListOwnedAsync(
        string ownerActorId,
        string? jobId = null,
        bool includeTerminal = true,
        CancellationToken cancellationToken = default);
    Task<UploadItem?> GetItemAsync(
        string sessionId,
        string itemId,
        string ownerActorId,
        CancellationToken cancellationToken = default);
    Task<UploadItem> UpdateItemAsync(
        UploadItem item,
        int expectedConcurrencyVersion,
        IReadOnlyCollection<ProcessingEvent> events,
        CancellationToken cancellationToken = default);
    Task<UploadItem> CompleteItemAsync(
        string sessionId,
        string itemId,
        string ownerActorId,
        string occurrenceKey,
        string fingerprint,
        string fileName,
        string mimeType,
        long rawSizeBytes,
        string contentBase64,
        CancellationToken cancellationToken = default);
    Task<UploadSession> HeartbeatAsync(
        string sessionId,
        string ownerActorId,
        int expectedConcurrencyVersion,
        DateTime utcNow,
        ProcessingEvent heartbeatEvent,
        CancellationToken cancellationToken = default);
    Task<UploadSession?> ReconcileStaleAsync(
        string sessionId,
        string ownerActorId,
        DateTime staleBefore,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
