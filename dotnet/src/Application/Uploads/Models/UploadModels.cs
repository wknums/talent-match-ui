using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Uploads.Models;

public sealed record CreateUploadItemRequest(
    Guid OccurrenceKey,
    int Ordinal,
    string FileName,
    string MimeType,
    long RawSizeBytes);

public sealed record CreateUploadSessionRequest(
    bool AllowDuplicates,
    IReadOnlyList<CreateUploadItemRequest> Items);

public sealed record UploadLimitsSnapshotDto(
    int FileConcurrency,
    long MaxIndividualFileBytes,
    long MaxInFlightBytes);

public sealed record UploadAggregateCountsDto(
    int Total,
    int WaitingOrThrottled,
    int ActiveOrRetrying,
    int Succeeded,
    int Skipped,
    int Failed,
    int Interrupted,
    int Terminal);

public sealed record UploadItemDto(
    string Id,
    string SessionId,
    Guid OccurrenceKey,
    int Ordinal,
    string FileName,
    string MimeType,
    long RawSizeBytes,
    string Status,
    int AttemptCount,
    string? ContentFingerprint,
    string? ApplicationId,
    string? OutcomeCode,
    string? OutcomeMessage,
    DateTime? NextRetryAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    int ConcurrencyVersion);

public sealed record UploadSessionSummaryDto(
    string Id,
    string JobId,
    string Status,
    bool AllowDuplicates,
    UploadLimitsSnapshotDto Limits,
    UploadAggregateCountsDto Counts,
    int ProgressPercent,
    string CorrelationId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime LastHeartbeatAt,
    DateTime? CompletedAt,
    int ConcurrencyVersion);

public sealed record UploadSessionDetailDto(
    string Id,
    string JobId,
    string Status,
    bool AllowDuplicates,
    UploadLimitsSnapshotDto Limits,
    UploadAggregateCountsDto Counts,
    int ProgressPercent,
    string CorrelationId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime LastHeartbeatAt,
    DateTime? CompletedAt,
    int ConcurrencyVersion,
    IReadOnlyList<UploadItemDto> Items);

public sealed record UploadSettingsDto(
    int FileConcurrency,
    long MaxIndividualFileBytes,
    long MaxInFlightBytes,
    int ConcurrencyVersion,
    bool Persisted,
    DateTime? UpdatedAt,
    string? UpdatedBy);

public sealed record UpdateUploadSettingsRequest(
    int FileConcurrency,
    long MaxIndividualFileBytes,
    long MaxInFlightBytes,
    int ExpectedConcurrencyVersion);

public sealed record UpdateUploadItemStatusRequest(
    Guid OccurrenceKey,
    string Status,
    int ExpectedConcurrencyVersion,
    string? OutcomeCode = null,
    string? OutcomeMessage = null,
    DateTime? NextRetryAt = null,
    int? TransportAttemptCount = null);

public sealed class UploadValidationException(
    IReadOnlyDictionary<string, string[]> errors,
    string message = "Optional upload validation failed.")
    : InvalidOperationException(message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public static class UploadMappings
{
    public static UploadItemDto ToDto(this UploadItem item) => new(
        item.Id,
        item.SessionId,
        Guid.Parse(item.OccurrenceKey),
        item.Ordinal,
        item.FileName,
        item.MimeType,
        item.RawSizeBytes,
        item.Status.ToWireValue(),
        item.AttemptCount,
        item.ContentFingerprint,
        item.ApplicationId,
        item.OutcomeCode,
        item.OutcomeMessage,
        item.NextRetryAt,
        item.CreatedAt,
        item.UpdatedAt,
        item.CompletedAt,
        item.ConcurrencyVersion);

    public static UploadSessionSummaryDto ToSummaryDto(this UploadSession session) => new(
        session.Id,
        session.JobId,
        session.Status,
        session.AllowDuplicates,
        new(session.FileConcurrency, session.MaxIndividualFileBytes, session.MaxInFlightBytes),
        new(
            session.TotalItemCount,
            session.WaitingCount,
            session.ActiveCount,
            session.SucceededCount,
            session.SkippedCount,
            session.FailedCount,
            session.InterruptedCount,
            session.TerminalItemCount),
        session.ProgressPercent,
        session.CorrelationId,
        session.CreatedAt,
        session.StartedAt,
        session.LastHeartbeatAt,
        session.CompletedAt,
        session.ConcurrencyVersion);

    public static UploadSessionDetailDto ToDetailDto(this UploadSession session)
    {
        var summary = session.ToSummaryDto();
        return new(
            summary.Id,
            summary.JobId,
            summary.Status,
            summary.AllowDuplicates,
            summary.Limits,
            summary.Counts,
            summary.ProgressPercent,
            summary.CorrelationId,
            summary.CreatedAt,
            summary.StartedAt,
            summary.LastHeartbeatAt,
            summary.CompletedAt,
            summary.ConcurrencyVersion,
            session.Items.OrderBy(x => x.Ordinal).Select(x => x.ToDto()).ToArray());
    }
}
