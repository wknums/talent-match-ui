using System.Text.Json;
using TalentMatch.Application.Rubrics.Models;

namespace TalentMatch.Application.Rubrics.Services;

public sealed class LegacyRubricAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly RubricOrderNormalizer _rubricOrderNormalizer;

    public LegacyRubricAdapter(RubricOrderNormalizer rubricOrderNormalizer)
    {
        _rubricOrderNormalizer = rubricOrderNormalizer;
    }

    public bool IsRubricV2Json(string? rubricJson)
    {
        if (string.IsNullOrWhiteSpace(rubricJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(rubricJson);
            return RubricJsonReader.IsV2(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public RubricEnvelopeModel? TryParseRubricEnvelope(string? rubricJson)
    {
        if (!IsRubricV2Json(rubricJson))
            return null;

        try
        {
            var envelope = JsonSerializer.Deserialize<RubricEnvelopeModel>(rubricJson!, JsonOptions);
            return envelope is null ? null : _rubricOrderNormalizer.Normalize(envelope);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<LegacyRubricCategoryModel> ParseLegacyCategories(string? legacyRubricJson)
    {
        if (string.IsNullOrWhiteSpace(legacyRubricJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<LegacyRubricCategoryModel>>(legacyRubricJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public RubricEnvelopeModel CreateLegacyConversionProposal(
        string? legacyRubricJson,
        string? mustHavesJson,
        string? desiredCriteriaJson,
        string legacySourceVersionId)
    {
        var legacyCategories = ParseLegacyCategories(legacyRubricJson);
        var mustHaveLookup = ParseMustHaves(mustHavesJson);
        var desiredLookup = ParseDesiredCriteria(desiredCriteriaJson);

        var envelope = new RubricEnvelopeModel
        {
            SchemaVersion = RubricSchemaVersions.RubricV2,
            LegacySourceVersionId = legacySourceVersionId,
            Categories = legacyCategories
                .Select((category, index) => new RubricCategoryModel
                {
                    Id = Slugify($"cat-{category.Name}"),
                    Name = category.Name.Trim(),
                    Weight = category.Weight,
                    Description = string.IsNullOrWhiteSpace(category.Description)
                        ? "Legacy category preserved during conversion."
                        : category.Description.Trim(),
                    Order = index,
                })
                .ToList(),
        };

        var items = new List<RubricItemModel>();
        foreach (var category in envelope.Categories)
        {
            var legacyCategory = legacyCategories.First(candidate => candidate.Name.Trim() == category.Name);
            var sourceItems = SplitLegacyRequirements(legacyCategory.Description);
            if (sourceItems.Count == 0)
                sourceItems.Add(category.Name);

            foreach (var sourceItem in sourceItems)
            {
                var requirementType = mustHaveLookup.Contains(sourceItem)
                    ? RubricRequirementTypes.MustHave
                    : desiredLookup.Contains(sourceItem)
                        ? RubricRequirementTypes.Desired
                        : RubricRequirementTypes.Other;

                items.Add(new RubricItemModel
                {
                    Id = Slugify($"legacy-item-{category.Id}-{items.Count + 1}"),
                    CategoryId = category.Id,
                    Text = sourceItem,
                    RequirementType = requirementType,
                    Order = items.Count(item => item.CategoryId == category.Id),
                    SourceText = sourceItem,
                    SourceLocation = null,
                    SourceRequirementId = null,
                    ReviewStatus = RubricReviewStatuses.Confirmed,
                    CreatedFrom = RubricCreatedFrom.LegacyConversion,
                });
            }
        }

        envelope.Items = items;
        return _rubricOrderNormalizer.Normalize(envelope);
    }

    public string ProjectMustHavesJson(RubricEnvelopeModel envelope)
    {
        var items = envelope.Items
            .Where(item => item.RequirementType is RubricRequirementTypes.MustHave or RubricRequirementTypes.Experience)
            .Select(item => new
            {
                Criterion = item.Text,
                Description = item.SourceText ?? item.Text,
            })
            .ToList();
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    public string ProjectDesiredCriteriaJson(RubricEnvelopeModel envelope)
    {
        var items = envelope.Items
            .Where(item => item.RequirementType == RubricRequirementTypes.Desired)
            .Select(item => new
            {
                Qualification = item.Text,
                Description = item.SourceText ?? item.Text,
            })
            .ToList();
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    public IReadOnlyList<LegacyRubricCategoryModel> ProjectLegacyCategories(RubricEnvelopeModel envelope)
    {
        return envelope.Categories
            .OrderBy(category => category.Order)
            .Select(category => new LegacyRubricCategoryModel
            {
                Name = category.Name,
                Weight = category.Weight,
                Description = string.Join("; ", envelope.Items
                    .Where(item => item.CategoryId == category.Id)
                    .OrderBy(item => item.Order)
                    .Select(item => item.Text)),
            })
            .ToList();
    }

    public string SerializeEnvelope(RubricEnvelopeModel envelope) => JsonSerializer.Serialize(
        _rubricOrderNormalizer.Normalize(envelope),
        JsonOptions);

    private static HashSet<string> ParseMustHaves(string? mustHavesJson)
    {
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(mustHavesJson))
            return items;

        try
        {
            using var document = JsonDocument.Parse(mustHavesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return items;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        AddIfValue(items, element.GetString());
                        break;
                    case JsonValueKind.Object when element.TryGetProperty("criterion", out var criterion):
                        AddIfValue(items, criterion.GetString());
                        break;
                    case JsonValueKind.Object when element.TryGetProperty("Criterion", out var upperCriterion):
                        AddIfValue(items, upperCriterion.GetString());
                        break;
                }
            }
        }
        catch (JsonException)
        {
        }

        return items;
    }

    private static HashSet<string> ParseDesiredCriteria(string? desiredCriteriaJson)
    {
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(desiredCriteriaJson))
            return items;

        try
        {
            using var document = JsonDocument.Parse(desiredCriteriaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return items;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        AddIfValue(items, element.GetString());
                        break;
                    case JsonValueKind.Object when element.TryGetProperty("qualification", out var qualification):
                        AddIfValue(items, qualification.GetString());
                        break;
                    case JsonValueKind.Object when element.TryGetProperty("Qualification", out var upperQualification):
                        AddIfValue(items, upperQualification.GetString());
                        break;
                }
            }
        }
        catch (JsonException)
        {
        }

        return items;
    }

    private static List<string> SplitLegacyRequirements(string? description)
    {
        return (description ?? string.Empty)
            .Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddIfValue(ISet<string> items, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            items.Add(value.Trim());
    }

    private static string Slugify(string value)
    {
        var builder = new char[value.Length];
        var index = 0;
        foreach (var character in value.ToLowerInvariant())
        {
            builder[index++] = char.IsLetterOrDigit(character) ? character : '-';
        }

        return string.Join(
            '-',
            new string(builder[..index])
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
