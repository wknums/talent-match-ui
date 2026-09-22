using FluentAssertions;
using Moq;
using TalentMatch.Application.Applications.Commands;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class RetryDlqItemCommandTests
{
    [Fact]
    public async Task Handle_RetriesSequentialFailureAndWakesTheScoringPool()
    {
        var failures = new Mock<IFailureQueueRepository>();
        failures.Setup(repo => repo.GetByIdAsync("failure-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FailureQueueItem
            {
                Id = "failure-1",
                EntityType = "Application",
                EntityId = "application-1",
            });
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(repo => repo.GetByIdAsync("application-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "application-1",
                JobId = "job-1",
                Status = "ScoringFailed",
            });
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1" });
        var queue = new Mock<ISequentialScoringQueueRepository>();
        queue.Setup(item => item.RetryAsync("failure-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var signal = new Mock<IScoringQueueSignal>();
        var handler = new RetryDlqItemCommandHandler(
            failures.Object,
            applications.Object,
            jobs.Object,
            queue.Object,
            signal.Object);

        var retried = await handler.Handle(
            new RetryDlqItemCommand("failure-1"),
            CancellationToken.None);

        retried.Should().BeTrue();
        queue.Verify(item => item.RetryAsync(
            "failure-1",
            It.IsAny<CancellationToken>()), Times.Once);
        signal.Verify(item => item.Pulse(), Times.Once);
        failures.Verify(item => item.RemoveAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeniesRetryOutsideTheCurrentAuthorizationScope()
    {
        var failures = new Mock<IFailureQueueRepository>();
        failures.Setup(repo => repo.GetByIdAsync("failure-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FailureQueueItem
            {
                Id = "failure-1",
                EntityType = "Application",
                EntityId = "application-1",
            });
        var applications = new Mock<IApplicationRepository>();
        applications.Setup(repo => repo.GetByIdAsync("application-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Domain.Entities.Application
            {
                Id = "application-1",
                JobId = "job-1",
            });
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
        var handler = new RetryDlqItemCommandHandler(
            failures.Object, applications.Object, jobs.Object,
            currentUser: currentUser.Object);

        var act = () => handler.Handle(
            new RetryDlqItemCommand("failure-1"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        applications.Verify(
            repo => repo.UpdateAsync(It.IsAny<Domain.Entities.Application>(), It.IsAny<CancellationToken>()),
            Times.Never);
        failures.Verify(
            repo => repo.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
