using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Tests;

public sealed class RoleAssignmentTests
{
    [Theory]
    [InlineData("admin", "organization-a", null)]
    [InlineData("organization_admin", null, null)]
    [InlineData("organization_admin", "organization-a", "department-a")]
    [InlineData("recruiter", "organization-a", null)]
    [InlineData("business_panel", null, null)]
    public void DelegatedAssignment_WithInvalidRoleScope_IsInvalid(
        string role,
        string? organizationId,
        string? departmentId)
    {
        var user = CreateUser();
        var assignment = CreateDelegatedAssignment(user, role, organizationId, departmentId);

        Validate(assignment).Should().NotBeEmpty(
            "delegated roles must remain within their required non-global organization scope");
    }

    [Fact]
    public void DelegatedRecruiter_WithoutMatchingMemberships_IsInvalid()
    {
        var user = CreateUser();
        var assignment = CreateDelegatedAssignment(
            user, "recruiter", "organization-a", "department-a");

        Validate(assignment).Should().NotBeEmpty(
            "a delegated scoped assignment requires matching active memberships");
    }

    [Fact]
    public void DelegatedRecruiter_WithDepartmentMembershipInAnotherOrganization_IsInvalid()
    {
        var user = CreateUser();
        AddMemberships(user, "organization-a", "department-a", "organization-b");
        var assignment = CreateDelegatedAssignment(
            user, "recruiter", "organization-a", "department-a");

        Validate(assignment).Should().NotBeEmpty(
            "department authority cannot transfer between organizations");
    }

    [Fact]
    public void DelegatedRecruiter_WithMatchingActiveMemberships_IsValid()
    {
        var user = CreateUser();
        AddMemberships(user, "organization-a", "department-a", "organization-a");
        var assignment = CreateDelegatedAssignment(
            user, "recruiter", "organization-a", "department-a");

        Validate(assignment).Should().BeEmpty();
    }

    private static IReadOnlyCollection<ValidationResult> Validate(object entity)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(entity, new ValidationContext(entity), results, validateAllProperties: true);
        return results;
    }

    private static User CreateUser() => new()
    {
        Id = "user-a",
        AuthenticationProvider = "entra",
        EntraTenantId = "tenant-a",
        EntraObjectId = "object-a",
        PasswordHash = null,
    };

    private static RoleAssignment CreateDelegatedAssignment(
        User user,
        string role,
        string? organizationId,
        string? departmentId) => new()
    {
        Id = "assignment-a",
        UserId = user.Id,
        User = user,
        TenantId = user.EntraTenantId!,
        UserObjectId = user.EntraObjectId!,
        Role = role,
        OrganizationId = organizationId,
        DepartmentId = departmentId,
        Source = "delegated",
        UpdatedBy = "actor-a",
    };

    private static void AddMemberships(
        User user,
        string organizationId,
        string departmentId,
        string departmentOrganizationId)
    {
        var organization = new Organization
        {
            Id = organizationId,
            Name = organizationId,
            UpdatedBy = "actor-a",
        };
        var departmentOrganization = departmentOrganizationId == organizationId
            ? organization
            : new Organization
            {
                Id = departmentOrganizationId,
                Name = departmentOrganizationId,
                UpdatedBy = "actor-a",
            };
        var department = new Department
        {
            Id = departmentId,
            OrganizationId = departmentOrganization.Id,
            Organization = departmentOrganization,
            Name = departmentId,
            UpdatedBy = "actor-a",
        };
        var departmentMembership = new DepartmentMembership
        {
            Id = "department-membership-a",
            UserId = user.Id,
            User = user,
            OrganizationId = departmentOrganization.Id,
            Organization = departmentOrganization,
            DepartmentId = department.Id,
            Department = department,
            UpdatedBy = "actor-a",
        };
        var organizationMembership = new OrganizationMembership
        {
            Id = "organization-membership-a",
            UserId = user.Id,
            User = user,
            OrganizationId = organization.Id,
            Organization = organization,
            DefaultDepartmentMembershipId = departmentMembership.Id,
            DefaultDepartmentMembership = departmentMembership,
            UpdatedBy = "actor-a",
        };

        user.OrganizationMemberships.Add(organizationMembership);
        user.DepartmentMemberships.Add(departmentMembership);
    }
}