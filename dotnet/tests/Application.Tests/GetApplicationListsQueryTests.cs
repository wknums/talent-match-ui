using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using ApplicationEntity = TalentMatch.Domain.Entities.Application;

namespace TalentMatch.Application.Tests;

public sealed class GetApplicationListsQueryTests
{
    [Fact]
    public async Task ReviewList_ReturnsOnlyApplicationsFlaggedForManualReview()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = JobRepository();
        var manualReview = new ApplicationEntity
        {
            Id = "review-1",
            JobId = "job-1",
            Status = "NeedsManualReview",
            FinalDecision = "NeedsManualReview",
        };
        applications.Setup(repository => repository.GetByJobIdAsync(
                "job-1",
                "review",
                70,
                85,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([manualReview]);
        var handler = new GetApplicationsQueryHandler(
            applications.Object, jobs.Object);

        var result = await handler.Handle(
            new GetApplicationsQuery(
                "job-1", "review", null, null, null, null, 1, 50),
            CancellationToken.None);

        result.Should().ContainSingle().Which.Id.Should().Be("review-1");
        applications.Verify(repository => repository.GetByJobIdAsync(
            "job-1",
            "review",
            70,
            85,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Summary_ReturnsRepositoryAggregatesWithoutLoadingApplications()
    {
        var applications = new Mock<IApplicationRepository>();
        var jobs = JobRepository();
        var counts = new ApplicationCounts(
            Total: 299,
            Pending: 12,
            Uploading: 3,
            Shortlist: 21,
            Longlist: 87,
            Excluded: 170,
            Review: 9,
            Queued: 7,
            Scoring: 5,
            Complete: 241,
            Failed: 1);
        applications.Setup(repository => repository.GetCountsByJobIdAsync(
                "job-1",
                70,
                85,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(counts);
        var handler = new GetApplicationCountsQueryHandler(
            applications.Object, jobs.Object);

        var result = await handler.Handle(
            new GetApplicationCountsQuery("job-1"),
            CancellationToken.None);

        result.Should().Be(counts);
        applications.Verify(repository => repository.GetByJobIdAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IJobRepository> JobRepository()
    {
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repository => repository.GetByIdWithoutApplicationsAsync(
                "job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", Title = "Test Job" });
        return jobs;
    }
}
