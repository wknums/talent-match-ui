namespace TalentMatch.Domain.Entities;

public class PromptGenerationInstruction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? JobId { get; set; }
    public int VersionNumber { get; set; }
    public string InstructionText { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ReasoningLevel { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public string? ChangeNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
}
