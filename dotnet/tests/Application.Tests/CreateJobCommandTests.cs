using FluentAssertions;
using Moq;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class CreateJobCommandTests
{
    private readonly Mock<IJobRepository> _jobRepoMock = new();

    [Fact]
    public async Task Handle_ValidCommand_CreatesJobWithConfig()
    {
        _jobRepoMock.Setup(r => r.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _jobRepoMock.Setup(r => r.AddConfigVersionAsync(It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateJobCommandHandler(_jobRepoMock.Object);
        var command = new CreateJobCommand("Software Engineer", "Engineering", "TechCo", DateTime.Today,
            null, null, 3, "median", 70, 85, 15);

        var result = await handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        result.Title.Should().Be("Software Engineer");
        result.Department.Should().Be("Engineering");
        result.Status.Should().Be("active");
        result.CurrentConfigVersionId.Should().NotBeNullOrEmpty();
        _jobRepoMock.Verify(r => r.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Once);
        _jobRepoMock.Verify(r => r.AddConfigVersionAsync(It.IsAny<JobConfigVersion>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
