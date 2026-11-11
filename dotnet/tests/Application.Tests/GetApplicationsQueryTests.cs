using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Queries;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class GetApplicationsQueryTests
{
    [Fact]
    public async Task Handle_PreservesFailedApplications_AndHidesUnpublishedUploads()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1" });
        applications.Setup(repo => repo.GetByJobIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Domain.Entities.Application { Id = "failed", Status = "ScoringFailed" },
                new Domain.Entities.Application { Id = "uploading", Status = "Uploading" },
                new Domain.Entities.Application { Id = "test", Status = "Queued", TestRunId = "test-1" },
            ]);

        var result = await new GetApplicationsQueryHandler(applications.Object, jobs.Object).Handle(
            new GetApplicationsQuery("job-1", null, null, null, null, null, 1, 50), CancellationToken.None);

        result.Should().ContainSingle().Which.Id.Should().Be("failed");
        applications.Verify(repo => repo.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeniesJobsOutsideTheCurrentAuthorizationScope()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job
            {
                Id = "job-1",
                OrganizationId = Guid.NewGuid().ToString(),
                DepartmentId = Guid.NewGuid().ToString(),
            });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(
                    "tenant", "user", "user", "User", "user@example.com",
                    DateTimeOffset.UtcNow, new HashSet<string>(), new HashSet<string>()),
                null, [], [], [], [], "tenant", null));
        var handler = new GetApplicationsQueryHandler(
            applications.Object, jobs.Object, currentUser.Object);

        var act = () => handler.Handle(
            new GetApplicationsQuery("job-1", null, null, null, null, null, 1, 50),
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        applications.Verify(
            repo => repo.GetByJobIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
