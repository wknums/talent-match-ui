using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using ApplicationEntity = TalentMatch.Domain.Entities.Application;

namespace TalentMatch.Application.Tests;

public class SaveManualReviewCommandTests
{
    [Fact]
    public async Task Handle_NewReview_NoEdits_KeepsHumanEditedFalse()
    {
        var repository = new Mock<IApplicationRepository>();
        var currentUser = new Mock<ICurrentUserService>();

        repository
            .Setup(r => r.GetManualReviewAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ManualReviewData?)null);

        repository
            .Setup(r => r.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationEntity { Id = "app-1", FinalDecision = "NeedsManualReview", Status = "NeedsManualReview" });
        repository
            .Setup(r => r.UpdateAsync(It.IsAny<ApplicationEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.GetAggregatedResultAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AggregatedResult?)null);

        ManualReviewData? saved = null;
        repository
            .Setup(r => r.SetManualReviewAsync(It.IsAny<ManualReviewData>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewData, CancellationToken>((review, _) => saved = review)
            .Returns(Task.CompletedTask);

        var handler = new SaveManualReviewCommandHandler(repository.Object, currentUser.Object);
        var command = new SaveManualReviewCommand(
            "app-1",
            "{}",
            "Pre-populated from AI scoring...",
            81.5,
            "[]",
            false,
            null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.HumanEdited.Should().BeFalse();
        saved.Should().NotBeNull();
        saved!.HumanEdited.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NewReview_WithEdits_SetsHumanEditedTrue()
    {
        var repository = new Mock<IApplicationRepository>();
        var currentUser = new Mock<ICurrentUserService>();

        repository
            .Setup(r => r.GetManualReviewAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ManualReviewData?)null);

        repository
            .Setup(r => r.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationEntity { Id = "app-1", FinalDecision = "NeedsManualReview", Status = "NeedsManualReview" });
        repository
            .Setup(r => r.UpdateAsync(It.IsAny<ApplicationEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.GetAggregatedResultAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AggregatedResult?)null);

        ManualReviewData? saved = null;
        repository
            .Setup(r => r.SetManualReviewAsync(It.IsAny<ManualReviewData>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewData, CancellationToken>((review, _) => saved = review)
            .Returns(Task.CompletedTask);

        var handler = new SaveManualReviewCommandHandler(repository.Object, currentUser.Object);
        var command = new SaveManualReviewCommand(
            "app-1",
            "{}",
            "Recruiter override",
            90,
            "[]",
            true,
            null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.HumanEdited.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.HumanEdited.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ExistingHumanEdited_StaysTrue()
    {
        var repository = new Mock<IApplicationRepository>();
        var currentUser = new Mock<ICurrentUserService>();

        repository
            .Setup(r => r.GetManualReviewAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManualReviewData
            {
                ApplicationId = "app-1",
                HumanEdited = true,
                RubricScoresJson = "{}",
                OverallComment = "existing",
                AuditTrailJson = "[]",
            });

        repository
            .Setup(r => r.GetByIdAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApplicationEntity { Id = "app-1", FinalDecision = "NeedsManualReview", Status = "NeedsManualReview" });
        repository
            .Setup(r => r.UpdateAsync(It.IsAny<ApplicationEntity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(r => r.GetAggregatedResultAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AggregatedResult?)null);

        ManualReviewData? saved = null;
        repository
            .Setup(r => r.SetManualReviewAsync(It.IsAny<ManualReviewData>(), It.IsAny<CancellationToken>()))
            .Callback<ManualReviewData, CancellationToken>((review, _) => saved = review)
            .Returns(Task.CompletedTask);

        var handler = new SaveManualReviewCommandHandler(repository.Object, currentUser.Object);
        var command = new SaveManualReviewCommand(
            "app-1",
            "{}",
            "still edited",
            88,
            "[]",
            false,
            null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.HumanEdited.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.HumanEdited.Should().BeTrue();
    }
}
