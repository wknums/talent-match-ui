using FluentAssertions;
using MediatR;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Common.Services;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class SequentialApplicationScorerTests
{
    [Fact]
    public async Task ScoreAsync_DoesNotEnterFinalizationWhileTheScoringApiIsPending()
    {
        var response = new TaskCompletionSource<ScoreApplicationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediator = new Mock<ISender>();
        var queue = new Mock<ISequentialScoringQueueRepository>(MockBehavior.Strict);
        var finalizer = new Mock<IApplicationScoringFinalizer>(MockBehavior.Strict);
        var work = new SequentialScoringWork("app", "job", "owner", "prompt", 1, "Job", "[]", 15, 70);
        IReadOnlyList<ScoringRun> runs = [new() { ApplicationId = "app", TotalScore = 80 }];
        var prepared = new ApplicationScoringFinalization(
            runs, new AggregatedResult { ApplicationId = "app" }, "Completed", null);
        mediator.Setup(sender => sender.Send(
            It.Is<ScoreApplicationCommand>(command => !command.PersistResults), It.IsAny<CancellationToken>()))
            .Returns(response.Task);
        finalizer.Setup(service => service.Prepare("app", runs, 1, 15, 70, null)).Returns(prepared);
        queue.Setup(repo => repo.FinalizeAsync("app", "owner", prepared, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scoring = new SequentialApplicationScorer(mediator.Object, queue.Object, finalizer.Object)
            .ScoreAsync(work, CancellationToken.None);

        try
        {
            scoring.IsCompleted.Should().BeFalse();
            queue.VerifyNoOtherCalls();
            finalizer.VerifyNoOtherCalls();
        }
        finally
        {
            response.TrySetResult(new ScoreApplicationResult(runs, null));
        }
        await scoring;
        queue.Verify(repo => repo.FinalizeAsync("app", "owner", prepared, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_PreparesResultsBeforeTheOwnershipFencedBatch()
    {
        var mediator = new Mock<ISender>();
        var queue = new Mock<ISequentialScoringQueueRepository>();
        var finalizer = new Mock<IApplicationScoringFinalizer>();
        var work = new SequentialScoringWork("app-1", "job-1", "owner-1", "prompt-1", 1, "Job", "[]", 15, 70);
        var run = new ScoringRun { ApplicationId = work.ApplicationId, TotalScore = 82 };
        IReadOnlyList<ScoringRun> runs = [run];
        var prepared = new ApplicationScoringFinalization(
            runs, new AggregatedResult { ApplicationId = work.ApplicationId, FinalScore = 82 }, "Completed", "Candidate");
        mediator.Setup(sender => sender.Send(It.Is<ScoreApplicationCommand>(command =>
                !command.PersistResults && command.ApplicationId == work.ApplicationId
                && command.PromptVersionId == work.PromptVersionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoreApplicationResult(runs, null, CandidateName: "Candidate"));
        var sequence = new MockSequence();
        finalizer.InSequence(sequence).Setup(service => service.Prepare(
            work.ApplicationId, runs, 1, 15, 70, "Candidate")).Returns(prepared);
        queue.InSequence(sequence).Setup(repo => repo.FinalizeAsync(
            work.ApplicationId, work.Owner, prepared, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var scorer = new SequentialApplicationScorer(mediator.Object, queue.Object, finalizer.Object);

        await scorer.ScoreAsync(work, CancellationToken.None);

        finalizer.Verify(service => service.Prepare(work.ApplicationId, runs, 1, 15, 70, "Candidate"), Times.Once);
        queue.Verify(repo => repo.FinalizeAsync(
            work.ApplicationId, work.Owner, prepared, It.IsAny<CancellationToken>()), Times.Once);
        finalizer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScoreAsync_DoesNotPersistResultsAfterOwnershipIsLost()
    {
        var mediator = new Mock<ISender>();
        var applications = new Mock<IApplicationRepository>(MockBehavior.Strict);
        mediator.Setup(sender => sender.Send(It.IsAny<ScoreApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoreApplicationResult([new ScoringRun()], null));
        var scorer = new SequentialApplicationScorer(mediator.Object,
            Mock.Of<ISequentialScoringQueueRepository>(), new ApplicationScoringFinalizer(applications.Object));
        var work = new SequentialScoringWork("app-1", "job-1", "expired-owner", "prompt-1", 1, "Job", "[]", 15, 70);

        var act = () => scorer.ScoreAsync(work, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership*");
        applications.VerifyNoOtherCalls();
        mediator.Verify(sender => sender.Send(It.IsAny<ScoreApplicationCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(82, 82, false, "Completed", "Eligible")]
    [InlineData(40, 40, false, "Completed", "Excluded")]
    [InlineData(50, 90, false, "NeedsManualReview", "NeedsManualReview")]
    [InlineData(82, 82, true, "Completed", "Excluded")]
    public async Task Prepare_PreservesTheSharedFinalizerDecisionAndDoesNoPersistence(
        double first, double second, bool gateFailed, string status, string decision)
    {
        var applications = new Mock<IApplicationRepository>(MockBehavior.Strict);
        var finalizer = new ApplicationScoringFinalizer(applications.Object);
        var runs = new[]
        {
            new ScoringRun { ApplicationId = "app", TotalScore = first,
                CategoryScoresJson = """{"Experience":70}""",
                MustHaveEvaluationJson = gateFailed ? """{"passed":false}""" : "{}" },
            new ScoringRun { ApplicationId = "app", TotalScore = second,
                CategoryScoresJson = """{"Experience":90}""",
                MustHaveEvaluationJson = gateFailed ? """{"passed":false}""" : "{}" }
        };

        var prepared = finalizer.Prepare("app", runs, 4, 15, 70, "Candidate");

        prepared.Status.Should().Be(status);
        prepared.Aggregate.Decision.Should().Be(decision);
        prepared.Aggregate.FinalScore.Should().Be((first + second) / 2);
        prepared.Aggregate.Confidence.Should().Be(0.5);
        prepared.Aggregate.FinalSubScoresJson.Should().Be("""{"Experience":80}""");
        prepared.CandidateName.Should().Be("Candidate");
        applications.VerifyNoOtherCalls();

        applications.Setup(repo => repo.GetByIdAsync("app", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application { Id = "app" });
        applications.Setup(repo => repo.UpdateAsync(It.Is<Domain.Entities.Application>(app =>
            app.Status == status && app.FinalDecision == decision), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        applications.Setup(repo => repo.SetAggregatedResultAsync(It.IsAny<AggregatedResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var persisted = await finalizer.FinalizeAsync("app", "job", runs, 4, 15, 70);
        persisted.Should().Be(new ApplicationScoringFinalizerResult(
            prepared.Aggregate.FinalScore, prepared.Aggregate.Variance, decision, status));
    }
}
