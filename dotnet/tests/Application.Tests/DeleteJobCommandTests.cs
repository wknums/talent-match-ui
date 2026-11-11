using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class DeleteJobCommandTests
{
    private readonly Mock<IJobRepository> _jobRepoMock = new();
    private readonly Mock<ICurrentUserService> _currentUserMock = new();

    private DeleteJobCommandHandler CreateHandler() =>
        new(_jobRepoMock.Object, _currentUserMock.Object);

    [Fact]
    public async Task Handle_AdminDeletesExistingJob_ReturnsTrue()
    {
        _currentUserMock.Setup(u => u.IsAdmin).Returns(true);
        _jobRepoMock.Setup(r => r.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Test Job" });
        _jobRepoMock.Setup(r => r.DeleteAsync("job-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new DeleteJobCommand("job-1"), CancellationToken.None);

        result.Should().BeTrue();
        _jobRepoMock.Verify(r => r.DeleteAsync("job-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_JobNotFound_ReturnsFalse()
    {
        _currentUserMock.Setup(u => u.IsAdmin).Returns(true);
        _jobRepoMock.Setup(r => r.GetByIdAsync("nonexistent", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        var result = await CreateHandler().Handle(new DeleteJobCommand("nonexistent"), CancellationToken.None);

        result.Should().BeFalse();
        _jobRepoMock.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_NonAdmin_ThrowsUnauthorizedAccessException()
    {
        _currentUserMock.Setup(u => u.IsAdmin).Returns(false);

        var act = () => CreateHandler().Handle(new DeleteJobCommand("job-1"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*administrators*");
    }
}
