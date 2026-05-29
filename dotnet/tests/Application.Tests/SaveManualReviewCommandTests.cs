using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

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
            false);

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
            true);

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
            false);

        var result = await handler.Handle(command, CancellationToken.None);

        result.HumanEdited.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.HumanEdited.Should().BeTrue();
    }
}
