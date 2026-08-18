using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class ScoringRetryTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void CanRetry_QueuesOnlyAfterThreeAutomaticRetries(int failureCount, bool expected)
    {
        ScoringRetryPolicy.CanRetry(failureCount).Should().Be(expected);
    }

    [Fact]
    public async Task Handle_RetriesMalformedJsonBeforePersistingRun()
    {
        var fixture = CreateFixture();
        fixture.Llm.SetupSequence(x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "not-json" })
            .ReturnsAsync(new[] { """{"overallScore":82,"recommendation":"Eligible"}""" });

        var result = await fixture.Handler.Handle(CreateCommand(), CancellationToken.None);

        result.Runs.Should().ContainSingle();
        result.Runs[0].TotalScore.Should().Be(82);
        fixture.Applications.Verify(
            x => x.AddScoringRunAsync(It.IsAny<ScoringRun>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.Llm.Verify(
            x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_RetriesTimeoutBeforePersistingRun()
    {
        var fixture = CreateFixture();
        fixture.Llm.SetupSequence(x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("scoring timed out"))
            .ReturnsAsync(new[] { """{"overallScore":74,"recommendation":"Eligible"}""" });

        var result = await fixture.Handler.Handle(CreateCommand(), CancellationToken.None);

        result.Runs.Should().ContainSingle();
        result.Runs[0].TotalScore.Should().Be(74);
        fixture.Llm.Verify(
            x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Handle_ThrowsMalformedJsonAfterFourthPersistentFailure()
    {
        var fixture = CreateFixture();
        fixture.Llm.Setup(x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "still-not-json" });

        var act = () => fixture.Handler.Handle(CreateCommand(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ScoringRetriesExhaustedException>();
        exception.Which.FailureCount.Should().Be(4);
        fixture.Llm.Verify(
            x => x.ScoreWithDocumentAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(),
                It.IsAny<string>(), 1, It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        fixture.Applications.Verify(
            x => x.AddScoringRunAsync(It.IsAny<ScoringRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ScoreApplicationCommand CreateCommand()
        => new("app-1", "job-1", 1, "prompt-1", "Job description");

    private static RetryFixture CreateFixture()
    {
        var llm = new Mock<ILlmProxyService>();
        var applications = new Mock<IApplicationRepository>();
        var prompts = new Mock<IScoringPromptRepository>();
        var blobs = new Mock<IBlobStore>();

        prompts.Setup(x => x.GetByIdAsync("prompt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoringPrompt
            {
                Id = "prompt-1",
                JobId = "job-1",
                PromptText = "Score {{JOB_SPEC_TEXT}}",
            });
        applications.Setup(x => x.GetDocumentsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ApplicationDocument
                {
                    Id = "doc-1",
                    ApplicationId = "app-1",
                    FileName = "candidate.pdf",
                    FileType = "application/pdf",
                    ContentBase64 = Convert.ToBase64String("cv"u8.ToArray()),
                },
            });
        applications.Setup(x => x.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TalentMatch.Domain.Entities.Application
            {
                Id = "app-1",
                JobId = "job-1",
                CandidateName = "Candidate",
            });

        var handler = new ScoreApplicationCommandHandler(
            llm.Object,
            applications.Object,
            prompts.Object,
            blobs.Object,
            (_, _) => Task.CompletedTask);

        return new RetryFixture(handler, llm, applications);
    }

    private sealed record RetryFixture(
        ScoreApplicationCommandHandler Handler,
        Mock<ILlmProxyService> Llm,
        Mock<IApplicationRepository> Applications);
}
