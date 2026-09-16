using System.Text.Json;
using TalentMatch.Application.ExtractionInstructions.Models;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.ExtractionInstructions;

internal static class ExtractionInstructionMappings
{
    public static ExtractionInstructionVersionModel ToModel(this ExtractionInstructionVersion version)
        => new(
            version.Id,
            version.VersionNumber,
            version.InstructionText,
            version.ProtectedContractVersion,
            version.Status,
            version.ValidationStatus,
            version.ChangeNote,
            ReadFindings(version.ValidationFindingsJson),
            version.CreatedAt,
            version.CreatedBy,
            version.ValidatedAt,
            version.ValidatedBy,
            version.ActivatedAt,
            version.ActivatedBy,
            version.ConcurrencyVersion);

    public static ExtractionInstructionVersionDetailModel ToDetailModel(this ExtractionInstructionVersion version)
        => new(
            version.Id,
            version.VersionNumber,
            version.InstructionText,
            version.ProtectedContractVersion,
            version.Status,
            version.ValidationStatus,
            version.ChangeNote,
            ReadFindings(version.ValidationFindingsJson),
            JsonSerializer.Deserialize<object>(ProtectedContractJson)!,
            version.CreatedAt,
            version.CreatedBy,
            version.ValidatedAt,
            version.ValidatedBy,
            version.ActivatedAt,
            version.ActivatedBy,
            version.ConcurrencyVersion);

    private static List<ExtractionValidationFinding> ReadFindings(string json)
        => JsonSerializer.Deserialize<List<ExtractionValidationFinding>>(string.IsNullOrWhiteSpace(json) ? "[]" : json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    private const string ProtectedContractJson = """
        {
          "version": "extraction-rubric-v1",
          "schemaFile": "specs/001-dynamic-rubric-editor/contracts/extraction-rubric.schema.json",
          "rubricSchemaFile": "specs/001-dynamic-rubric-editor/contracts/rubric-config.schema.json"
        }
        """;
}
