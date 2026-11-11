using System.Text.Json.Serialization;

namespace TalentMatch.Domain.Entities;

public class ExtractionArtifact
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApplicationId { get; set; } = string.Empty;
    public string NormalisedText { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string Status { get; set; } = "pending"; // pending, completed, failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    [JsonIgnore]
    public Application? Application { get; set; }
}
