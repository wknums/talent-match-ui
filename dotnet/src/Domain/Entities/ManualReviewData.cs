namespace TalentMatch.Domain.Entities;

public class ManualReviewData
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public string RubricScoresJson { get; set; } = "{}"; // JSON dict
    public string OverallComment { get; set; } = string.Empty;
    public double? AdjustedFinalScore { get; set; }
    public string AuditTrailJson { get; set; } = "[]"; // JSON array of ManualReviewAuditEntry
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    public Application? Application { get; set; }
}
