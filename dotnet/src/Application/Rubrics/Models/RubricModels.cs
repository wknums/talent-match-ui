using System.Text.Json.Serialization;

namespace TalentMatch.Application.Rubrics.Models;

public static class RubricSchemaVersions
{
    public const string RubricV2 = "rubric-v2";
}

public static class RubricCategorySources
{
    public const string Doc = "doc";
    public const string Generated = "generated";
}

public static class RubricRequirementTypes
{
    public const string MustHave = "must_have";
    public const string Desired = "desired";
    public const string Experience = "experience";
    public const string Responsibility = "responsibility";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MustHave,
        Desired,
        Experience,
        Responsibility,
        Other,
    };
}

public static class RubricReviewStatuses
{
    public const string Confirmed = "confirmed";
    public const string NeedsReview = "needs_review";
}

public static class RubricCreatedFrom
{
    public const string Extracted = "extracted";
    public const string Manual = "manual";
    public const string LegacyConversion = "legacy_conversion";
}

public static class RubricSpecialCategoryIds
{
    public const string NeedsReview = "cat-needs-review";
}

public sealed class RubricEnvelopeModel
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = RubricSchemaVersions.RubricV2;

    [JsonPropertyName("legacySourceVersionId")]
    public string? LegacySourceVersionId { get; set; }

    [JsonPropertyName("categories")]
    public List<RubricCategoryModel> Categories { get; set; } = [];

    [JsonPropertyName("items")]
    public List<RubricItemModel> Items { get; set; } = [];
}

public sealed class RubricCategoryModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public sealed class RubricItemModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("categoryId")]
    public string CategoryId { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("requirementType")]
    public string RequirementType { get; set; } = RubricRequirementTypes.Other;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("sourceText")]
    public string? SourceText { get; set; }

    [JsonPropertyName("sourceLocation")]
    public string? SourceLocation { get; set; }

    [JsonPropertyName("sourceRequirementId")]
    public string? SourceRequirementId { get; set; }

    [JsonPropertyName("reviewStatus")]
    public string ReviewStatus { get; set; } = RubricReviewStatuses.Confirmed;

    [JsonPropertyName("createdFrom")]
    public string CreatedFrom { get; set; } = RubricCreatedFrom.Manual;
}

public sealed class LegacyRubricCategoryModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
