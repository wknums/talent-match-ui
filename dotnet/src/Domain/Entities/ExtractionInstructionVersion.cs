namespace TalentMatch.Domain.Entities;

public class ExtractionInstructionVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int VersionNumber { get; set; }
    public string InstructionText { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ReasoningLevel { get; set; } = string.Empty;
    public string ProtectedContractVersion { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public string? ChangeNote { get; set; }
    public string ValidationStatus { get; set; } = "unvalidated";
    public string ValidationFindingsJson { get; set; } = "[]";
    public DateTime? ValidatedAt { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
    public int ConcurrencyVersion { get; set; } = 1;
}
