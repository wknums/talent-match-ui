using System.Text.Json;
using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.Rubrics.Models;
using TalentMatch.Application.Rubrics.Services;

namespace TalentMatch.Application.JobExtraction.Services;

public sealed class JobSpecExtractionContractValidator
{
    public const string ProtectedContractVersion = "extraction-rubric-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RubricOrderNormalizer _rubricOrderNormalizer;

    public JobSpecExtractionContractValidator(RubricOrderNormalizer rubricOrderNormalizer)
    {
        _rubricOrderNormalizer = rubricOrderNormalizer;
    }

    public ExtractionContractValidationResult Validate(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return ExtractionContractValidationResult.Invalid(
                [
                    new ExtractionValidationFinding(
                        "invalid_json",
                        "error",
                        "$",
                        "The extraction response was empty.")
                ]);
        }

        NormalizedExtractionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<NormalizedExtractionDocument>(rawResponse, JsonOptions);
        }
        catch (JsonException exception)
        {
            return ExtractionContractValidationResult.Invalid(
                [
                    new ExtractionValidationFinding(
                        "invalid_json",
                        "error",
                        "$",
                        $"The extraction response was not valid JSON: {exception.Message}")
                ]);
        }

        if (document is null)
        {
            return ExtractionContractValidationResult.Invalid(
                [
                    new ExtractionValidationFinding(
                        "invalid_json",
                        "error",
                        "$",
                        "The extraction response could not be deserialized.")
                ]);
        }

        var findings = new List<ExtractionValidationFinding>();
        ValidateRequiredRoot(document, findings);
        ValidateRequirements(document.Requirements, findings);
        ValidateRubric(document.Rubric, findings);

        if (findings.Any(finding => string.Equals(finding.Severity, "error", StringComparison.Ordinal)))
            return ExtractionContractValidationResult.Invalid(findings, document);

        var rubric = _rubricOrderNormalizer.Normalize(ToRubricEnvelope(document));
        findings.AddRange(_rubricOrderNormalizer.Validate(rubric));

        return findings.Any(finding => string.Equals(finding.Severity, "error", StringComparison.Ordinal))
            ? ExtractionContractValidationResult.Invalid(findings, document, rubric)
            : ExtractionContractValidationResult.Valid(document, rubric, findings);
    }

    public RubricEnvelopeModel ToRubricEnvelope(NormalizedExtractionDocument document)
    {
        var categories = document.Rubric.Categories
            .Select((category, index) => new RubricCategoryModel
            {
                Id = category.Id.Trim(),
                Name = category.Name.Trim(),
                Weight = category.Weight,
                Description = string.IsNullOrWhiteSpace(category.Description) ? null : category.Description.Trim(),
                Order = index,
            })
            .ToList();

        var items = document.Requirements
            .Select((requirement, index) => new { requirement, index })
            .GroupBy(entry => entry.requirement.CategoryId, StringComparer.Ordinal)
            .SelectMany(group => group.Select((entry, order) => new RubricItemModel
            {
                Id = entry.requirement.Id.Trim(),
                CategoryId = entry.requirement.CategoryId.Trim(),
                Text = entry.requirement.Text.Trim(),
                RequirementType = entry.requirement.RequirementType.Trim(),
                Order = order,
                SourceText = entry.requirement.SourceText.Trim(),
                SourceLocation = string.IsNullOrWhiteSpace(entry.requirement.SourceLocation)
                    ? null
                    : entry.requirement.SourceLocation.Trim(),
                SourceRequirementId = entry.requirement.Id.Trim(),
                ReviewStatus = entry.requirement.NeedsReview
                    ? RubricReviewStatuses.NeedsReview
                    : RubricReviewStatuses.Confirmed,
                CreatedFrom = RubricCreatedFrom.Extracted,
            }))
            .ToList();

        return new RubricEnvelopeModel
        {
            SchemaVersion = RubricSchemaVersions.RubricV2,
            Categories = categories,
            Items = items,
        };
    }

    private static void ValidateRequiredRoot(
        NormalizedExtractionDocument document,
        ICollection<ExtractionValidationFinding> findings)
    {
        if (document.Requirements is null || document.Requirements.Count == 0)
        {
            findings.Add(new ExtractionValidationFinding(
                "missing_requirement",
                "error",
                "$.requirements",
                "At least one extracted requirement is required."));
        }

        if (document.Rubric is null)
        {
            findings.Add(new ExtractionValidationFinding(
                "schema_mismatch",
                "error",
                "$.rubric",
                "A rubric object is required."));
        }
    }

    private static void ValidateRequirements(
        IReadOnlyList<ExtractedRequirementModel>? requirements,
        ICollection<ExtractionValidationFinding> findings)
    {
        if (requirements is null)
            return;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (requirement, index) in requirements.Select((value, index) => (value, index)))
        {
            var path = $"$.requirements[{index}]";
            if (string.IsNullOrWhiteSpace(requirement.Id))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.id", "Requirement id is required."));
            }
            else if (!ids.Add(requirement.Id.Trim()))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.id", $"Requirement id '{requirement.Id}' must be unique."));
            }

            if (string.IsNullOrWhiteSpace(requirement.Text))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.text", "Requirement text is required."));
            }

            if (string.IsNullOrWhiteSpace(requirement.CategoryId))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.category_id", "Requirement category_id is required."));
            }

            if (string.IsNullOrWhiteSpace(requirement.SourceText))
            {
                findings.Add(new ExtractionValidationFinding("missing_source_trace", "error", $"{path}.source_text", "Every requirement must preserve source_text."));
            }

            if (!RubricRequirementTypes.All.Contains(requirement.RequirementType?.Trim() ?? string.Empty))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.requirement_type", $"Unsupported requirement_type '{requirement.RequirementType}'."));
            }

            if (!string.IsNullOrWhiteSpace(requirement.DuplicateOf)
                && string.Equals(requirement.DuplicateOf.Trim(), requirement.Id.Trim(), StringComparison.Ordinal))
            {
                findings.Add(new ExtractionValidationFinding("duplicate_requirement", "warning", $"{path}.duplicate_of", "A requirement cannot be marked as a duplicate of itself."));
            }
        }
    }

    private static void ValidateRubric(
        ExtractedRubricModel? rubric,
        ICollection<ExtractionValidationFinding> findings)
    {
        if (rubric is null)
            return;

        if (rubric.Categories is null || rubric.Categories.Count == 0)
        {
            findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", "$.rubric.categories", "At least one rubric category is required."));
            return;
        }

        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        double weightSum = 0;
        foreach (var (category, index) in rubric.Categories.Select((value, index) => (value, index)))
        {
            var path = $"$.rubric.categories[{index}]";
            if (string.IsNullOrWhiteSpace(category.Id))
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.id", "Rubric category id is required."));
            else if (!categoryIds.Add(category.Id.Trim()))
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.id", $"Rubric category id '{category.Id}' must be unique."));

            if (string.IsNullOrWhiteSpace(category.Name))
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.name", "Rubric category name is required."));

            if (string.IsNullOrWhiteSpace(category.Description))
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.description", "Rubric category description is required."));

            if (!string.Equals(category.Source, RubricCategorySources.Doc, StringComparison.Ordinal)
                && !string.Equals(category.Source, RubricCategorySources.Generated, StringComparison.Ordinal))
            {
                findings.Add(new ExtractionValidationFinding("schema_mismatch", "error", $"{path}.source", $"Unsupported rubric category source '{category.Source}'."));
            }

            weightSum += category.Weight;
        }

        if (!rubric.WeightsSumToOne || Math.Abs(weightSum - 1d) > 0.0001d)
        {
            findings.Add(new ExtractionValidationFinding(
                "invalid_weight_total",
                "error",
                "$.rubric.categories",
                $"Rubric category weights must sum to 1.0 but were {weightSum:F4}."));
        }
    }
}

public sealed record ExtractionContractValidationResult(
    bool IsValid,
    NormalizedExtractionDocument? Document,
    RubricEnvelopeModel? Rubric,
    IReadOnlyList<ExtractionValidationFinding> Findings)
{
    public string ValidationStatus => IsValid ? "valid" : "invalid";

    public static ExtractionContractValidationResult Invalid(
        IReadOnlyList<ExtractionValidationFinding> findings,
        NormalizedExtractionDocument? document = null,
        RubricEnvelopeModel? rubric = null)
        => new(false, document, rubric, findings);

    public static ExtractionContractValidationResult Valid(
        NormalizedExtractionDocument document,
        RubricEnvelopeModel rubric,
        IReadOnlyList<ExtractionValidationFinding> findings)
        => new(true, document, rubric, findings);
}
