namespace TalentMatch.Domain.Entities;

public class ScoringRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public int RunIndex { get; set; }
    public double TotalScore { get; set; }
    public string CategoryScoresJson { get; set; } = "{}"; // JSON serialized Dictionary<string, double>
    public string MustHaveEvaluationJson { get; set; } = "{}"; // JSON serialized
    public string EvidenceCitationsJson { get; set; } = "[]"; // JSON serialized
    public string ImprovementTipsJson { get; set; } = "[]"; // JSON serialized
    public string AiModelId { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public Application? Application { get; set; }
}
