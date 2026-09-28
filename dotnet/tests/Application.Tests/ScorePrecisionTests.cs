using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.Scoring.Commands;

namespace TalentMatch.Application.Tests;

public sealed class ScorePrecisionTests
{
    [Fact]
    public void Parser_rounds_total_and_category_scores_to_three_decimals()
    {
        using var document = JsonDocument.Parse(
            """{"overall_score":81.23456,"category_scores":[{"name":"Skills","score":79.87654}]}""");

        var run = ScoreApplicationCommandHandler.ParseSingleRunStatic(
            document.RootElement, "app-1", "prompt-1", 1, "model-x", "high");

        run.TotalScore.Should().Be(81.235);
        run.AiModelId.Should().Be("model-x");
        run.ReasoningLevel.Should().Be("high");
        var categories = JsonSerializer.Deserialize<Dictionary<string, double>>(
            run.CategoryScoresJson);
        categories!["Skills"].Should().Be(79.877);
    }
}
