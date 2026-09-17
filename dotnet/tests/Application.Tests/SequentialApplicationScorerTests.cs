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
    public async Task ScoreAsync_PersistsRunsAndFinalResultOnlyInsideTheOwnershipFence()
    {
        var mediator = new Mock<ISender>();
        var applications = new Mock<IApplicationRepository>();
        var queue = new Mock<ISequentialScoringQueueRepository>();
        var finalizer = new Mock<IApplicationScoringFinalizer>();
        var work = new SequentialScoringWork("app-1", "job-1", "owner-1", "prompt-1", 1, "Job", "[]", 15, 70);
        var run = new ScoringRun { ApplicationId = work.ApplicationId, TotalScore = 82 };
        var insideFence = false;
        mediator.Setup(sender => sender.Send(It.Is<ScoreApplicationCommand>(command =>
                !command.PersistResults && command.ApplicationId == work.ApplicationId
                && command.PromptVersionId == work.PromptVersionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoreApplicationResult([run], null));
        applications.Setup(repo => repo.AddScoringRunAsync(run, It.IsAny<CancellationToken>()))
            .Callback(() => insideFence.Should().BeTrue());
        queue.Setup(repo => repo.FinalizeAsync(
                work.ApplicationId, work.Owner, It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Func<CancellationToken, Task> persist, CancellationToken token) =>
            {
                insideFence = true;
                await persist(token);
                insideFence = false;
                return true;
            });
        var scorer = new SequentialApplicationScorer(mediator.Object, applications.Object, queue.Object, finalizer.Object);

        await scorer.ScoreAsync(work, CancellationToken.None);

        applications.Verify(repo => repo.AddScoringRunAsync(run, It.IsAny<CancellationToken>()), Times.Once);
        finalizer.Verify(service => service.FinalizeAsync(
            "app-1", "job-1", It.IsAny<IReadOnlyList<ScoringRun>>(), 1, 15, 70, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_DoesNotPersistResultsAfterOwnershipIsLost()
    {
        var mediator = new Mock<ISender>();
        var applications = new Mock<IApplicationRepository>(MockBehavior.Strict);
        mediator.Setup(sender => sender.Send(It.IsAny<ScoreApplicationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScoreApplicationResult([new ScoringRun()], null));
        var scorer = new SequentialApplicationScorer(mediator.Object, applications.Object,
            Mock.Of<ISequentialScoringQueueRepository>(), Mock.Of<IApplicationScoringFinalizer>());
        var work = new SequentialScoringWork("app-1", "job-1", "expired-owner", "prompt-1", 1, "Job", "[]", 15, 70);

        var act = () => scorer.ScoreAsync(work, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership*");
        applications.VerifyNoOtherCalls();
    }
}
