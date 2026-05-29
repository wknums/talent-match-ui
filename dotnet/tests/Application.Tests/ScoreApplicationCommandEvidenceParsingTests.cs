using System.Text.Json;
using FluentAssertions;
using TalentMatch.Application.Scoring.Commands;

namespace TalentMatch.Application.Tests;

public class ScoreApplicationCommandEvidenceParsingTests
{
    /// <summary>
    /// Create a handler instance to access ParseSingleRun (now internal).
    /// The handler constructor parameters are not needed for parsing tests.
    /// </summary>
    private ScoreApplicationCommandHandler CreateHandler()
    {
        return new ScoreApplicationCommandHandler(null!, null!, null!, null!);
    }

    [Fact]
    public void ParseSingleRun_NestedObject_LowercaseEvidence_ExtractsCitations()
    {
        var fixture = TestFixtureLoader.LoadFixture("nestedObjectLowercaseEvidence");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "5 years Java experience");
        citations.Should().Contain(c => c.Category == "Communication" && c.Snippet == "Led team presentations");
    }

    [Fact]
    public void ParseSingleRun_NestedObject_CapitalisedEvidence_ExtractsCitations()
    {
        var fixture = TestFixtureLoader.LoadFixture("nestedObjectCapitalisedEvidence");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "5 years Java experience");
        citations.Should().Contain(c => c.Category == "Communication" && c.Snippet == "Led team presentations");
    }

    [Fact]
    public void ParseSingleRun_ArrayOfObjects_CategoryFirst_ExtractsCorrectCategoryAndSnippet()
    {
        var fixture = TestFixtureLoader.LoadFixture("arrayOfObjectsCategoryFirst");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "5 years Java experience");
        citations.Should().Contain(c => c.Category == "Communication" && c.Snippet == "Led team presentations");

        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
        scores.Should().ContainKey("Technical Skills").WhoseValue.Should().Be(85);
    }

    [Fact]
    public void ParseSingleRun_ArrayOfObjects_EvidenceBeforeCategory_ExtractsCorrectCategoryAndSnippet()
    {
        var fixture = TestFixtureLoader.LoadFixture("arrayOfObjectsEvidenceBeforeCategory");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet!.Contains("Java"));
        citations.Should().Contain(c => c.Category == "Communication" && c.Snippet!.Contains("presentations"));

        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
        scores.Should().ContainKey("Technical Skills").WhoseValue.Should().Be(85);
        scores.Should().ContainKey("Communication").WhoseValue.Should().Be(70);
    }

    [Fact]
    public void ParseSingleRun_ArrayOfObjects_WithEvidenceArray_ExtractsAllSnippets()
    {
        var fixture = TestFixtureLoader.LoadFixture("arrayOfObjectsWithEvidenceArray");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "5 years Java experience");
        citations.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "AWS certified");
    }

    [Fact]
    public void ParseSingleRun_EmptyEvidenceFields_DoesNotCrash()
    {
        var fixture = TestFixtureLoader.LoadFixture("emptyEvidenceFields");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        // Should still extract scores even with empty/null evidence
        var scores = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
        scores.Should().ContainKey("Technical Skills");
    }

    [Fact]
    public void ParseSingleRun_EmptySnippets_AreFilteredOut()
    {
        var fixture = TestFixtureLoader.LoadFixture("emptySnippets");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Snippet));
        citations.Should().Contain(c => c.Snippet == "Valid evidence");
    }

    [Fact]
    public void ParseSingleRun_NonJsonResponse_HandledGracefully()
    {
        // ParseSingleRun expects a JsonElement, so non-JSON would be caught before calling it.
        // Test with an empty JSON object instead.
        using var doc = JsonDocument.Parse("{}");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(doc.RootElement, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations.Should().BeEmpty();
    }

    [Fact]
    public void ExtractCategoryName_PrefersCategoryNamedProperty()
    {
        using var doc = JsonDocument.Parse(@"{""evidence"": ""some long evidence text"", ""score"": 85, ""category_name"": ""Technical Skills""}");

        var result = ScoreApplicationCommandHandler.ExtractCategoryName(doc.RootElement);

        result.Should().Be("Technical Skills");
    }

    [Fact]
    public void ExtractStringField_PrefersEvidenceNamedProperty()
    {
        using var doc = JsonDocument.Parse(@"{""category_name"": ""Technical Skills"", ""score"": 85, ""evidence"": ""5 years Java experience""}");

        var result = ScoreApplicationCommandHandler.ExtractStringField(doc.RootElement);

        result.Should().Be("5 years Java experience");
    }

    [Fact]
    public void TryGetPropertyCaseInsensitive_MatchesVariousCases()
    {
        using var doc = JsonDocument.Parse(@"{""Evidence"": [""test""]}");

        ScoreApplicationCommandHandler.TryGetPropertyCaseInsensitive(doc.RootElement, "evidence", out var value).Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void ParseSingleRun_MixedCasePropertyNames_ExtractsCorrectly()
    {
        var fixture = TestFixtureLoader.LoadFixture("mixedCasePropertyNames");
        var handler = CreateHandler();

        var run = handler.ParseSingleRun(fixture, "app-1", "prompt-1", 1);

        var citations = JsonSerializer.Deserialize<List<CitationDto>>(run.EvidenceCitationsJson, JsonOpts);
        citations.Should().NotBeNull();
        citations!.Should().Contain(c => c.Category == "Technical Skills" && c.Snippet == "5 years Java experience");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record CitationDto(string? Category, string? Snippet);
}
