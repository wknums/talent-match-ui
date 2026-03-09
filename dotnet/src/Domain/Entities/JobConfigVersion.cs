namespace TalentMatch.Domain.Entities;

public class JobConfigVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public int VersionNumber { get; set; } = 1;
    public string RubricJson { get; set; } = "[]"; // JSON array of RubricCategory
    public string MustHaveCriteriaJson { get; set; } = "[]";
    public string DesiredCriteriaJson { get; set; } = "[]"; // JSON array of { qualification, description }
    public int ScoringRunCount { get; set; } = 3;
    public string AggregationStrategy { get; set; } = "median"; // median, mean, weighted
    public double LonglistThreshold { get; set; } = 70;
    public double ShortlistThreshold { get; set; } = 85;
    public double VarianceThreshold { get; set; } = 15;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public Job? Job { get; set; }
}
