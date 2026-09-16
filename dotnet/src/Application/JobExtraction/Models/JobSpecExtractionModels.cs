using System.Text.Json.Serialization;
using TalentMatch.Application.Rubrics.Models;

namespace TalentMatch.Application.JobExtraction.Models;

public sealed record ExtractionValidationFinding(
    string Code,
    string Severity,
    string Path,
    string Message);

public sealed record JobSpecExtractionResultModel(
    string ExtractionId,
    string InstructionVersionId,
    string ProtectedContractVersion,
    string ValidationStatus,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings,
    string? Title,
    string? JobDescription,
    string? Department,
    string? Organization,
    RubricEnvelopeModel Rubric);

public sealed record JobSpecExtractionFailureModel(
    string ExtractionId,
    string InstructionVersionId,
    string ProtectedContractVersion,
    string ValidationStatus,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings);

public sealed record JobSpecExtractionExecutionResult(
    string ExtractionId,
    string InstructionVersionId,
    string ProtectedContractVersion,
    string ValidationStatus,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings,
    string? Title,
    string? JobDescription,
    string? Department,
    string? Organization,
    RubricEnvelopeModel? Rubric)
{
    public bool IsValid => string.Equals(ValidationStatus, "valid", StringComparison.Ordinal);
}

public sealed class NormalizedExtractionDocument
{
    [JsonPropertyName("job_title")]
    public string? JobTitle { get; set; }

    [JsonPropertyName("job_description")]
    public string? JobDescription { get; set; }

    [JsonPropertyName("department")]
    public string? Department { get; set; }

    [JsonPropertyName("organization")]
    public string? Organization { get; set; }

    [JsonPropertyName("requirements")]
    public List<ExtractedRequirementModel> Requirements { get; set; } = [];

    [JsonPropertyName("rubric")]
    public ExtractedRubricModel Rubric { get; set; } = new();
}

public sealed class ExtractedRequirementModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("requirement_type")]
    public string RequirementType { get; set; } = RubricRequirementTypes.Other;

    [JsonPropertyName("category_id")]
    public string CategoryId { get; set; } = string.Empty;

    [JsonPropertyName("source_text")]
    public string SourceText { get; set; } = string.Empty;

    [JsonPropertyName("source_location")]
    public string? SourceLocation { get; set; }

    [JsonPropertyName("duplicate_of")]
    public string? DuplicateOf { get; set; }

    [JsonPropertyName("needs_review")]
    public bool NeedsReview { get; set; }
}

public sealed class ExtractedRubricModel
{
    [JsonPropertyName("has_rubric_in_doc")]
    public bool HasRubricInDoc { get; set; }

    [JsonPropertyName("categories")]
    public List<ExtractedRubricCategoryModel> Categories { get; set; } = [];

    [JsonPropertyName("weights_sum_to_1_0")]
    public bool WeightsSumToOne { get; set; }
}

public sealed class ExtractedRubricCategoryModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = RubricCategorySources.Generated;
}

public sealed record ExtractDocumentRequestModel(string FileName, string Content, string MimeType);

public sealed record JobSpecExtractionRecordModel(
    string Id,
    string Purpose,
    string InstructionVersionId,
    string ProtectedContractVersion,
    string SourceFileName,
    string SourceMimeType,
    string SourceSha256,
    string RawResponse,
    string? NormalizedResponseJson,
    string ValidationStatus,
    IReadOnlyList<ExtractionValidationFinding> ValidationFindings,
    string? JobId,
    string? JobConfigVersionId,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime CompletedAt,
    string CorrelationId);
