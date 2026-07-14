using System.Text.Json;
using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Prompts.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class GeneratePromptCommandTests
{
    private readonly Mock<IScoringPromptRepository> _promptRepoMock = new();
    private readonly Mock<IJobRepository> _jobRepoMock = new();

    [Fact]
    public async Task Handle_FallbackPrompt_AppendsRubricDerivedTargetJsonTemplate()
    {
        var job = BuildJobWithApprovedConfig(
            rubricJson: """
            [
              { "name": "Technical Skills", "weight": 0.6, "description": "Core technical match" },
              { "name": "Communication", "weight": 0.4, "description": "Clear communication" }
            ]
            """,
            mustHavesJson: """
            [
              { "criterion": "3+ years C#", "description": "Professional C# experience" },
              { "criterion": "Cloud exposure", "description": "Worked with Azure or AWS" }
            ]
            """,
            desiredCriteriaJson: """
            [
              { "qualification": "Mentoring", "description": "Mentored engineers" }
            ]
            """);

        ScoringPrompt? savedPrompt = null;
        _jobRepoMock.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _promptRepoMock.Setup(r => r.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScoringPrompt>());
        _promptRepoMock.Setup(r => r.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((p, _) => savedPrompt = p)
            .Returns(Task.CompletedTask);

        var handler = new GeneratePromptCommandHandler(_promptRepoMock.Object, _jobRepoMock.Object, llmService: null);

        var result = await handler.Handle(new GeneratePromptCommand("job-1", "author-1"), CancellationToken.None);

        result.Should().NotBeNull();
        savedPrompt.Should().NotBeNull();
        savedPrompt!.PromptText.Should().Contain("## Output Contract (Mandatory)");

        using var templateDoc = JsonDocument.Parse(ExtractTemplateJson(savedPrompt.PromptText));
        var root = templateDoc.RootElement;

        root.TryGetProperty("candidate_name", out var candidateName).Should().BeTrue();
        candidateName.ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("category_scores").GetArrayLength().Should().Be(2);
        root.GetProperty("category_scores").EnumerateArray().Select(x => x.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "Technical Skills", "Communication" });

        foreach (var category in root.GetProperty("category_scores").EnumerateArray())
        {
            category.TryGetProperty("score", out _).Should().BeTrue();
            category.GetProperty("evidence").ValueKind.Should().Be(JsonValueKind.Array);
        }

        var entries = root.GetProperty("must_have_requirements")
            .GetProperty("details")
            .GetProperty("entries");
        entries.GetArrayLength().Should().Be(2);

        root.GetProperty("desired_criteria").GetArrayLength().Should().Be(1);
        root.TryGetProperty("overall_score", out _).Should().BeTrue();
        root.TryGetProperty("improvement_tips", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Handle_UsesApprovedRubricVersion_WhenNewerDraftExists()
    {
        var approvedConfig = new JobConfigVersion
        {
            VersionNumber = 1,
            RubricApprovalStatus = "approved",
            RubricJson = """
            [
              { "name": "Approved Category", "weight": 1.0, "description": "Approved rubric" }
            ]
            """
        };

        var draftConfig = new JobConfigVersion
        {
            VersionNumber = 2,
            RubricApprovalStatus = "draft",
            RubricJson = """
            [
              { "name": "Draft Category", "weight": 1.0, "description": "Draft rubric" }
            ]
            """
        };

        var job = new Job
        {
            Id = "job-1",
            Title = "Software Engineer",
            Department = "Engineering",
            Organisation = "TechCo",
            ConfigVersions = new List<JobConfigVersion> { approvedConfig, draftConfig }
        };

        ScoringPrompt? savedPrompt = null;
        _jobRepoMock.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _promptRepoMock.Setup(r => r.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScoringPrompt>());
        _promptRepoMock.Setup(r => r.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((p, _) => savedPrompt = p)
            .Returns(Task.CompletedTask);

        var handler = new GeneratePromptCommandHandler(_promptRepoMock.Object, _jobRepoMock.Object, llmService: null);

        await handler.Handle(new GeneratePromptCommand("job-1", "author-1"), CancellationToken.None);

        savedPrompt.Should().NotBeNull();
        using var templateDoc = JsonDocument.Parse(ExtractTemplateJson(savedPrompt!.PromptText));
        var categoryNames = templateDoc.RootElement.GetProperty("category_scores")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        categoryNames.Should().ContainSingle("Approved Category");
        categoryNames.Should().NotContain("Draft Category");
    }

    [Fact]
    public async Task Handle_FallsBackToLatestRubric_WhenNoApprovedVersionExists()
    {
        var legacyConfigV1 = new JobConfigVersion
        {
            VersionNumber = 1,
            RubricApprovalStatus = "draft",
            RubricJson = """
            [
              { "name": "Legacy V1", "weight": 1.0, "description": "Older rubric" }
            ]
            """
        };

        var legacyConfigV2 = new JobConfigVersion
        {
            VersionNumber = 2,
            RubricApprovalStatus = "draft",
            RubricJson = """
            [
              { "name": "Legacy V2", "weight": 1.0, "description": "Latest legacy rubric" }
            ]
            """
        };

        var job = new Job
        {
            Id = "job-1",
            Title = "Software Engineer",
            Department = "Engineering",
            Organisation = "TechCo",
            ConfigVersions = new List<JobConfigVersion> { legacyConfigV1, legacyConfigV2 }
        };

        ScoringPrompt? savedPrompt = null;
        _jobRepoMock.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _promptRepoMock.Setup(r => r.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScoringPrompt>());
        _promptRepoMock.Setup(r => r.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>()))
            .Callback<ScoringPrompt, CancellationToken>((p, _) => savedPrompt = p)
            .Returns(Task.CompletedTask);

        var handler = new GeneratePromptCommandHandler(_promptRepoMock.Object, _jobRepoMock.Object, llmService: null);

        await handler.Handle(new GeneratePromptCommand("job-1", "author-1"), CancellationToken.None);

        savedPrompt.Should().NotBeNull();
        using var templateDoc = JsonDocument.Parse(ExtractTemplateJson(savedPrompt!.PromptText));
        var categoryNames = templateDoc.RootElement.GetProperty("category_scores")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        categoryNames.Should().ContainSingle("Legacy V2");
        categoryNames.Should().NotContain("Legacy V1");
    }

    [Fact]
    public async Task Handle_LlmGeneratedPrompt_StillAppendsStableOutputContract()
    {
        var job = BuildJobWithApprovedConfig(
            rubricJson: """
            [
              { "name": "Delivery", "weight": 1.0, "description": "Delivery fit" }
            ]
            """,
            mustHavesJson: "[]",
            desiredCriteriaJson: "[]");

        var llmMock = new Mock<ILlmProxyService>();
        string? capturedUserPrompt = null;
        llmMock.Setup(s => s.SendPromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, userPrompt, _) => capturedUserPrompt = userPrompt)
            .ReturnsAsync("Generated by LLM");

        _jobRepoMock.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _promptRepoMock.Setup(r => r.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScoringPrompt>());
        _promptRepoMock.Setup(r => r.AddAsync(It.IsAny<ScoringPrompt>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new GeneratePromptCommandHandler(_promptRepoMock.Object, _jobRepoMock.Object, llmMock.Object);

        var result = await handler.Handle(new GeneratePromptCommand("job-1", "author-1"), CancellationToken.None);

        result.PromptText.Should().Contain("Generated by LLM");
        result.PromptText.Should().Contain("## Output Contract (Mandatory)");
        capturedUserPrompt.Should().NotBeNull();
        capturedUserPrompt.Should().Contain("targetScoringJson");
        capturedUserPrompt.Should().Contain("candidate_name");
    }

    private static Job BuildJobWithApprovedConfig(string rubricJson, string mustHavesJson, string desiredCriteriaJson)
    {
        return new Job
        {
            Id = "job-1",
            Title = "Software Engineer",
            Department = "Engineering",
            Organisation = "TechCo",
            ConfigVersions = new List<JobConfigVersion>
            {
                new()
                {
                    VersionNumber = 1,
                    RubricApprovalStatus = "approved",
                    RubricJson = rubricJson,
                    MustHavesJson = mustHavesJson,
                    DesiredCriteriaJson = desiredCriteriaJson
                }
            }
        };
    }

    private static string ExtractTemplateJson(string promptText)
    {
        const string marker = "### Target JSON Template";
        var markerIndex = promptText.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0);

        return promptText[(markerIndex + marker.Length)..].Trim();
    }
}
