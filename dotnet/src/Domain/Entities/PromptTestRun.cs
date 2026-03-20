using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class PromptTestRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string PromptId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending_scoring"; // pending_scoring, scoring, pending_review, approved, rejected
    public string ApplicationIdsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewNotes { get; set; }

    // Navigation properties
    [JsonIgnore]
    public Job? Job { get; set; }
    [JsonIgnore]
    public ScoringPrompt? Prompt { get; set; }
}
