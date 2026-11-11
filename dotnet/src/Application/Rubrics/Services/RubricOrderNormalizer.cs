using TalentMatch.Application.JobExtraction.Models;
using TalentMatch.Application.Rubrics.Models;

namespace TalentMatch.Application.Rubrics.Services;

public sealed class RubricOrderNormalizer
{
    public RubricEnvelopeModel Normalize(RubricEnvelopeModel envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var categories = envelope.Categories
            .OrderBy(category => category.Order)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .Select((category, index) => new RubricCategoryModel
            {
                Id = NormalizeRequired(category.Id),
                Name = NormalizeRequired(category.Name),
                Weight = category.Weight,
                Description = NormalizeOptional(category.Description),
                Order = index,
            })
            .ToList();

        var items = envelope.Items
            .Select(item => new RubricItemModel
            {
                Id = NormalizeRequired(item.Id),
                CategoryId = NormalizeRequired(item.CategoryId),
                Text = NormalizeRequired(item.Text),
                RequirementType = NormalizeRequired(item.RequirementType),
                Order = item.Order,
                SourceText = NormalizeOptional(item.SourceText),
                SourceLocation = NormalizeOptional(item.SourceLocation),
                SourceRequirementId = NormalizeOptional(item.SourceRequirementId),
                ReviewStatus = NormalizeRequired(item.ReviewStatus),
                CreatedFrom = NormalizeRequired(item.CreatedFrom),
            })
            .ToList();

        foreach (var group in items
                     .GroupBy(item => item.CategoryId, StringComparer.Ordinal)
                     .OrderBy(group => categories.FindIndex(category => category.Id == group.Key)))
        {
            var ordered = group
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = 0; index < ordered.Count; index++)
                ordered[index].Order = index;
        }

        return new RubricEnvelopeModel
        {
            SchemaVersion = RubricSchemaVersions.RubricV2,
            LegacySourceVersionId = NormalizeOptional(envelope.LegacySourceVersionId),
            Categories = categories,
            Items = items,
        };
    }

    public IReadOnlyList<ExtractionValidationFinding> Validate(RubricEnvelopeModel envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var findings = new List<ExtractionValidationFinding>();
        if (!string.Equals(envelope.SchemaVersion, RubricSchemaVersions.RubricV2, StringComparison.Ordinal))
        {
            findings.Add(new ExtractionValidationFinding(
                "schema_mismatch",
                "error",
                "$.schemaVersion",
                $"Rubric schemaVersion must be '{RubricSchemaVersions.RubricV2}'."));
        }

        if (envelope.Categories.Count == 0)
        {
            findings.Add(new ExtractionValidationFinding(
                "schema_mismatch",
                "error",
                "$.categories",
                "At least one rubric category is required."));
            return findings;
        }

        var categoryIds = envelope.Categories.Select(category => category.Id).ToList();
        if (categoryIds.Count != categoryIds.Distinct(StringComparer.Ordinal).Count())
        {
            findings.Add(new ExtractionValidationFinding(
                "schema_mismatch",
                "error",
                "$.categories",
                "Rubric category IDs must be unique."));
        }

        var itemIds = envelope.Items.Select(item => item.Id).ToList();
        if (itemIds.Count != itemIds.Distinct(StringComparer.Ordinal).Count())
        {
            findings.Add(new ExtractionValidationFinding(
                "schema_mismatch",
                "error",
                "$.items",
                "Rubric item IDs must be unique."));
        }

        var weightSum = envelope.Categories.Sum(category => category.Weight);
        if (Math.Abs(weightSum - 1d) > 0.0001d)
        {
            findings.Add(new ExtractionValidationFinding(
                "invalid_weight_total",
                "error",
                "$.categories",
                $"Rubric category weights must sum to 1.0 but were {weightSum:F4}."));
        }

        foreach (var item in envelope.Items)
        {
            if (!categoryIds.Contains(item.CategoryId, StringComparer.Ordinal)
                && !string.Equals(item.CategoryId, RubricSpecialCategoryIds.NeedsReview, StringComparison.Ordinal))
            {
                findings.Add(new ExtractionValidationFinding(
                    "unassigned_requirement",
                    "error",
                    "$.items",
                    $"Rubric item '{item.Id}' references unknown category '{item.CategoryId}'."));
            }
        }

        return findings;
    }

    private static string NormalizeRequired(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Trim();

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim();
}
