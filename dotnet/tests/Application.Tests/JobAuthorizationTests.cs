using FluentAssertions;
using Moq;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs.Commands;
using TalentMatch.Application.Jobs.Queries;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public class JobAuthorizationTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string UserId = "22222222-2222-4222-8222-222222222222";
    private const string OrganizationId = "33333333-3333-4333-8333-333333333333";
    private const string OtherOrganizationId = "44444444-4444-4444-8444-444444444444";
    private const string DepartmentId = "55555555-5555-4555-8555-555555555555";
    private const string OtherDepartmentId = "66666666-6666-4666-8666-666666666666";
    private const string DepartmentMembershipId = "77777777-7777-4777-8777-777777777777";

    private readonly Mock<IJobRepository> _jobRepository = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    [Fact]
    public async Task Create_WithoutNormalizedOrganizationAndDepartment_RejectsJob()
    {
        ConfigureCurrentUser("recruiter", OrganizationId, DepartmentId);
        var handler = new CreateJobCommandHandler(_jobRepository.Object, _currentUser.Object);
        var command = new CreateJobCommand(
            "Scoped role",
            "Engineering",
            "Primary",
            DateTime.UtcNow,
            null,
            null,
            null,
            3,
            "median",
            60,
            75,
            15,
            null,
            null,
            null);

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*organization*department*");
        _jobRepository.Verify(
            repository => repository.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Read_UsesExactRecruiterScope_WhenDepartmentNamesAreDuplicated()
    {
        ConfigureCurrentUser("recruiter", OrganizationId, DepartmentId);
        var visible = CreateJob("visible", OrganizationId, DepartmentId);
        var otherOrganization = CreateJob("other-organization", OtherOrganizationId, OtherDepartmentId);
        var invalidPair = CreateJob("invalid-pair", OrganizationId, OtherDepartmentId);
        _jobRepository
            .Setup(repository => repository.GetByDepartmentAsync("Engineering", It.IsAny<CancellationToken>()))
            .ReturnsAsync([visible, otherOrganization, invalidPair]);
        var handler = new GetJobsQueryHandler(_jobRepository.Object, _currentUser.Object);

        var result = await handler.Handle(new GetJobsQuery(), CancellationToken.None);

        result.Select(item => item.Id).Should().Equal("visible");
    }

    [Fact]
    public async Task Read_AllowsOrganizationScopedAnalyticsOnlyWithinThatOrganization()
    {
        ConfigureCurrentUser("business_panel", OrganizationId, null);
        var firstDepartment = CreateJob("first", OrganizationId, DepartmentId);
        var secondDepartment = CreateJob("second", OrganizationId, OtherDepartmentId);
        var otherOrganization = CreateJob("other-organization", OtherOrganizationId, OtherDepartmentId);
        _jobRepository
            .Setup(repository => repository.GetByDepartmentAsync(string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync([firstDepartment, secondDepartment, otherOrganization]);
        var handler = new GetJobsQueryHandler(_jobRepository.Object, _currentUser.Object);

        var result = await handler.Handle(new GetJobsQuery(), CancellationToken.None);

        result.Select(item => item.Id).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Mutation_AllowsOrganizationAdminInsideAssignedOrganization()
    {
        ConfigureCurrentUser("organization_admin", OrganizationId, null);
        _jobRepository
            .Setup(repository => repository.GetByIdAsync("inside", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("inside", OrganizationId, DepartmentId));
        _jobRepository
            .Setup(repository => repository.DeleteAsync("inside", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var handler = new DeleteJobCommandHandler(_jobRepository.Object, _currentUser.Object);

        var result = await handler.Handle(new DeleteJobCommand("inside"), CancellationToken.None);

        result.Should().BeTrue();
        _jobRepository.Verify(
            repository => repository.DeleteAsync("inside", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Mutation_DeniesOrganizationAdminOutsideAssignedOrganization()
    {
        ConfigureCurrentUser("organization_admin", OrganizationId, null);
        _jobRepository
            .Setup(repository => repository.GetByIdAsync("outside", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("outside", OtherOrganizationId, OtherDepartmentId));
        var handler = new DeleteJobCommandHandler(_jobRepository.Object, _currentUser.Object);

        var act = () => handler.Handle(new DeleteJobCommand("outside"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _jobRepository.Verify(
            repository => repository.GetByIdAsync("outside", It.IsAny<CancellationToken>()),
            Times.Once);
        _jobRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Mutation_DeniesAnalyticsViewerInsideReadableScope()
    {
        ConfigureCurrentUser("business_panel", OrganizationId, DepartmentId);
        _jobRepository
            .Setup(repository => repository.GetByIdAsync("visible", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("visible", OrganizationId, DepartmentId));
        var handler = new DeleteJobCommandHandler(_jobRepository.Object, _currentUser.Object);

        var act = () => handler.Handle(new DeleteJobCommand("visible"), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _jobRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Mutation_RejectsInvalidOrganizationDepartmentPair_EvenForAdmin()
    {
        ConfigureCurrentUser("admin", OrganizationId, DepartmentId);
        _currentUser.SetupGet(service => service.IsAdmin).Returns(true);
        _jobRepository
            .Setup(repository => repository.GetByIdAsync("invalid-pair", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob("invalid-pair", OrganizationId, OtherDepartmentId));
        _jobRepository
            .Setup(repository => repository.DeleteAsync("invalid-pair", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var handler = new DeleteJobCommandHandler(_jobRepository.Object, _currentUser.Object);

        var act = () => handler.Handle(new DeleteJobCommand("invalid-pair"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*organization*department*");
        _jobRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void ConfigureCurrentUser(string role, string scopeOrganizationId, string? scopeDepartmentId)
    {
        _currentUser.SetupGet(service => service.UserId).Returns(UserId);
        _currentUser.SetupGet(service => service.Username).Returns("actor@example.com");
        _currentUser.SetupGet(service => service.Role).Returns(role);
        _currentUser.SetupGet(service => service.Department).Returns(scopeDepartmentId is null ? null : "Engineering");
        _currentUser.SetupGet(service => service.IsAdmin).Returns(role == "admin");
        _currentUser
            .Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAuthorizationState(role, scopeOrganizationId, scopeDepartmentId));
    }

    private static CurrentAuthorizationState CreateAuthorizationState(
        string role,
        string scopeOrganizationId,
        string? scopeDepartmentId)
    {
        var departmentMembership = new DepartmentMembership
        {
            Id = DepartmentMembershipId,
            UserId = UserId,
            OrganizationId = scopeOrganizationId,
            DepartmentId = DepartmentId,
            Status = "active",
        };
        var organizationMembership = new OrganizationMembership
        {
            UserId = UserId,
            OrganizationId = scopeOrganizationId,
            DefaultDepartmentMembershipId = DepartmentMembershipId,
            Status = "active",
        };
        var assignment = new RoleAssignment
        {
            UserId = UserId,
            TenantId = TenantId,
            UserObjectId = UserId,
            Role = role,
            OrganizationId = role == "admin" ? null : scopeOrganizationId,
            DepartmentId = role == "admin" ? null : scopeDepartmentId,
            Source = "delegated",
            Status = "active",
        };

        return new CurrentAuthorizationState(
            new CurrentEntraClaims(
                TenantId,
                UserId,
                "actor@example.com",
                "Authorized Actor",
                "actor@example.com",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                new HashSet<string>(),
                new HashSet<string>()),
            new User
            {
                Id = UserId,
                AuthenticationProvider = "entra",
                EntraTenantId = TenantId,
                EntraObjectId = UserId,
                Username = "actor@example.com",
                FullName = "Authorized Actor",
                IsActive = true,
            },
            [organizationMembership],
            [departmentMembership],
            [assignment],
            [],
            TenantId,
            null);
    }

    private static Job CreateJob(string id, string scopeOrganizationId, string scopeDepartmentId)
        => new()
        {
            Id = id,
            JobCode = id.ToUpperInvariant(),
            Title = $"Job {id}",
            Organisation = scopeOrganizationId == OrganizationId ? "Primary" : "Other",
            Department = "Engineering",
            OrganizationId = scopeOrganizationId,
            DepartmentId = scopeDepartmentId,
            Status = "active",
        };
}