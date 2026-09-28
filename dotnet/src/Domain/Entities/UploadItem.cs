namespace TalentMatch.Domain.Entities;

public enum UploadItemStatus
{
    Waiting,
    Throttled,
    Uploading,
    Retrying,
    Succeeded,
    SkippedDuplicate,
    Failed,
    Interrupted,
}

public static class UploadItemStatusExtensions
{
    public static bool IsTerminal(this UploadItemStatus status) =>
        status is UploadItemStatus.Succeeded
            or UploadItemStatus.SkippedDuplicate
            or UploadItemStatus.Failed
            or UploadItemStatus.Interrupted;

    public static string ToWireValue(this UploadItemStatus status) => status switch
    {
        UploadItemStatus.Waiting => "waiting",
        UploadItemStatus.Throttled => "throttled",
        UploadItemStatus.Uploading => "uploading",
        UploadItemStatus.Retrying => "retrying",
        UploadItemStatus.Succeeded => "succeeded",
        UploadItemStatus.SkippedDuplicate => "skipped_duplicate",
        UploadItemStatus.Failed => "failed",
        UploadItemStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

public sealed class UploadItem
{
    public const int MaxAttempts = 4;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = string.Empty;
    public string OccurrenceKey { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long RawSizeBytes { get; set; }
    public UploadItemStatus Status { get; set; } = UploadItemStatus.Waiting;
    public int AttemptCount { get; set; }
    public string? ContentFingerprint { get; set; }
    public string? ApplicationId { get; private set; }
    public string? OutcomeCode { get; set; }
    public string? OutcomeMessage { get; set; }
    public int? LastHttpStatus { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int ConcurrencyVersion { get; set; } = 1;
    public UploadSession? Session { get; set; }
    public Application? Application { get; set; }

    public bool CanTransitionTo(UploadItemStatus next) => Status switch
    {
        UploadItemStatus.Waiting => next is UploadItemStatus.Throttled or UploadItemStatus.Uploading
            or UploadItemStatus.Failed or UploadItemStatus.Interrupted,
        UploadItemStatus.Throttled => next is UploadItemStatus.Waiting or UploadItemStatus.Uploading
            or UploadItemStatus.Failed or UploadItemStatus.Interrupted,
        UploadItemStatus.Uploading => next is UploadItemStatus.Retrying or UploadItemStatus.Succeeded
            or UploadItemStatus.SkippedDuplicate or UploadItemStatus.Failed or UploadItemStatus.Interrupted,
        UploadItemStatus.Retrying => next is UploadItemStatus.Uploading
            or UploadItemStatus.Failed or UploadItemStatus.Interrupted,
        _ => false,
    };

    public void TransitionTo(
        UploadItemStatus next,
        DateTime utcNow,
        string? outcomeCode = null,
        string? outcomeMessage = null,
        DateTime? nextRetryAt = null)
    {
        if (!CanTransitionTo(next))
            throw new InvalidOperationException($"Illegal upload item transition {Status.ToWireValue()} -> {next.ToWireValue()}.");
        if (next == UploadItemStatus.Uploading)
        {
            if (AttemptCount >= MaxAttempts)
                throw new InvalidOperationException("The upload item attempt limit has been reached.");
            AttemptCount++;
            LastAttemptAt = utcNow;
        }
        Status = next;
        OutcomeCode = outcomeCode;
        OutcomeMessage = outcomeMessage;
        NextRetryAt = next == UploadItemStatus.Retrying ? nextRetryAt : null;
        UpdatedAt = utcNow;
        ConcurrencyVersion++;
        if (next.IsTerminal())
            CompletedAt = utcNow;
    }

    public void LinkApplication(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            throw new ArgumentException("Application ID is required.", nameof(applicationId));
        if (ApplicationId is not null && !string.Equals(ApplicationId, applicationId, StringComparison.Ordinal))
            throw new InvalidOperationException("ApplicationId is write-once.");
        ApplicationId = applicationId;
    }
}
