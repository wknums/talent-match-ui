using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class CreateJobCommandTests
{
    private readonly Mock<IJobRepository> _jobRepoMock = new();
    private readonly Mock<ICurrentUserService> _currentUserMock = new();

    [Fact]
    public async Task Handle_ValidCommand_CreatesJobWithConfig()
    {
        Job? createdJob = null;

        _jobRepoMock.Setup(r => r.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((job, _) => createdJob = job)
            .Returns(Task.CompletedTask);
        _jobRepoMock.Setup(r => r.AddConfigVersionAsync(It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _currentUserMock.SetupGet(c => c.UserId).Returns("user-123");

        var handler = new CreateJobCommandHandler(_jobRepoMock.Object, _currentUserMock.Object);
        var command = new CreateJobCommand("Software Engineer", "Engineering", "TechCo", DateTime.Today,
            null, null, null, 3, "median", 70, 85, 15, null, null, null);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        result.Title.Should().Be("Software Engineer");
        result.Department.Should().Be("Engineering");
        result.Status.Should().Be("active");
        result.CurrentConfigVersionId.Should().NotBeNullOrEmpty();
        result.CreatedBy.Should().Be("user-123");
        createdJob.Should().NotBeNull();
        createdJob!.CreatedBy.Should().Be("user-123");
        _jobRepoMock.Verify(r => r.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Once);
        _jobRepoMock.Verify(r => r.AddConfigVersionAsync(It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
