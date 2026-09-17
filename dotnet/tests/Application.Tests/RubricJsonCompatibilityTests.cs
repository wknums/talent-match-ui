using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.Rubrics.Services;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Tests;

public sealed class RubricJsonCompatibilityTests
{
    private const string StoredBlazorRubric = """
        {"SchemaVersion":"rubric-v2","Categories":[
          {"Id":"technical","Name":"Technical Skills","Weight":1,"Order":0}],
         "Items":[{"Id":"item-1","CategoryId":"technical","Text":"Programming experience",
                   "RequirementType":"experience","Order":0,"ReviewStatus":"confirmed","CreatedFrom":"manual"}]}
        """;

    [Theory]
    [InlineData("""{"SCHEMAVERSION":"rubric-v2","Categories":[{}],"items":[]}""", true)]
    [InlineData("""{"SchemaVersion":2,"Categories":[{}],"Items":[]}""", false)]
    [InlineData("""{"SchemaVersion":"rubric-v3","Categories":[{}],"Items":[]}""", false)]
    [InlineData("""{"SchemaVersion":"rubric-v2","Categories":[],"Items":[]}""", false)]
    [InlineData("""{"SchemaVersion":"rubric-v2","Categories":[{}],"Items":null}""", false)]
    [InlineData("""{"schemaVersion":"rubric-v2","SchemaVersion":"rubric-v3","Categories":[{}],"Items":[]}""", false)]
    public void Admission_HandlesPropertyCasingWithoutAcceptingMalformedOrAmbiguousEnvelopes(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        RubricJsonReader.HasScoringRubric(document.RootElement).Should().Be(expected);
    }

    [Fact]
    public void Adapter_ReadsExistingBlazorRubricsWithoutTreatingThemAsLegacyArrays()
    {
        var adapter = new LegacyRubricAdapter(new RubricOrderNormalizer());

        adapter.IsRubricV2Json(StoredBlazorRubric).Should().BeTrue();
        var envelope = adapter.TryParseRubricEnvelope(StoredBlazorRubric);

        envelope.Should().NotBeNull();
        envelope!.Categories.Should().ContainSingle().Which.Name.Should().Be("Technical Skills");
        envelope.Items.Should().ContainSingle().Which.CategoryId.Should().Be("technical");
    }

    [Fact]
    public void Scoring_MapsCategoriesFromPreviouslyStoredBlazorRubrics()
    {
        var run = new ScoringRun { CategoryScoresJson = """{"Technical":85}""" };

        ScoreApplicationCommandHandler.RemapToRubricStatic(run, StoredBlazorRubric);

        using var scores = JsonDocument.Parse(run.CategoryScoresJson);
        scores.RootElement.TryGetProperty("Technical Skills", out var score).Should().BeTrue();
        score.GetDouble().Should().Be(85);
    }
}
