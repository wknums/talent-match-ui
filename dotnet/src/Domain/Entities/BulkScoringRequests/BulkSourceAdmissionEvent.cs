namespace TalentMatch.Domain.Entities.BulkScoringRequests;

public sealed class BulkSourceAdmissionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public BulkSourceAdmissionKind Kind { get; set; }
    public string SubjectHash { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
