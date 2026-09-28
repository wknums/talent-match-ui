using TalentMatch.Application.JobExtraction.Models;

namespace TalentMatch.Application.ExtractionInstructions.Models;

public record ExtractionInstructionVersionModel(
    string Id,
    int VersionNumber,
    string InstructionText,
    string ModelId,
    string ReasoningLevel,
    string ProtectedContractVersion,
    string Status,
    string ValidationStatus,
    string? ChangeNote,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ValidatedAt,
    string? ValidatedBy,
    DateTime? ActivatedAt,
    string? ActivatedBy,
    int ConcurrencyVersion);

public sealed record ExtractionInstructionVersionDetailModel(
    string Id,
    int VersionNumber,
    string InstructionText,
    string ModelId,
    string ReasoningLevel,
    string ProtectedContractVersion,
    string Status,
    string ValidationStatus,
    string? ChangeNote,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings,
    object ProtectedContract,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ValidatedAt,
    string? ValidatedBy,
    DateTime? ActivatedAt,
    string? ActivatedBy,
    int ConcurrencyVersion)
    : ExtractionInstructionVersionModel(
        Id,
        VersionNumber,
        InstructionText,
        ModelId,
        ReasoningLevel,
        ProtectedContractVersion,
        Status,
        ValidationStatus,
        ChangeNote,
        ValidationFindings,
        CreatedAt,
        CreatedBy,
        ValidatedAt,
        ValidatedBy,
        ActivatedAt,
        ActivatedBy,
        ConcurrencyVersion);

public sealed record CreateExtractionInstructionDraftRequest(
    string InstructionText,
    string? ChangeNote,
    string ModelId = "o3",
    string ReasoningLevel = "high");

public sealed record ActivateExtractionInstructionRequest(int ExpectedConcurrencyVersion);
