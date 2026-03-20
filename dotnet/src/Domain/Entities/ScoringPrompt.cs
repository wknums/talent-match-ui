using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class ScoringPrompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string Status { get; set; } = "draft"; // draft, active, inactive, production-approved
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
    public string Author { get; set; } = string.Empty;
    public int? Rating { get; set; }
    public string? Comments { get; set; }
    public string Source { get; set; } = "manual"; // manual, imported, generated
    public string? GenerationMetadataJson { get; set; }

    // Navigation properties
    [JsonIgnore]
    public Job? Job { get; set; }
    [JsonIgnore]
    public ICollection<PromptTestRun> TestRuns { get; set; } = new List<PromptTestRun>();
}
