namespace TalentMatch.Domain.Entities;

public class JobSpecExtraction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Purpose { get; set; } = "job_creation";
    public string InstructionVersionId { get; set; } = string.Empty;
    public string ProtectedContractVersion { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceMimeType { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public string? NormalizedResponseJson { get; set; }
    public string ValidationStatus { get; set; } = "invalid";
    public string ValidationFindingsJson { get; set; } = "[]";
    public string? JobId { get; set; }
    public string? JobConfigVersionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();

    public ExtractionInstructionVersion? InstructionVersion { get; set; }
    public Job? Job { get; set; }
}
