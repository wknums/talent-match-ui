namespace TalentMatch.Domain.Entities;

public class Application
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued"; // Use ApplicationStatus enum values
    public double? FinalScore { get; set; }
    public string? FinalDecision { get; set; } // Eligible, Excluded, NeedsManualReview
    public double? Variance { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? TestRunId { get; set; }
    
    // Navigation properties
    public Job? Job { get; set; }
    public PromptTestRun? PromptTestRun { get; set; }
    public ICollection<ApplicationDocument> Documents { get; set; } = new List<ApplicationDocument>();
    public ICollection<ScoringRun> ScoringRuns { get; set; } = new List<ScoringRun>();
    public AggregatedResult? AggregatedResult { get; set; }
    public ManualReviewData? ManualReview { get; set; }
    public ExtractionArtifact? Extraction { get; set; }
}
