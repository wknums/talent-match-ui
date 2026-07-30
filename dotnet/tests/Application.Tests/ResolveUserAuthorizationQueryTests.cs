using FluentAssertions;
using Moq;
using TalentMatch.Application.Authorization;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Tests;

public class ResolveUserAuthorizationQueryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ObjectId = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationId = "33333333-3333-3333-3333-333333333333";
    private const string DepartmentId = "44444444-4444-4444-4444-444444444444";
    private const string GroupId = "55555555-5555-5555-5555-555555555555";
    private const string MappingId = "66666666-6666-6666-6666-666666666666";

    [Fact]
    public async Task BootstrapAdmin_WithMatchingRoleAndMemberships_ReturnsGlobalAdminContext()
    {
        var state = CreateState(
            assignments: [CreateAssignment("admin", "bootstrap")],
            roles: new HashSet<string> { "admin" },
            bootstrapObjectId: ObjectId);

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeTrue();
        result.Context!.GlobalRole.Should().Be("admin");
        result.Context.Authorizations.Should().ContainSingle(a =>
            a.Role == "admin" && a.AssignmentSource == "bootstrap");
    }

    [Fact]
    public async Task GroupAssignment_WithMatchingRoleGroupAndMapping_ReturnsScopedAuthorization()
    {
        var assignment = CreateAssignment("recruiter", "group", MappingId);
        var mapping = new RoleGroupMapping
        {
            Id = MappingId,
            TenantId = TenantId,
            GroupObjectId = GroupId,
            Role = "recruiter",
            OrganizationId = OrganizationId,
            DepartmentId = DepartmentId,
            Enabled = true,
        };
        var state = CreateState(
            assignments: [assignment],
            roles: new HashSet<string> { "recruiter" },
            groups: new HashSet<string> { GroupId },
            mappings: [mapping]);

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeTrue();
        result.Context!.Authorizations.Should().ContainSingle(a =>
            a.Role == "recruiter" &&
            a.OrganizationId == OrganizationId &&
            a.DepartmentId == DepartmentId &&
            a.AssignmentSource == "group");
    }

    [Fact]
    public async Task DelegatedAssignment_DoesNotRequireRoleOrGroupClaims()
    {
        var state = CreateState(assignments: [CreateAssignment("business_panel", "delegated")]);

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeTrue();
        result.Context!.Authorizations.Should().ContainSingle(a =>
            a.Role == "business_panel" && a.AssignmentSource == "delegated");
    }

    [Fact]
    public async Task ScopedAssignment_WithoutOrganizationMembership_IsDenied()
    {
        var state = CreateState(
            assignments: [CreateAssignment("recruiter", "delegated")],
            organizationMemberships: [],
            departmentMemberships: []);

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("membership_missing");
        result.Error.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TokenOlderThanFifteenMinutes_IsDeniedAsStale()
    {
        var state = CreateState(
            assignments: [CreateAssignment("recruiter", "delegated")],
            tokenIssuedAt: DateTimeOffset.UtcNow.AddMinutes(-16));

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("token_stale");
        result.Error.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task DepartmentMembership_FromAnotherOrganization_DoesNotAuthorizeScope()
    {
        var foreignOrganizationId = "77777777-7777-7777-7777-777777777777";
        var departmentMembership = CreateDepartmentMembership();
        departmentMembership.OrganizationId = foreignOrganizationId;
        departmentMembership.Organization = new Organization
        {
            Id = foreignOrganizationId,
            Name = "Foreign Organization",
        };

        var state = CreateState(
            assignments: [CreateAssignment("recruiter", "delegated")],
            departmentMemberships: [departmentMembership]);

        var result = await ResolveAsync(state);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("membership_missing");
    }

    private static async Task<AuthorizationResolutionResult> ResolveAsync(CurrentAuthorizationState state)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser
            .Setup(service => service.GetAuthorizationStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
        var handler = new ResolveUserAuthorizationQueryHandler(currentUser.Object);

        return await handler.Handle(new ResolveUserAuthorizationQuery(), CancellationToken.None);
    }

    private static CurrentAuthorizationState CreateState(
        IReadOnlyList<RoleAssignment>? assignments = null,
        IReadOnlySet<string>? roles = null,
        IReadOnlySet<string>? groups = null,
        IReadOnlyList<RoleGroupMapping>? mappings = null,
        IReadOnlyList<OrganizationMembership>? organizationMemberships = null,
        IReadOnlyList<DepartmentMembership>? departmentMemberships = null,
        DateTimeOffset? tokenIssuedAt = null,
        string? bootstrapObjectId = null)
        => new(
            new CurrentEntraClaims(
                TenantId,
                ObjectId,
                tokenIssuedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                roles ?? new HashSet<string>(),
                groups ?? new HashSet<string>()),
            new User
            {
                Id = ObjectId,
                AuthenticationProvider = "entra",
                EntraTenantId = TenantId,
                EntraObjectId = ObjectId,
                Username = "ada@example.com",
                FullName = "Ada Lovelace",
                Email = "ada@example.com",
                PasswordHash = null,
                IsActive = true,
            },
            organizationMemberships ?? [CreateOrganizationMembership()],
            departmentMemberships ?? [CreateDepartmentMembership()],
            assignments ?? [],
            mappings ?? [],
            TenantId,
            bootstrapObjectId);

    private static RoleAssignment CreateAssignment(string role, string source, string? mappingId = null)
        => new()
        {
            UserId = ObjectId,
            TenantId = TenantId,
            UserObjectId = ObjectId,
            Role = role,
            OrganizationId = role == "admin" ? null : OrganizationId,
            DepartmentId = role is "admin" or "organization_admin" ? null : DepartmentId,
            RoleGroupMappingId = mappingId,
            Source = source,
            Status = "active",
        };

    private static OrganizationMembership CreateOrganizationMembership()
        => new()
        {
            UserId = ObjectId,
            OrganizationId = OrganizationId,
            Status = "active",
            Organization = new Organization
            {
                Id = OrganizationId,
                Name = "Analytical Engines",
            },
        };

    private static DepartmentMembership CreateDepartmentMembership()
        => new()
        {
            UserId = ObjectId,
            OrganizationId = OrganizationId,
            DepartmentId = DepartmentId,
            Status = "active",
            Organization = new Organization
            {
                Id = OrganizationId,
                Name = "Analytical Engines",
            },
            Department = new Department
            {
                Id = DepartmentId,
                OrganizationId = OrganizationId,
                Name = "Engineering",
            },
        };
}