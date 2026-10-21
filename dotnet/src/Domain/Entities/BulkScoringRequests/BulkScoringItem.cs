namespace TalentMatch.Domain.Entities.BulkScoringRequests;

using System.ComponentModel.DataAnnotations.Schema;

public sealed class BulkScoringItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RequestId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string SourceDriveId { get; set; } = string.Empty;
    public string SourceDriveItemId { get; set; } = string.Empty;
    public string SourceETag { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool ExcludedWhenRasterized { get; set; }
    public BulkItemStatus Status { get; set; } = BulkItemStatus.Waiting;
    public BulkReasonCode? ReasonCode { get; set; }
    public string? ReasonMessage { get; set; }
    public string? Fingerprint { get; set; }
    public bool FingerprintClaimed { get; set; }
    public string? ApplicationId { get; private set; }
    public string? DocumentId { get; private set; }
    public BulkScoredVersion? ScoredVersion { get; set; }
    public int? OriginalPageCount { get; set; }
    [NotMapped, Obsolete("Use OriginalPageCount and ConvertedPageCount.")]
    public int? PageCount
    {
        get => OriginalPageCount;
        set => OriginalPageCount = value;
    }
    public int? ConvertedPageCount { get; set; }
    public int? RasterDpiUsed { get; set; }
    public decimal? MinimumPageSsim { get; set; }
    public DateTime? RasterFidelityValidatedAt { get; set; }
    public long? ConvertedSizeBytes { get; set; }
    public DateTime? SourceStoredAt { get; set; }
    public DateTime? ConvertedStoredAt { get; set; }
    public DateTime? ApplicationCreatedAt { get; set; }
    public DateTime? HandedOffAt { get; set; }
    public string? ScoringConfigVersionId { get; set; }
    public int Attempt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? RetryOfItemId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TerminalAt { get; set; }
    public BulkScoringRequest? Request { get; set; }
    public ICollection<BulkScoringItemContent> Contents { get; set; } = new List<BulkScoringItemContent>();

    public bool IsTerminal => Status.IsTerminal();

    // Read-model flag set by queries; not persisted.
    public bool HasRetainedOriginal { get; set; }

    public void TransitionTo(BulkItemStatus next, DateTime utcNow, BulkReasonCode? reason = null, string? message = null)
    {
        if (!BulkStateMachine.CanMove(Status, next))
            throw new InvalidOperationException($"Illegal bulk item transition {Status.ToWire()} -> {next.ToWire()}.");

        if (next is BulkItemStatus.Failed or BulkItemStatus.Skipped && reason is null)
            throw new InvalidOperationException($"A reason is required when an item becomes {next.ToWire()}.");

        Status = next;
        UpdatedAt = utcNow;

        if (next == BulkItemStatus.Cancelled)
            reason ??= BulkReasonCode.CancelledByUser;

        if (next is BulkItemStatus.Failed or BulkItemStatus.Skipped or BulkItemStatus.Cancelled)
        {
            ReasonCode = reason;
            ReasonMessage = message;
        }

        if (next is BulkItemStatus.Waiting)
            ReleaseLease();

        if (next.IsTerminal())
        {
            TerminalAt = utcNow;
            ReleaseLease();
        }

        if (next == BulkItemStatus.Scoring)
            HandedOffAt ??= utcNow;
    }

    // Application and document identities are deterministic and write-once (R8).
    public void LinkApplication(string applicationId, string documentId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(applicationId) || string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Application and document identifiers are required.");

        if (ApplicationId is not null)
        {
            if (ApplicationId == applicationId && DocumentId == documentId) return;
            throw new InvalidOperationException("ApplicationId is write-once.");
        }

        ApplicationId = applicationId;
        DocumentId = documentId;
        ApplicationCreatedAt ??= utcNow;
    }

    // A fresh waiting item for a retry request. The eTag is left empty so the worker records the file's current
    // version when it first reads it; the retry exists to pick up the file as it is now.
    public BulkScoringItem CreateRetry(DateTime utcNow) => new()
    {
        Ordinal = Ordinal,
        SourceDriveId = SourceDriveId,
        SourceDriveItemId = SourceDriveItemId,
        SourceETag = string.Empty,
        FileName = FileName,
        SourcePath = SourcePath,
        MimeType = MimeType,
        SizeBytes = SizeBytes,
        ExcludedWhenRasterized = ExcludedWhenRasterized,
        RetryOfItemId = Id,
        CreatedAt = utcNow,
        UpdatedAt = utcNow,
    };

    public void ReleaseLease()
    {
        LeaseOwner = null;
        LeaseUntil = null;
    }

    public bool IsRetryable => BulkStateMachine.IsRetryableItem(Status, ReasonCode, HandedOffAt);
}
