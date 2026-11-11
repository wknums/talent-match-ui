namespace TalentMatch.Domain.Entities;

public static class UploadSessionStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
}

public sealed class UploadSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string OwnerActorId { get; set; } = string.Empty;
    public string? OwnerDisplayName { get; set; }
    public bool AllowDuplicates { get; set; }
    public string Status { get; set; } = UploadSessionStatuses.Active;
    public int FileConcurrency { get; set; }
    public long MaxIndividualFileBytes { get; set; }
    public long MaxInFlightBytes { get; set; }
    public int TotalItemCount { get; set; }
    public int WaitingCount { get; set; }
    public int ActiveCount { get; set; }
    public int SucceededCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public int InterruptedCount { get; set; }
    public int TerminalItemCount { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int ConcurrencyVersion { get; set; } = 1;
    public Job? Job { get; set; }
    public ICollection<UploadItem> Items { get; set; } = new List<UploadItem>();

    public int ProgressPercent => TotalItemCount == 0
        ? 0
        : (int)Math.Floor((double)TerminalItemCount / TotalItemCount * 100);

    public void ApplyAggregates(IEnumerable<UploadItem> items, DateTime utcNow)
    {
        var materialized = items.ToArray();
        TotalItemCount = materialized.Length;
        WaitingCount = materialized.Count(x => x.Status is UploadItemStatus.Waiting or UploadItemStatus.Throttled);
        ActiveCount = materialized.Count(x => x.Status is UploadItemStatus.Uploading or UploadItemStatus.Retrying);
        SucceededCount = materialized.Count(x => x.Status == UploadItemStatus.Succeeded);
        SkippedCount = materialized.Count(x => x.Status == UploadItemStatus.SkippedDuplicate);
        FailedCount = materialized.Count(x => x.Status == UploadItemStatus.Failed);
        InterruptedCount = materialized.Count(x => x.Status == UploadItemStatus.Interrupted);
        TerminalItemCount = SucceededCount + SkippedCount + FailedCount + InterruptedCount;
        var isComplete = TotalItemCount > 0 && TerminalItemCount == TotalItemCount;
        Status = isComplete ? UploadSessionStatuses.Completed : UploadSessionStatuses.Active;
        CompletedAt = isComplete ? CompletedAt ?? utcNow : null;
    }

    public void ValidateInvariants()
    {
        if (FileConcurrency <= 0 || MaxIndividualFileBytes <= 0 || MaxInFlightBytes < MaxIndividualFileBytes)
            throw new InvalidOperationException("Upload session limits are invalid.");
        if (TotalItemCount <= 0)
            throw new InvalidOperationException("An upload session must contain at least one item.");
        var total = WaitingCount + ActiveCount + SucceededCount + SkippedCount + FailedCount + InterruptedCount;
        if (total != TotalItemCount || TerminalItemCount != SucceededCount + SkippedCount + FailedCount + InterruptedCount)
            throw new InvalidOperationException("Upload session aggregate counts are inconsistent.");
        if ((Status == UploadSessionStatuses.Completed) != (TerminalItemCount == TotalItemCount)
            || (Status == UploadSessionStatuses.Completed && CompletedAt is null))
            throw new InvalidOperationException("Upload session completion is inconsistent.");
    }
}
