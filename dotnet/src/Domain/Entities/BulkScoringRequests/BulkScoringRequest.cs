using System.Text.Json;
using System.ComponentModel.DataAnnotations.Schema;

namespace TalentMatch.Domain.Entities.BulkScoringRequests;

public sealed class BulkScoringRequest
{
    [Obsolete("Compatibility-only projection for pre-v3 tests; delegated v3 persistence has no owning stack.")]
    public const string StackA = "A";
    [Obsolete("Compatibility-only projection for pre-v3 tests; delegated v3 persistence has no owning stack.")]
    public const string StackB = "B";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string OwnerActorId { get; set; } = string.Empty;
    public BulkRequestStatus Status { get; set; } = BulkRequestStatus.Draft;
    public BulkRequestOutcome? Outcome { get; private set; }
    public BulkSourceKind SourceKind { get; set; }
    public string SourceTenantId { get; set; } = string.Empty;
    public string SourceAccountBindingHash { get; set; } = string.Empty;
    public string SourceDriveId { get; set; } = string.Empty;
    public string SourceFolderItemId { get; set; } = string.Empty;
    public string SourceFolderName { get; set; } = string.Empty;
    public string SourceFolderPath { get; set; } = string.Empty;
    public string SourceWebUrl { get; set; } = string.Empty;
    public bool ConvertToRasterPdf { get; private set; }
    public bool AllowDuplicates { get; private set; }
    public int RasterDpi { get; set; }
    public string? CapturedScoringConfigVersionId { get; set; }
    [NotMapped, Obsolete("Use CapturedScoringConfigVersionId.")]
    public string? JobConfigVersionId
    {
        get => CapturedScoringConfigVersionId;
        set => CapturedScoringConfigVersionId = value;
    }
    [NotMapped, Obsolete("Pre-v3 history only; delegated v3 has one access model.")]
    public SharePointAccessModel AccessModel { get; set; } = SharePointAccessModel.SameTenant;
    [NotMapped, Obsolete("Pre-v3 history only; delegated v3 has no owning stack selector.")]
    public string? OwningStack { get; set; }
    [NotMapped, Obsolete("Use the frozen typed limit columns.")]
    public string LimitsJson { get; set; } = "{}";
    public int MaxFilesPerRequest { get; set; }
    public long MaxBytesPerFile { get; set; }
    public long MaxTotalBytes { get; set; }
    public int DiscoveryStepMaxFiles { get; set; }
    public int DiscoveryStepMaxEntries { get; set; }
    public int DiscoveryStepMaxSeconds { get; set; }
    public BulkDiscoveryStatus DiscoveryStatus { get; set; } = BulkDiscoveryStatus.MoreAvailable;
    public string? DiscoveryCursorJson { get; set; }
    public int EntriesScanned { get; set; }
    public int DiscoveryStepCount { get; set; }
    public int DraftReviewVersion { get; set; } = 1;
    public int IncludedItemCount { get; set; }
    public long IncludedBytes { get; set; }
    public string ExcludedByReasonJson { get; set; } = "{}";
    public string? RetryOfRequestId { get; set; }
    public string? ConnectionId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? CancelRequestedAt { get; private set; }
    public string? CancelledByActorId { get; private set; }
    public DateTime? TerminalAt { get; private set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedByActorId { get; set; }
    public int ConcurrencyVersion { get; set; } = 1;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();

    public int DiscoveredCount { get; set; }
    public int ExcludedCount { get; set; }
    public int InaccessibleCount { get; set; }
    public int WaitingCount { get; set; }
    public int ImportingCount { get; set; }
    public int ConvertingCount { get; set; }
    public int ScoringCount { get; set; }
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int CancelledCount { get; set; }
    public long TotalEligibleBytes { get; set; }

    public Job? Job { get; set; }
    public BulkRequestAuthorization? Authorization { get; set; }
    public ICollection<BulkScoringItem> Items { get; set; } = new List<BulkScoringItem>();

    public bool CancelRequested => CancelRequestedAt is not null;

    // Options are chosen while the request is a draft and are immutable once active (FR-010E).
    public void SetOptions(bool convertToRasterPdf, bool allowDuplicates)
    {
        if (Status != BulkRequestStatus.Draft)
            throw new InvalidOperationException("Conversion and duplicate options cannot change after confirmation.");
        ConvertToRasterPdf = convertToRasterPdf;
        AllowDuplicates = allowDuplicates;
    }

    public bool IsExpired(DateTime utcNow) => Status == BulkRequestStatus.Draft && ExpiresAt is { } e && e <= utcNow;

    public void EnsureCanDiscoverMore(DateTime utcNow)
    {
        if (Status != BulkRequestStatus.Draft)
            throw new InvalidOperationException("Discovery can only continue on a draft.");
        if (IsExpired(utcNow))
            throw new InvalidOperationException("The draft has expired.");
        if (DiscoveryStatus != BulkDiscoveryStatus.MoreAvailable)
            throw new InvalidOperationException("Discovery has already finished.");
    }

    public void Confirm(bool convertToRasterPdf, bool allowDuplicates, string? scoringConfigVersionId, DateTime utcNow)
    {
        if (Status != BulkRequestStatus.Draft)
            throw new InvalidOperationException("Only a draft can be confirmed.");
        if (IsExpired(utcNow))
            throw new InvalidOperationException("The draft has expired.");
        if (DiscoveredCount <= 0)
            throw new InvalidOperationException("A request needs at least one included file.");

        SetOptions(convertToRasterPdf, allowDuplicates);
        Move(BulkRequestStatus.Active);
        ConfirmedAt = utcNow;
        ExpiresAt = null;
        CapturedScoringConfigVersionId = scoringConfigVersionId;
    }

    // A retry is a new, already-accepted request over the retryable files of a finished one. It inherits the
    // conversion and duplicate choices and the limits; the source request is never changed.
    public static BulkScoringRequest CreateRetry(
        BulkScoringRequest source, string ownerActorId, string? scoringConfigVersionId, string correlationId, DateTime utcNow)
    {
        if (source.Status != BulkRequestStatus.Completed)
            throw new InvalidOperationException("Only a finished request can be retried.");

        return new BulkScoringRequest
        {
            JobId = source.JobId,
            OwnerActorId = ownerActorId,
            Status = BulkRequestStatus.Active,
            SourceKind = source.SourceKind,
            SourceTenantId = source.SourceTenantId,
            SourceAccountBindingHash = source.SourceAccountBindingHash,
            SourceDriveId = source.SourceDriveId,
            SourceFolderItemId = source.SourceFolderItemId,
            SourceFolderName = source.SourceFolderName,
            SourceFolderPath = source.SourceFolderPath,
            SourceWebUrl = source.SourceWebUrl,
            ConvertToRasterPdf = source.ConvertToRasterPdf,
            AllowDuplicates = source.AllowDuplicates,
            RasterDpi = source.RasterDpi,
            CapturedScoringConfigVersionId = scoringConfigVersionId,
            MaxFilesPerRequest = source.MaxFilesPerRequest,
            MaxBytesPerFile = source.MaxBytesPerFile,
            MaxTotalBytes = source.MaxTotalBytes,
            DiscoveryStepMaxFiles = source.DiscoveryStepMaxFiles,
            DiscoveryStepMaxEntries = source.DiscoveryStepMaxEntries,
            DiscoveryStepMaxSeconds = source.DiscoveryStepMaxSeconds,
            DiscoveryStatus = BulkDiscoveryStatus.Complete,
            RetryOfRequestId = source.Id,
            CreatedAt = utcNow,
            ConfirmedAt = utcNow,
            CorrelationId = correlationId,
        };
    }

    public void RequestCancel(string actorId, DateTime utcNow)
    {
        if (Status != BulkRequestStatus.Active)
            throw new InvalidOperationException("Only an active request can be cancelled.");
        if (CancelRequestedAt is not null) return;
        CancelRequestedAt = utcNow;
        CancelledByActorId = actorId;
    }

    public void Complete(DateTime utcNow)
    {
        if (Status != BulkRequestStatus.Active)
            throw new InvalidOperationException("Only an active request can complete.");
        if (WaitingCount + ImportingCount + ConvertingCount + ScoringCount != 0)
            throw new InvalidOperationException("A request completes only when every included item is terminal.");

        Outcome = ComputeOutcome();
        Move(BulkRequestStatus.Completed);
        TerminalAt = Utc(utcNow);
    }

    public bool TryAcknowledge(string actorId, DateTime utcNow)
    {
        if (Status != BulkRequestStatus.Completed)
            throw new InvalidOperationException("Only a terminal request can be acknowledged.");
        if (!string.Equals(actorId, OwnerActorId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Only the request owner can acknowledge its notification.");
        if (AcknowledgedAt is not null) return false;
        AcknowledgedAt = utcNow;
        AcknowledgedByActorId = actorId;
        return true;
    }

    // Skipped duplicates are intentional and are not failures.
    public BulkRequestOutcome ComputeOutcome()
    {
        if (CancelRequested) return BulkRequestOutcome.Cancelled;
        if (FailedCount == 0 && CancelledCount == 0) return BulkRequestOutcome.AllSucceeded;
        return CompletedCount == 0 ? BulkRequestOutcome.Failed : BulkRequestOutcome.Partial;
    }

    public void ApplyItemCounts(IEnumerable<BulkScoringItem> items)
    {
        var materialized = items.ToArray();
        ApplyCounts(
            materialized.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count()),
            materialized.Sum(x => x.SizeBytes));
    }

    // Used by repositories that aggregate in SQL instead of loading every item.
    public void ApplyCounts(IReadOnlyDictionary<BulkItemStatus, int> byStatus, long totalEligibleBytes)
    {
        // Outcome and terminal counts form one retained notification snapshot. Once the first
        // terminal transition has committed, delayed reconciliation must not rewrite that snapshot.
        if (Status == BulkRequestStatus.Completed)
            return;

        int Of(BulkItemStatus s) => byStatus.GetValueOrDefault(s);
        DiscoveredCount = byStatus.Values.Sum();
        WaitingCount = Of(BulkItemStatus.Waiting);
        ImportingCount = Of(BulkItemStatus.Importing);
        ConvertingCount = Of(BulkItemStatus.Converting);
        ScoringCount = Of(BulkItemStatus.Scoring);
        CompletedCount = Of(BulkItemStatus.Completed);
        FailedCount = Of(BulkItemStatus.Failed);
        SkippedCount = Of(BulkItemStatus.Skipped);
        CancelledCount = Of(BulkItemStatus.Cancelled);
        TotalEligibleBytes = totalEligibleBytes;
    }

    // A re-listed folder first reverses its earlier contribution, so deltas are signed and never go below zero.
    public void AdjustExcluded(BulkReasonCode reason, int delta)
    {
        if (!BulkExclusionCategories.IsExclusionCategory(reason))
            throw new ArgumentException($"'{reason.ToWire()}' is not an exclusion category.", nameof(reason));

        var tallies = GetExcludedByReason().ToDictionary(x => x.Key.ToWire(), x => x.Value);
        tallies[reason.ToWire()] = Math.Max(0, tallies.GetValueOrDefault(reason.ToWire()) + delta);
        ExcludedByReasonJson = JsonSerializer.Serialize(tallies);
        ExcludedCount = tallies.Values.Sum();
    }

    public IReadOnlyDictionary<BulkReasonCode, int> GetExcludedByReason()
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(ExcludedByReasonJson) ?? [];
        var result = new Dictionary<BulkReasonCode, int>();
        foreach (var (key, count) in raw)
            if (WireEnum.TryParse<BulkReasonCode>(key, out var code)) result[code] = count;
        return result;
    }

    public void AddExcluded(BulkReasonCode reason, int count)
    {
        if (!BulkExclusionCategories.IsExclusionCategory(reason))
            throw new ArgumentException($"'{reason.ToWire()}' is not an exclusion category.", nameof(reason));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        var tallies = GetExcludedByReason().ToDictionary(x => x.Key.ToWire(), x => x.Value);
        tallies[reason.ToWire()] = tallies.GetValueOrDefault(reason.ToWire()) + count;
        ExcludedByReasonJson = JsonSerializer.Serialize(tallies);
        ExcludedCount += count;
    }

    public void ValidateInvariants()
    {
        var itemTotal = WaitingCount + ImportingCount + ConvertingCount + ScoringCount
            + CompletedCount + FailedCount + SkippedCount + CancelledCount;
        if (DiscoveredCount < 0 || ExcludedCount < 0 || InaccessibleCount < 0 || itemTotal != DiscoveredCount)
            throw new InvalidOperationException("Bulk scoring request counts are inconsistent.");
        if ((Outcome is not null) != (Status == BulkRequestStatus.Completed))
            throw new InvalidOperationException("Outcome is set exactly when the request is completed.");
        if (IncludedItemCount < 0 || IncludedItemCount > MaxFilesPerRequest
            || IncludedBytes < 0 || IncludedBytes > MaxTotalBytes)
            throw new InvalidOperationException("Frozen request limits are inconsistent.");
        if ((AcknowledgedAt is null) != (AcknowledgedByActorId is null))
            throw new InvalidOperationException("Notification acknowledgment actor and time are written together.");
        if (AcknowledgedByActorId is not null
            && !string.Equals(AcknowledgedByActorId, OwnerActorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the request owner can acknowledge the notification.");
    }

    private void Move(BulkRequestStatus next)
    {
        if (!BulkStateMachine.CanMove(Status, next))
            throw new InvalidOperationException($"Illegal request transition {Status.ToWire()} -> {next.ToWire()}.");
        Status = next;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
