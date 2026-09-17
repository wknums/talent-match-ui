using System.Text.Json;
using TalentMatch.Application.Rubrics.Models;

namespace TalentMatch.Application.Rubrics.Services;

public static class RubricJsonReader
{
    public static bool IsV2(JsonElement root)
        => TryGetProperty(root, "schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.String
            && version.ValueEquals(RubricSchemaVersions.RubricV2);

    public static bool TryGetCategories(JsonElement root, out JsonElement categories)
    {
        categories = default;
        return IsV2(root)
            && TryGetProperty(root, "categories", out categories)
            && categories.ValueKind == JsonValueKind.Array;
    }

    public static bool HasScoringRubric(JsonElement root)
        => root.ValueKind == JsonValueKind.Array
            ? root.GetArrayLength() > 0
            : TryGetCategories(root, out var categories)
                && categories.GetArrayLength() > 0
                && TryGetProperty(root, "items", out var items)
                && items.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        var found = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            // Reject ambiguous duplicates rather than interpreting different casing differently.
            if (found)
            {
                value = default;
                return false;
            }
            value = property.Value;
            found = true;
        }
        return found;
    }
}
