using FluentAssertions;
using Moq;
using TalentMatch.Application.BulkScoringRequests;
using TalentMatch.Application.BulkScoringRequests.Services;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests.BulkScoringRequests;

// Who may use bulk scoring on a job: recruiters of the job's department and wider admins can start it,
// business-panel members can only look (403), and anyone else must not learn that the job exists (404).
public sealed class BulkScoringAccessTests
{
    private const string UserId = "11111111-1111-4111-8111-111111111111";
    private const string TenantId = "22222222-2222-4222-8222-222222222222";
    private const string OrganizationId = "33333333-3333-4333-8333-333333333333";
    private const string DepartmentId = "44444444-4444-4444-8444-444444444444";
    private const string OtherDepartmentId = "55555555-5555-4555-8555-555555555555";

    [Theory]
    [InlineData("recruiter", DepartmentId)]
    [InlineData("organization_admin", null)]
    [InlineData("admin", null)]
    public async Task People_who_can_change_the_job_can_start_bulk_scoring(string role, string? department)
    {
        var access = Create(Assignment(role, department));

        var job = await access.RequireJobAsync("job-1", mutate: true, CancellationToken.None);

        job.Id.Should().Be("job-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DepartmentId)]
    public async Task Business_panel_members_can_read_the_job_but_not_start_anything(string? department)
    {
        var access = Create(Assignment("business_panel", department));

        (await access.RequireJobAsync("job-1", mutate: false, CancellationToken.None)).Id.Should().Be("job-1");

        var act = () => access.RequireJobAsync("job-1", mutate: true, CancellationToken.None);
        var error = (await act.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(403);
        error.Code.Should().Be("forbidden");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_in_another_department_looks_like_it_does_not_exist(bool mutate)
    {
        var access = Create(Assignment("recruiter", OtherDepartmentId));

        var act = () => access.RequireJobAsync("job-1", mutate, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<BulkApiException>()).Which;
        error.StatusCode.Should().Be(404);
        error.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task A_job_that_does_not_exist_is_a_404()
    {
        var access = Create(Assignment("recruiter", DepartmentId), jobExists: false);

        var act = () => access.RequireJobAsync("job-1", mutate: true, CancellationToken.None);

        (await act.Should().ThrowAsync<BulkApiException>()).Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task When_the_feature_is_off_every_operation_is_a_503_before_the_job_is_even_looked_up()
    {
        var access = Create(Assignment("recruiter", DepartmentId), mode: "off");

        var act = () => access.RequireJobAsync("job-1", mutate: true, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<BulkApiException>()).Which;
        (error.StatusCode, error.Code).Should().Be((503, "feature_unavailable"));
    }

    [Fact]
    public async Task Status_authorization_does_not_materialize_all_job_applications()
    {
        var jobs = new Mock<IJobRepository>();
        var access = Create(Assignment("recruiter", DepartmentId), jobRepository: jobs);
        jobs.Setup(repository => repository.GetByIdWithoutApplicationsAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = "job-1", OrganizationId = OrganizationId, DepartmentId = DepartmentId });
        jobs.Setup(repository => repository.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The full application collection must not be loaded for status authorization."));

        var result = await access.RequireJobAsync("job-1", mutate: false, CancellationToken.None);

        result.Id.Should().Be("job-1");
        jobs.Verify(repository => repository.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static BulkScoringAccess Create(RoleAssignment assignment, bool jobExists = true, string mode = "mock",
        Mock<IJobRepository>? jobRepository = null)
    {
        var settings = new Dictionary<string, string?> { ["MICROSOFT365_SOURCE_MODE"] = mode, ["AZURE_TENANT_ID"] = TenantId };
        var options = BulkScoringOptions.Create(key => settings.GetValueOrDefault(key), isProduction: false, isDevelopment: true);

        var jobs = jobRepository ?? new Mock<IJobRepository>();
        jobs.Setup(repository => repository.GetByIdAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobExists
                ? new Job { Id = "job-1", Title = "Job", OrganizationId = OrganizationId, DepartmentId = DepartmentId, Status = "active" }
                : null);

        var organizations = new Mock<IOrganizationRepository>();
        organizations.Setup(repository => repository.GetByIdAsync(OrganizationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Organization { Id = OrganizationId, Name = "Contoso", Status = "active" });
        organizations.Setup(repository => repository.GetDepartmentAsync(OrganizationId, DepartmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Department { Id = DepartmentId, OrganizationId = OrganizationId, Name = "HR", Status = "active" });

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(user => user.UserId).Returns(UserId);
        currentUser.SetupGet(user => user.IsAdmin).Returns(assignment.Role == "admin");
        currentUser.Setup(user => user.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentAuthorizationState(
                new CurrentEntraClaims(TenantId, UserId, "actor@example.com", "Actor", "actor@example.com",
                    DateTimeOffset.UtcNow.AddMinutes(-1), new HashSet<string>(), new HashSet<string>()),
                new User { Id = UserId, Username = "actor@example.com", FullName = "Actor", IsActive = true },
                [],
                [],
                [assignment],
                [],
                TenantId,
                null));

        return new BulkScoringAccess(
            options,
            jobs.Object,
            currentUser.Object,
            Mock.Of<IUploadSettingsRepository>(),
            Mock.Of<IBulkImportSettingsRepository>(),
            Mock.Of<ISharePointSource>(),
            Mock.Of<IBulkRequestAuthorizationRepository>(),
            organizations.Object);
    }

    private static RoleAssignment Assignment(string role, string? department) => new()
    {
        UserId = UserId,
        TenantId = TenantId,
        UserObjectId = UserId,
        Role = role,
        OrganizationId = role == "admin" ? null : OrganizationId,
        DepartmentId = role == "admin" ? null : department,
        Source = "delegated",
        Status = "active",
    };
}
