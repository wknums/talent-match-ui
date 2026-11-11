using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Stats.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class GetScoringThroughputQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 7, 32, 7, TimeSpan.Zero);

    [Fact]
    public async Task Handle_Returns24ZeroFilledRollingWindows_AndTheSameLastHourAsTheFinalBar()
    {
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new Job { Id = "job-1" }]);
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(value => value.IsAdmin).Returns(true);
        var throughput = new Mock<IScoringThroughputRepository>();
        throughput.Setup(repo => repo.GetHourlyCountsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), Now.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { [7] = 2, [6] = 3 });
        var handler = new GetScoringThroughputQueryHandler(jobs.Object, user.Object, throughput.Object, new FixedClock(Now.AddMilliseconds(500)));

        var result = await handler.Handle(new GetScoringThroughputQuery(), CancellationToken.None);

        result.AsOfUtc.Should().Be(Now);
        result.Hours.Should().HaveCount(24);
        result.Hours[0].StartUtc.Should().Be(Now.AddHours(-24));
        result.Hours[^1].StartUtc.Should().Be(Now.AddHours(-1));
        result.Hours[^1].EndUtc.Should().Be(Now);
        result.Hours.Count(hour => hour.Count == 0).Should().Be(22);
        result.ScoredLastHour.Should().Be(3).And.Be(result.Hours[^1].Count);
        result.ScoredLast24Hours.Should().Be(5);
        result.Hours.Should().OnlyContain(hour => hour.EndUtc - hour.StartUtc == TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Handle_UsesTheSameDepartmentAndCreatorScopeAsDashboardStats()
    {
        var jobs = new Mock<IJobRepository>(MockBehavior.Strict);
        jobs.Setup(repo => repo.GetByDepartmentsOrCreatorAsync(
                It.Is<IEnumerable<string>>(departments => departments.SequenceEqual(new[] { "IT", "Operations" })),
                "user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Job { Id = "visible-job" }]);
        var user = new Mock<ICurrentUserService>();
        user.SetupGet(value => value.Department).Returns("IT, Operations");
        user.SetupGet(value => value.UserId).Returns("user-1");
        var throughput = EmptyThroughput();
        var handler = new GetScoringThroughputQueryHandler(jobs.Object, user.Object, throughput.Object, new FixedClock(Now));

        var result = await handler.Handle(new GetScoringThroughputQuery(), CancellationToken.None);

        throughput.Verify(repo => repo.GetHourlyCountsAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "visible-job" })),
            Now.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
        result.Hours.Should().HaveCount(24).And.OnlyContain(hour => hour.Count == 0);
        result.ScoredLastHour.Should().Be(0);
        result.ScoredLast24Hours.Should().Be(0);
    }

    [Fact]
    public async Task Handle_ExcludesJobsOutsideTheEntraAuthorizationScope()
    {
        var organization = Guid.NewGuid().ToString();
        var department = Guid.NewGuid().ToString();
        var jobs = new Mock<IJobRepository>();
        jobs.Setup(repo => repo.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new Job { Id = "visible", OrganizationId = organization, DepartmentId = department },
            new Job { Id = "hidden", OrganizationId = Guid.NewGuid().ToString(), DepartmentId = department },
        ]);
        var user = new Mock<ICurrentUserService>();
        user.Setup(value => value.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims("tenant", "user", "user", "User", "user@example.com", Now, new HashSet<string>(), new HashSet<string>()),
                null, [], [],
                [new RoleAssignment { Role = "recruiter", Status = "active", OrganizationId = organization, DepartmentId = department }],
                [], "tenant", null));
        var throughput = EmptyThroughput();

        await new GetScoringThroughputQueryHandler(jobs.Object, user.Object, throughput.Object, new FixedClock(Now))
            .Handle(new GetScoringThroughputQuery(), CancellationToken.None);

        throughput.Verify(repo => repo.GetHourlyCountsAsync(
            It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "visible" })),
            Now.UtcDateTime, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IScoringThroughputRepository> EmptyThroughput()
    {
        var repository = new Mock<IScoringThroughputRepository>();
        repository.Setup(repo => repo.GetHourlyCountsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int>());
        return repository;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
