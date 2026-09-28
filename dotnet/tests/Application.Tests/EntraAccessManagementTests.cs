using FluentAssertions;
using Moq;
using TalentMatch.Application.AccessManagement;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Tests;

public sealed class EntraAccessManagementTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string ActorObjectId = "22222222-2222-4222-8222-222222222222";
    private const string TargetObjectId = "33333333-3333-4333-8333-333333333333";
    private const string OrganizationId = "44444444-4444-4444-8444-444444444444";
    private const string OtherOrganizationId = "55555555-5555-4555-8555-555555555555";
    private const string DepartmentId = "66666666-6666-4666-8666-666666666666";
    private const string CorrelationId = "77777777-7777-4777-8777-777777777777";

    [Fact]
    public async Task ListAndDetail_DelegateActorFilteredReads()
    {
        var repository = CreateRepository();
        var listHandler = new ListEntraAccessUsersQueryHandler(repository.Object);
        var detailHandler = new GetEntraAccessUserQueryHandler(repository.Object);

        var page = await listHandler.Handle(
            new ListEntraAccessUsersQuery(OrganizationActor(), "target", OrganizationId, "active", null, 25),
            CancellationToken.None);
        var detail = await detailHandler.Handle(
            new GetEntraAccessUserQuery(OrganizationActor(), TargetObjectId),
            CancellationToken.None);

        page.Items.Should().ContainSingle();
        detail.Organizations.Should().OnlyContain(item => item.OrganizationId == OrganizationId);
        repository.Verify(item => item.ListAsync(
            It.Is<AccessManagementActor>(actor => !actor.GlobalAdmin),
            It.Is<EntraAccessSearch>(search => search.OrganizationId == OrganizationId && search.Limit == 25),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task PutOrganizationAccess_AdminCanConvergeOrganizationAdminRole()
    {
        var repository = CreateRepository();
        var handler = new PutEntraOrganizationAccessCommandHandler(repository.Object);
        var command = new PutEntraOrganizationAccessCommand(
            AdminActor(), TargetObjectId, OrganizationId, PutRequest("organization_admin", null));

        var result = await handler.Handle(command, CancellationToken.None);

        result.AuthorizationVersion.Should().Be(1);
        repository.Verify(item => item.PutOrganizationAccessAsync(
            It.IsAny<AccessManagementActor>(), TargetObjectId, OrganizationId,
            It.Is<PutEntraOrganizationAccess>(request => request.RoleAssignments.Single().Role == "organization_admin"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task PutOrganizationAccess_OrganizationAdminIsLimitedToOwnScopeAndCannotDelegatePeerRole()
    {
        var repository = CreateRepository();
        var handler = new PutEntraOrganizationAccessCommandHandler(repository.Object);
        var foreignScope = new PutEntraOrganizationAccessCommand(
            OrganizationActor(), TargetObjectId, OtherOrganizationId, PutRequest());
        var peerGrant = new PutEntraOrganizationAccessCommand(
            OrganizationActor(), TargetObjectId, OrganizationId, PutRequest("organization_admin", null));
        Func<Task> putForeignScope = () => handler.Handle(foreignScope, CancellationToken.None);
        Func<Task> grantPeerRole = () => handler.Handle(peerGrant, CancellationToken.None);

        await putForeignScope.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "forbidden");
        await grantPeerRole.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "forbidden");
        repository.Verify(item => item.PutOrganizationAccessAsync(
            It.IsAny<AccessManagementActor>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<PutEntraOrganizationAccess>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateUser_DisableAndReactivateRequireGlobalAdmin(bool isActive)
    {
        var repository = CreateRepository();
        var handler = new UpdateEntraAccessUserCommandHandler(repository.Object);
        var denied = new UpdateEntraAccessUserCommand(
            OrganizationActor(), TargetObjectId, new UpdateEntraAccessUserRequestDto(0, null, isActive));
        var allowed = denied with { Actor = AdminActor() };
        Func<Task> updateAsOrganizationAdmin = () => handler.Handle(denied, CancellationToken.None);

        await updateAsOrganizationAdmin.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "forbidden");
        var result = await handler.Handle(allowed, CancellationToken.None);

        result.IsActive.Should().BeTrue();
        repository.Verify(item => item.UpdateUserAsync(
            It.Is<AccessManagementActor>(actor => actor.GlobalAdmin),
            TargetObjectId,
            It.Is<UpdateEntraAccessUser>(request => request.IsActive == isActive),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeRole_OrganizationAdminCanTargetOnlyOwnOrganization()
    {
        var repository = CreateRepository();
        var handler = new RevokeEntraOrganizationRoleCommandHandler(repository.Object);
        var assignmentId = "88888888-8888-4888-8888-888888888888";
        Func<Task> revokeForeignScope = () => handler.Handle(new RevokeEntraOrganizationRoleCommand(
            OrganizationActor(), TargetObjectId, OtherOrganizationId, assignmentId, 0), CancellationToken.None);

        await handler.Handle(new RevokeEntraOrganizationRoleCommand(
            OrganizationActor(), TargetObjectId, OrganizationId, assignmentId, 0), CancellationToken.None);
        await revokeForeignScope.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "forbidden");

        repository.Verify(item => item.RevokeRoleAsync(
            It.IsAny<AccessManagementActor>(), TargetObjectId, OrganizationId, assignmentId, 0,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MutationFailure_RecordsCorrelatedFailureAfterRepositoryRollback()
    {
        var repository = CreateRepository();
        repository.Setup(item => item.PutOrganizationAccessAsync(
                It.IsAny<AccessManagementActor>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<PutEntraOrganizationAccess>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EntraAccessManagementException("version_conflict", "Refresh and retry."));
        var handler = new PutEntraOrganizationAccessCommandHandler(repository.Object);
        Func<Task> put = () => handler.Handle(new PutEntraOrganizationAccessCommand(
            AdminActor(), TargetObjectId, OrganizationId, PutRequest()), CancellationToken.None);

        await put.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "version_conflict");

        repository.Verify(item => item.RecordFailureAuditAsync(
            It.Is<AccessManagementActor>(actor => actor.CorrelationId == CorrelationId),
            TargetObjectId, "put_organization_access", "version_conflict",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void PutValidator_RejectsForeignDefaultDuplicateDepartmentsAndInvalidRecruiterScope()
    {
        var validator = new PutEntraOrganizationAccessCommandValidator();
        var invalid = PutRequest() with
        {
            Membership = new EntraAccessMembershipDto(
                "active", new[] { DepartmentId, DepartmentId }, OtherOrganizationId),
            RoleAssignments = new[] { new EntraDesiredRoleDto("recruiter", null) },
        };

        var result = validator.Validate(new PutEntraOrganizationAccessCommand(
            AdminActor(), TargetObjectId, OrganizationId, invalid));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorCode == "invalid_scope");
    }

    private static Mock<IEntraAccessManagementRepository> CreateRepository()
    {
        var repository = new Mock<IEntraAccessManagementRepository>();
        var aggregate = Aggregate();
        repository.Setup(item => item.ListAsync(
                It.IsAny<AccessManagementActor>(), It.IsAny<EntraAccessSearch>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntraAccessPage(new[] { aggregate }, null));
        repository.Setup(item => item.GetAsync(
                It.IsAny<AccessManagementActor>(), TargetObjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate);
        repository.Setup(item => item.PutOrganizationAccessAsync(
                It.IsAny<AccessManagementActor>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<PutEntraOrganizationAccess>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate with { AuthorizationVersion = 1 });
        repository.Setup(item => item.UpdateUserAsync(
                It.IsAny<AccessManagementActor>(), It.IsAny<string>(),
                It.IsAny<UpdateEntraAccessUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate with { AuthorizationVersion = 1 });
        repository.Setup(item => item.RevokeRoleAsync(
                It.IsAny<AccessManagementActor>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregate with { AuthorizationVersion = 1 });
        return repository;
    }

    private static AccessManagementActorDto AdminActor() =>
        new(TenantId, ActorObjectId, true, Array.Empty<string>(), CorrelationId);

    private static AccessManagementActorDto OrganizationActor() =>
        new(TenantId, ActorObjectId, false, new[] { OrganizationId }, CorrelationId);

    private static PutEntraOrganizationAccessRequestDto PutRequest(
        string role = "recruiter",
        string? departmentId = DepartmentId) => new(
        0,
        new EntraAccessProfileDto("target@example.com", "Target User", "target@example.com"),
        new EntraAccessMembershipDto("active", new[] { DepartmentId }, DepartmentId),
        new[] { new EntraDesiredRoleDto(role, departmentId) });

    private static EntraAccessAggregate Aggregate() => new(
        TargetObjectId,
        "target@example.com",
        "Target User",
        "target@example.com",
        true,
        0,
        new[]
        {
            new EntraOrganizationAccess(
                OrganizationId,
                "active",
                new[] { DepartmentId },
                DepartmentId,
                Array.Empty<EntraAccessRoleAssignment>()),
        });
}