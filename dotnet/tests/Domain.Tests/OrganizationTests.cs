using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Tests;

public sealed class OrganizationTests
{
    [Fact]
    public void NewOrganization_WithoutFirstActiveDepartment_IsInvalid()
    {
        var organization = CreateOrganization("organization-a");

        Validate(organization).Should().NotBeEmpty(
            "an organization must be created with its first active department");
    }

    [Fact]
    public void RetiringLastActiveDepartment_IsInvalid()
    {
        var organization = CreateOrganization("organization-a");
        organization.Departments.Add(CreateDepartment(
            "department-a", organization, status: "retired"));

        Validate(organization).Should().NotBeEmpty(
            "an active organization must retain at least one active department");
    }

    [Fact]
    public void Department_ParentOrganization_IsImmutable()
    {
        var organizationProperty = typeof(Department).GetProperty(nameof(Department.OrganizationId));

        organizationProperty.Should().NotBeNull();
        organizationProperty!.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit),
            "a department cannot be moved to another organization after creation");
    }

    [Fact]
    public void RetiringDefaultDepartment_RequiresReplacementFirst()
    {
        var user = CreateUser("user-a");
        var organization = CreateOrganization("organization-a");
        var retiredDepartment = CreateDepartment(
            "department-retired", organization, status: "retired");
        var replacementDepartment = CreateDepartment("department-active", organization);
        var retiredMembership = CreateDepartmentMembership(user, organization, retiredDepartment);
        var replacementMembership = CreateDepartmentMembership(user, organization, replacementDepartment);
        var organizationMembership = CreateOrganizationMembership(
            user, organization, retiredMembership);

        Validate(organizationMembership).Should().NotBeEmpty(
            "the default cannot point at a retired department membership");

        organizationMembership.DefaultDepartmentMembershipId = replacementMembership.Id;
        organizationMembership.DefaultDepartmentMembership = replacementMembership;

        Validate(organizationMembership).Should().BeEmpty(
            "selecting an active replacement before retirement preserves the aggregate");
    }

    [Fact]
    public void ActiveMembership_DefaultFromAnotherOrganization_IsInvalid()
    {
        var user = CreateUser("user-a");
        var organization = CreateOrganization("organization-a");
        var otherOrganization = CreateOrganization("organization-b");
        var foreignDepartment = CreateDepartment("department-b", otherOrganization);
        var foreignMembership = CreateDepartmentMembership(user, otherOrganization, foreignDepartment);
        var organizationMembership = CreateOrganizationMembership(
            user, organization, foreignMembership);

        Validate(organizationMembership).Should().NotBeEmpty(
            "an active default must belong to the same user and organization membership");
    }

    [Fact]
    public void DepartmentMembership_WithForeignDepartmentParent_IsInvalid()
    {
        var user = CreateUser("user-a");
        var organization = CreateOrganization("organization-a");
        var otherOrganization = CreateOrganization("organization-b");
        var foreignDepartment = CreateDepartment("department-b", otherOrganization);
        var membership = CreateDepartmentMembership(user, organization, foreignDepartment);

        Validate(membership).Should().NotBeEmpty(
            "a department membership cannot cross organization boundaries");
    }

    private static IReadOnlyCollection<ValidationResult> Validate(object entity)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(entity, new ValidationContext(entity), results, validateAllProperties: true);
        return results;
    }

    private static Organization CreateOrganization(string id) => new()
    {
        Id = id,
        Name = id,
        UpdatedBy = "actor-a",
    };

    private static Department CreateDepartment(
        string id,
        Organization organization,
        string status = "active") => new()
    {
        Id = id,
        OrganizationId = organization.Id,
        Organization = organization,
        Name = id,
        Status = status,
        UpdatedBy = "actor-a",
    };

    private static User CreateUser(string id) => new()
    {
        Id = id,
        AuthenticationProvider = "entra",
        EntraTenantId = "tenant-a",
        EntraObjectId = id,
        PasswordHash = null,
    };

    private static DepartmentMembership CreateDepartmentMembership(
        User user,
        Organization organization,
        Department department) => new()
    {
        Id = $"membership-{department.Id}",
        UserId = user.Id,
        User = user,
        OrganizationId = organization.Id,
        Organization = organization,
        DepartmentId = department.Id,
        Department = department,
        UpdatedBy = "actor-a",
    };

    private static OrganizationMembership CreateOrganizationMembership(
        User user,
        Organization organization,
        DepartmentMembership defaultMembership) => new()
    {
        Id = $"membership-{organization.Id}",
        UserId = user.Id,
        User = user,
        OrganizationId = organization.Id,
        Organization = organization,
        DefaultDepartmentMembershipId = defaultMembership.Id,
        DefaultDepartmentMembership = defaultMembership,
        UpdatedBy = "actor-a",
    };
}