using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.Rubrics.Services;
using TalentMatch.Web.Client.Services;

namespace TalentMatch.Web.Tests;

public sealed class RubricSerializationTests
{
    [Fact]
    public void DefaultSerialization_UsesCanonicalNamesForTheEmbeddedRubricJson()
    {
        var rubric = new RubricEnvelopeDto("rubric-v2", null,
            [new RubricCategoryV2Dto("cat-1", "Technical Skills", 1, "Technical requirements", 0)],
            [new RubricItemDto("item-1", "cat-1", "Programming experience", "experience", 0,
                "Programming experience", null, null, "confirmed", "manual")]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rubric));

        json.RootElement.TryGetProperty("schemaVersion", out var version).Should().BeTrue();
        version.GetString().Should().Be("rubric-v2");
        json.RootElement.GetProperty("categories")[0].GetProperty("name").GetString().Should().Be("Technical Skills");
        json.RootElement.GetProperty("items")[0].GetProperty("categoryId").GetString().Should().Be("cat-1");
        json.RootElement.GetProperty("items")[0].GetProperty("requirementType").GetString().Should().Be("experience");
        RubricJsonReader.HasScoringRubric(json.RootElement).Should().BeTrue();
    }

    [Fact]
    public void ExistingPascalCaseRubric_CanStillBeLoadedByTheClient()
    {
        const string stored = """
            {"SchemaVersion":"rubric-v2","Categories":[{"Id":"cat-1","Name":"Technical Skills","Weight":1,"Order":0}],"Items":[]}
            """;

        var result = JsonSerializer.Deserialize<RubricEnvelopeDto>(
            stored, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        result!.SchemaVersion.Should().Be("rubric-v2");
        result.Categories.Should().ContainSingle().Which.Name.Should().Be("Technical Skills");
    }
}
