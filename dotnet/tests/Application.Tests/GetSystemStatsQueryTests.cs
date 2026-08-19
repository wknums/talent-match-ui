using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Stats.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class GetSystemStatsQueryTests
{
    [Fact]
    public async Task Handle_CountsProductionPipelineFailures()
    {
        var job = new Job
        {
            Id = "job-1",
            Applications =
            {
                new() { Id = "app-1", JobId = "job-1", Status = "ScoringFailed" },
                new() { Id = "app-2", JobId = "job-1", Status = "ExtractionFailed" },
                new() { Id = "app-3", JobId = "job-1", Status = "Completed" },
                new() { Id = "test-app", JobId = "job-1", Status = "ScoringFailed", TestRunId = "test-1" },
            },
        };
        var jobRepository = new Mock<IJobRepository>();
        jobRepository.Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });
        jobRepository.Setup(repository => repository.GetByIdAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.IsAdmin).Returns(true);
        currentUser.Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CurrentAuthorizationState?)null);

        var handler = new GetSystemStatsQueryHandler(jobRepository.Object, currentUser.Object);

        var result = await handler.Handle(new GetSystemStatsQuery(), CancellationToken.None);

        result.Failed.Should().Be(2);
        result.Completed.Should().Be(1);
        result.TotalApplications.Should().Be(3);
    }
}