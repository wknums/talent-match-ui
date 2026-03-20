using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class AggregatedResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public double FinalScore { get; set; }
    public string Decision { get; set; } = string.Empty; // Eligible, Excluded, NeedsManualReview
    public double Variance { get; set; }
    public double Confidence { get; set; }
    public string ConsolidatedRationale { get; set; } = string.Empty;
    public string MergedImprovementTipsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [JsonIgnore]
    public Application? Application { get; set; }
}
