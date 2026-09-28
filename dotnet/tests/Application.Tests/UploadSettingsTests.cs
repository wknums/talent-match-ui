using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Uploads.Commands;
using TalentMatch.Application.Uploads.Models;
using TalentMatch.Application.Uploads.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class UploadSettingsTests
{
    [Fact]
    public async Task Get_WhenAbsent_ReturnsDefaultsWithoutPersisting()
    {
        var repository = new Mock<IUploadSettingsRepository>();
        repository.Setup(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null);
        var handler = new GetUploadSettingsQueryHandler(repository.Object);

        var result = await handler.Handle(new GetUploadSettingsQuery(), CancellationToken.None);

        result.Should().Be(new UploadSettingsDto(4, 4_194_304, 104_857_600, 0, false, null, null));
        repository.Verify(x => x.SaveAsync(
            It.IsAny<UploadSettings>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_InvalidLimits_IsAtomic()
    {
        var repository = new Mock<IUploadSettingsRepository>();
        var events = new Mock<IProcessingEventRepository>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("admin-1");
        currentUser.SetupGet(x => x.IsAdmin).Returns(true);
        var handler = new UpdateUploadSettingsCommandHandler(
            repository.Object, events.Object, currentUser.Object);

        var act = () => handler.Handle(new UpdateUploadSettingsCommand(
            new(4, 10, 9, 0), "correlation-1"), CancellationToken.None);

        await act.Should().ThrowAsync<UploadValidationException>();
        repository.VerifyNoOtherCalls();
        events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_ValidLimits_PersistsAndAudits()
    {
        var repository = new Mock<IUploadSettingsRepository>();
        repository.Setup(x => x.GetPersistedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings?)null);
        repository.Setup(x => x.SaveAsync(
                It.IsAny<UploadSettings>(), 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UploadSettings value, int _, CancellationToken _) =>
            {
                value.ConcurrencyVersion = 1;
                return value;
            });
        var events = new Mock<IProcessingEventRepository>();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns("admin-1");
        currentUser.SetupGet(x => x.IsAdmin).Returns(true);
        var handler = new UpdateUploadSettingsCommandHandler(
            repository.Object, events.Object, currentUser.Object);

        var result = await handler.Handle(new UpdateUploadSettingsCommand(
            new(8, 8_388_608, 209_715_200, 0), "correlation-1"), CancellationToken.None);

        result.ConcurrencyVersion.Should().Be(1);
        events.Verify(x => x.AddAsync(
            It.Is<ProcessingEvent>(e => e.EventType == "upload-settings.updated"
                && e.CorrelationId == "correlation-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
