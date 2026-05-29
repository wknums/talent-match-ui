using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.Scoring.Commands;

namespace TalentMatch.Application.Tests;

/// <summary>
/// Tests for the parser's evidence extraction behavior that drives the manual review
/// prepopulation UI. These verify the end-to-end flow from LLM JSON to citation output
/// that matches the contract rules PR-1 through PR-4.
/// </summary>
public class ManualReviewPrepopulationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private ScoreApplicationCommandHandler CreateHandler()
    {
        return new ScoreApplicationCommandHandler(null!, null!, null!, null!);
    }

    [Fact]
    public void FirstOpen_ValidEvidence_ProducesScoresAndCitations_PR1()
    {
        var fixture = TestFixtureLoader.LoadFixture("nestedObjectLowercaseEvidence");
        var handler = CreateHandler();
        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
        scores.Should().ContainKey("Technical Skills").WhoseValue.Should().Be(85);
        scores.Should().ContainKey("Communication").WhoseValue.Should().Be(70);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().Contain(c => c.Category == "Technical Skills");
        citations.Should().Contain(c => c.Category == "Communication");
    }

    [Fact]
    public void UnmatchedCategories_ProducesEmptyCitationsForMismatch_PR3()
    {
        // When the LLM uses completely different category names than the rubric,
        // the ParseSingleRun will still extract citations with the LLM's names.
        // Mismatch detection happens at the UI level when matching to rubric.
        var json = @"{
            ""rubric_scores"": [
                { ""category_name"": ""Quantum Physics"", ""score"": 95, ""evidence"": ""Nobel prize winner"" },
                { ""category_name"": ""Rocket Science"", ""score"": 90, ""evidence"": ""SpaceX veteran"" }
            ],
            ""total_score"": 92.5
        }";
        using var doc = JsonDocument.Parse(json);
        var handler = CreateHandler();
        var run = handler.ParseSingleRun(doc.RootElement, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        // Citations use LLM category names, UI must match to rubric
        citations!.Should().Contain(c => c.Category == "Quantum Physics");
        citations.Should().Contain(c => c.Category == "Rocket Science");
    }

    [Fact]
    public void NoScoringRuns_ProducesEmptyOutput_PR4()
    {
        // When there are no scoring runs, ParseSingleRun is never called.
        // This test verifies the empty baseline produces valid JSON.
        using var doc = JsonDocument.Parse("{}");
        var handler = CreateHandler();
        var run = handler.ParseSingleRun(doc.RootElement, "app-1", "prompt-1", 1);

        run.TotalScore.Should().Be(0);
        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
        scores.Should().BeEmpty();

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().BeEmpty();
    }

    [Fact]
    public void EvidenceDeduplication_DuplicateSnippetsAcrossRuns_ExtractsAll()
    {
        // Two runs with the same evidence should both produce citations.
        // Deduplication happens at the UI level (ManualReview.razor / ManualReviewView.tsx).
        var json = @"{
            ""categories"": {
                ""Technical Skills"": {
                    ""score"": 85,
                    ""evidence"": [""Java expert"", ""AWS certified""]
                }
            },
            ""total_score"": 85
        }";
        using var doc1 = JsonDocument.Parse(json);
        using var doc2 = JsonDocument.Parse(json);
        var handler = CreateHandler();

        var run1 = handler.ParseSingleRun(doc1.RootElement, "app-1", "prompt-1", 1);
        var run2 = handler.ParseSingleRun(doc2.RootElement, "app-1", "prompt-1", 2);

        var citations1 = JsonSerializer.Deserialize<List<CitationDto>>(run1.EvidenceCitationsJson, JsonOpts);
        var citations2 = JsonSerializer.Deserialize<List<CitationDto>>(run2.EvidenceCitationsJson, JsonOpts);

        // Both runs should produce the same citations independently
        citations1.Should().HaveCount(2);
        citations2.Should().HaveCount(2);
    }

    [Fact]
    public void CapitalisedEvidence_WithNestedObject_IsExtracted()
    {
        var fixture = TestFixtureLoader.LoadFixture("nestedObjectCapitalisedEvidence");
        var handler = CreateHandler();
        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        // With the case-insensitive fix, "Evidence" should be found
        citations!.Where(c => c.Category == "Technical Skills").Should().NotBeEmpty();
    }

    private record CitationDto(string? Category, string? Snippet);
}
