using FluentAssertions;
using Moq;
using TalentMatch.Application.Analytics.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class GetRecruiterAnalyticsQueryTests
{
    private readonly Mock<IUserRepository> _userRepoMock = new();
    private readonly Mock<IJobRepository> _jobRepoMock = new();
    private readonly Mock<IApplicationRepository> _appRepoMock = new();
    private readonly Mock<IRoleAssignmentRepository> _roleRepoMock = new();
    private readonly Mock<IOrganizationRepository> _organizationRepoMock = new();

    private GetRecruiterAnalyticsQueryHandler CreateHandler() =>
        new(_userRepoMock.Object, _jobRepoMock.Object, _appRepoMock.Object,
            _roleRepoMock.Object, _organizationRepoMock.Object);

    [Theory]
    [InlineData("admin", null, 2)]
    [InlineData("recruiter", "u1", 1)]
    public async Task Handle_EntraUsesScopedRecruiterAssignments(
        string callerRole,
        string? callerUserId,
        int expectedRows)
    {
        _userRepoMock.Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new User { Id = "u1", Username = "alice", FullName = "Alice", AuthenticationProvider = "entra" },
                new User { Id = "u2", Username = "bob", FullName = "Bob", AuthenticationProvider = "entra" },
            ]);
        _roleRepoMock.Setup(repository => repository.GetActiveByRoleAsync(
                "tenant", "recruiter", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RoleAssignment { UserId = "u1", TenantId = "tenant", Role = "recruiter", OrganizationId = "org", DepartmentId = "dept-a", Status = "active" },
                new RoleAssignment { UserId = "u2", TenantId = "tenant", Role = "recruiter", OrganizationId = "org", DepartmentId = "dept-b", Status = "active" },
            ]);
        _organizationRepoMock.Setup(repository => repository.GetDepartmentAsync(
                "org", "dept-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Department { Id = "dept-a", OrganizationId = "org", Name = "Sales", Status = "active" });
        _organizationRepoMock.Setup(repository => repository.GetDepartmentAsync(
                "org", "dept-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Department { Id = "dept-b", OrganizationId = "org", Name = "Finance", Status = "active" });
        _jobRepoMock.Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Job { Id = "job-a", CreatedBy = "u1", OrganizationId = "org", DepartmentId = "dept-a", Status = "active" },
                new Job { Id = "job-b", CreatedBy = "u2", OrganizationId = "org", DepartmentId = "dept-b", Status = "active" },
            ]);
        _appRepoMock.Setup(repository => repository.GetByJobIdAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await CreateHandler().Handle(
            new GetRecruiterAnalyticsQuery(callerRole, null, callerUserId ?? "admin-id", "tenant"),
            CancellationToken.None);

        result.Should().HaveCount(expectedRows);
        result.Should().OnlyContain(item => item.ActiveJobs == 1);
        if (callerRole == "recruiter")
            result.Should().ContainSingle(item => item.RecruiterId == "u1" && item.Department == "Sales");
    }

    [Fact]
    public async Task Handle_AdminSees_AllRecruiters()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
            new() { Id = "u2", Role = "recruiter", Department = "Product", FullName = "Bob" },
        };
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Job>());

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", "all"), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(r => r.Department).Should().Contain("Engineering").And.Contain("Product");
    }

    [Fact]
    public async Task Handle_RecruiterSees_OnlyOwnDepartment()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
            new() { Id = "u2", Role = "recruiter", Department = "Product", FullName = "Bob" },
        };
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Job>());

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("recruiter", "Engineering"), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Department.Should().Be("Engineering");
    }

    [Fact]
    public async Task Handle_ExcludesUsersWithoutDepartment()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
            new() { Id = "u2", Role = "recruiter", Department = "", FullName = "NoDept" },
            new() { Id = "u3", Role = "recruiter", Department = null!, FullName = "NullDept" },
        };
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Job>());

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].RecruiterId.Should().Be("u1");
    }

    [Fact]
    public async Task Handle_CountsApplicationMetrics_Correctly()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Username = "alice", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
        };
        var jobs = new List<Job>
        {
            new() { Id = "j1", CreatedBy = "u1", Status = "Active" },
            new() { Id = "j2", CreatedBy = "u1", Status = "Processing" },
            new() { Id = "j3", CreatedBy = "u1", Status = "Closed" },
        };
        var j1Apps = new List<Domain.Entities.Application>
        {
            new() { Id = "a1", JobId = "j1", Status = "Queued" },
            new() { Id = "a2", JobId = "j1", Status = "NeedsManualReview" },
            new() { Id = "a3", JobId = "j1", FinalDecision = "Eligible", Status = "Completed" },
        };
        var j2Apps = new List<Domain.Entities.Application>
        {
            new() { Id = "a4", JobId = "j2", Status = "Queued" },
        };

        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j1", It.IsAny<CancellationToken>())).ReturnsAsync(j1Apps);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j2", It.IsAny<CancellationToken>())).ReturnsAsync(j2Apps);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j3", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Domain.Entities.Application>());
        _appRepoMock.Setup(r => r.GetAggregatedResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AggregatedResult?)null);

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().HaveCount(1);
        var analytics = result[0];
        analytics.ApplicationsInQueue.Should().Be(2); // a1, a4
        analytics.ManualReviewsPerformed.Should().Be(1); // a2
        analytics.ShortlistRecommendations.Should().Be(1); // a3
        analytics.ActiveJobs.Should().Be(2); // j1 (Active) + j2 (Processing)
        analytics.AverageProcessingTime.Should().BeNull(); // No aggregated results
    }

    [Fact]
    public async Task Handle_MatchesJobsCreatedByUsername()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Username = "alice", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
        };
        var jobs = new List<Job>
        {
            new() { Id = "j1", Department = "Engineering", CreatedBy = "alice", Status = "Active" },
        };

        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Domain.Entities.Application>
        {
            new() { Id = "a1", JobId = "j1", Status = "Queued" },
        });
        _appRepoMock.Setup(r => r.GetAggregatedResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AggregatedResult?)null);

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].ApplicationsInQueue.Should().Be(1);
        result[0].ActiveJobs.Should().Be(1);
    }

    [Fact]
    public async Task Handle_AssignsBlankCreatedByToUniqueDepartmentRecruiter()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Username = "alice", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
            new() { Id = "u2", Username = "bob", Role = "recruiter", Department = "Product", FullName = "Bob" },
        };
        var jobs = new List<Job>
        {
            new() { Id = "j1", Department = "Engineering", CreatedBy = "", Status = "Processing" },
        };

        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Domain.Entities.Application>
        {
            new() { Id = "a1", JobId = "j1", Status = "Queued" },
        });
        _appRepoMock.Setup(r => r.GetAggregatedResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AggregatedResult?)null);

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Single(r => r.RecruiterId == "u1").ApplicationsInQueue.Should().Be(1);
        result.Single(r => r.RecruiterId == "u1").ActiveJobs.Should().Be(1);
        result.Single(r => r.RecruiterId == "u2").ApplicationsInQueue.Should().Be(0);
    }

    [Fact]
    public async Task Handle_DoesNotAssignBlankCreatedByWhenDepartmentIsAmbiguous()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Username = "alice", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
            new() { Id = "u2", Username = "betty", Role = "recruiter", Department = "Engineering", FullName = "Betty" },
        };
        var jobs = new List<Job>
        {
            new() { Id = "j1", Department = "Engineering", CreatedBy = "", Status = "Active" },
        };

        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Domain.Entities.Application>
        {
            new() { Id = "a1", JobId = "j1", Status = "Queued" },
        });

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.ActiveJobs == 0 && r.ApplicationsInQueue == 0);
    }

    [Fact]
    public async Task Handle_ExcludesPromptTestRunApplications()
    {
        var users = new List<User>
        {
            new() { Id = "u1", Username = "alice", Role = "recruiter", Department = "Engineering", FullName = "Alice" },
        };
        var jobs = new List<Job>
        {
            new() { Id = "j1", Department = "Engineering", CreatedBy = "u1", Status = "Active" },
        };

        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(users);
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jobs);
        _appRepoMock.Setup(r => r.GetByJobIdAsync("j1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Domain.Entities.Application>
        {
            new() { Id = "a1", JobId = "j1", Status = "Queued" },
            new() { Id = "a2", JobId = "j1", Status = "Queued", TestRunId = "test-run-1" },
        });
        _appRepoMock.Setup(r => r.GetAggregatedResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AggregatedResult?)null);

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].ApplicationsInQueue.Should().Be(1);
    }

    [Fact]
    public async Task Handle_EmptyData_ReturnsEmptyList()
    {
        _userRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<User>());
        _jobRepoMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Job>());

        var handler = CreateHandler();
        var result = await handler.Handle(new GetRecruiterAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().BeEmpty();
    }
}

public class GetDepartmentAnalyticsQueryTests
{
    [Fact]
    public async Task Handle_GroupsByDepartment_AndSumsMetrics()
    {
        var recruiterResults = new List<RecruiterAnalytics>
        {
            new("u1", "Alice", "Engineering", 10, 5, 3, 2.0, 2),
            new("u2", "Bob", "Engineering", 8, 3, 2, null, 1),
            new("u3", "Carol", "Product", 5, 2, 1, 1.5, 1),
        };

        var mediatorMock = new Mock<MediatR.ISender>();
        mediatorMock
            .Setup(m => m.Send(It.IsAny<GetRecruiterAnalyticsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(recruiterResults);

        var handler = new GetDepartmentAnalyticsQueryHandler(mediatorMock.Object);
        var result = await handler.Handle(new GetDepartmentAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().HaveCount(2);

        var eng = result.First(d => d.Department == "Engineering");
        eng.TotalRecruiters.Should().Be(2);
        eng.ApplicationsInQueue.Should().Be(18);
        eng.ManualReviewsPerformed.Should().Be(8);
        eng.ShortlistRecommendations.Should().Be(5);
        eng.ActiveJobs.Should().Be(3);
        eng.Recruiters.Should().HaveCount(2);

        var prod = result.First(d => d.Department == "Product");
        prod.TotalRecruiters.Should().Be(1);
    }

    [Fact]
    public async Task Handle_EmptyRecruiters_ReturnsEmptyList()
    {
        var mediatorMock = new Mock<MediatR.ISender>();
        mediatorMock
            .Setup(m => m.Send(It.IsAny<GetRecruiterAnalyticsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RecruiterAnalytics>());

        var handler = new GetDepartmentAnalyticsQueryHandler(mediatorMock.Object);
        var result = await handler.Handle(new GetDepartmentAnalyticsQuery("admin", null), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsEntraCallerContext()
    {
        var mediatorMock = new Mock<MediatR.ISender>();
        mediatorMock
            .Setup(mediator => mediator.Send(
                It.Is<GetRecruiterAnalyticsQuery>(query =>
                    query.CallerRole == "recruiter"
                    && query.CallerUserId == "user-1"
                    && query.TenantId == "tenant-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var handler = new GetDepartmentAnalyticsQueryHandler(mediatorMock.Object);

        await handler.Handle(
            new GetDepartmentAnalyticsQuery("recruiter", null, "user-1", "tenant-1"),
            CancellationToken.None);

        mediatorMock.VerifyAll();
    }
}
