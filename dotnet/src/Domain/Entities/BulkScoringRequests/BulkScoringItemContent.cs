namespace TalentMatch.Domain.Entities.BulkScoringRequests;

public sealed class BulkScoringItemContent
{
    public string ItemId { get; set; } = string.Empty;
    public BulkContentKind Kind { get; set; }
    public string? BlobUri { get; set; }
    public string? ContentBase64 { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletionRequestedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? CleanupReasonCode { get; set; }
    public BulkScoringItem? Item { get; set; }

    // One canonical location per row, following the existing blob-or-database rule.
    public void ValidateInvariants()
    {
        if (string.IsNullOrWhiteSpace(ItemId))
            throw new InvalidOperationException("Content must belong to an item.");
        if (string.IsNullOrEmpty(BlobUri) == string.IsNullOrEmpty(ContentBase64))
            throw new InvalidOperationException("Exactly one of BlobUri or inline content must be set.");
        if (SizeBytes < 0)
            throw new InvalidOperationException("Content size cannot be negative.");
    }
}
