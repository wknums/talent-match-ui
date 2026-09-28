using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class EntraAuthorizationPersistenceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void Model_MapsAuthorizationVersionAndCompositeDefaultMembership()
    {
        using var context = CreateContext();

        var user = context.Model.FindEntityType(typeof(User))!;
        var organizationMembership = context.Model.FindEntityType(typeof(OrganizationMembership))!;
        var departmentMembership = context.Model.FindEntityType(typeof(DepartmentMembership))!;

        user.FindProperty("AuthorizationVersion").Should().NotBeNull();
        organizationMembership.FindProperty("DefaultDepartmentMembershipId").Should().NotBeNull();
        departmentMembership.GetKeys()
            .Should().Contain(key => key.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "Id", "UserId", "OrganizationId" }));
        organizationMembership.GetForeignKeys()
            .Should().Contain(key => key.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "DefaultDepartmentMembershipId", "UserId", "OrganizationId" }));
    }

    [Fact]
    public void Model_RestrictsDefaultDeletionAndAllowsRevokedAssignmentHistory()
    {
        using var context = CreateContext();

        var organizationMembership = context.Model.FindEntityType(typeof(OrganizationMembership))!;
        var roleAssignment = context.Model.FindEntityType(typeof(RoleAssignment))!;

        var defaultForeignKey = organizationMembership.GetForeignKeys().Single(key =>
            key.Properties.Any(property => property.Name == "DefaultDepartmentMembershipId"));
        defaultForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        roleAssignment.GetIndexes().Should().Contain(index => HasActiveDelegatedFilter(index.GetFilter()));
    }

    [Fact]
    public async Task ActivateMemberships_PersistsAllDepartmentsAndExplicitDefault()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var repository = new OrganizationRepository(context);
        var organizationMembership = CreateOrganizationMembership(user.Id, organization.Id);
        var departmentMemberships = departments
            .Select(department => CreateDepartmentMembership(user.Id, organization.Id, department.Id))
            .ToArray();

        await repository.ActivateMembershipsAsync(
            organizationMembership,
            departmentMemberships,
            departments[1].Id);

        var persisted = await context.OrganizationMemberships.SingleAsync();
        persisted.DefaultDepartmentMembershipId.Should().Be(departmentMemberships[1].Id);
        (await context.DepartmentMemberships.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ActivateMemberships_RejectsDefaultOutsideTransition()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var repository = new OrganizationRepository(context);
        var organizationMembership = CreateOrganizationMembership(user.Id, organization.Id);
        var departmentMembership = CreateDepartmentMembership(user.Id, organization.Id, departments[0].Id);

        var action = () => repository.ActivateMembershipsAsync(
            organizationMembership,
            new[] { departmentMembership },
            departments[1].Id);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Default department must be part of the active membership transition.");
        (await context.OrganizationMemberships.CountAsync()).Should().Be(0);
        (await context.DepartmentMemberships.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReplaceDefaultDepartment_SelectsAnotherActiveMembership()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var repository = new OrganizationRepository(context);
        var departmentMemberships = departments
            .Select(department => CreateDepartmentMembership(user.Id, organization.Id, department.Id))
            .ToArray();
        await repository.ActivateMembershipsAsync(
            CreateOrganizationMembership(user.Id, organization.Id),
            departmentMemberships,
            departments[0].Id);

        await repository.ReplaceDefaultDepartmentAsync(user.Id, organization.Id, departments[1].Id, user.Id);

        context.ChangeTracker.Clear();
        var persisted = await context.OrganizationMemberships.SingleAsync();
        persisted.DefaultDepartmentMembershipId.Should().Be(departmentMemberships[1].Id);
    }

    [Fact]
    public async Task RevokeMemberships_RevokesOrganizationAndAllActiveDepartments()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var repository = new OrganizationRepository(context);
        var departmentMemberships = departments
            .Select(department => CreateDepartmentMembership(user.Id, organization.Id, department.Id))
            .ToArray();
        await repository.ActivateMembershipsAsync(
            CreateOrganizationMembership(user.Id, organization.Id),
            departmentMemberships,
            departments[0].Id);

        await repository.RevokeMembershipsAsync(user.Id, organization.Id, user.Id);

        context.ChangeTracker.Clear();
        var organizationMembership = await context.OrganizationMemberships.SingleAsync();
        organizationMembership.Status.Should().Be("revoked");
        organizationMembership.RevokedAt.Should().NotBeNull();
        var persistedDepartments = await context.DepartmentMemberships.ToListAsync();
        persistedDepartments.Should().OnlyContain(membership =>
            membership.Status == "revoked" && membership.RevokedAt.HasValue);
    }

    [Fact]
    public async Task AdvanceAuthorizationVersion_OnlyExpectedVersionSucceeds()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, _, _) = await SeedMembershipScopeAsync(context);
        var repository = new UserRepository(context);

        (await repository.TryAdvanceAuthorizationVersionAsync(user.Id, 0)).Should().BeTrue();
        (await repository.TryAdvanceAuthorizationVersionAsync(user.Id, 0)).Should().BeFalse();

        context.ChangeTracker.Clear();
        (await context.Users.SingleAsync()).AuthorizationVersion.Should().Be(1);
    }

    [Fact]
    public async Task ActivateEquivalentAssignment_DoesNotCreateDuplicate()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var repository = new RoleAssignmentRepository(context);
        var assignment = CreateDelegatedAssignment(user, organization, departments[0]);
        var equivalent = CreateDelegatedAssignment(user, organization, departments[0]);

        await repository.ActivateAsync(assignment);
        await repository.ActivateAsync(equivalent);

        (await context.RoleAssignments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RepositoryMutations_ParticipateInExistingTransaction()
    {
        await using var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var userRepository = new UserRepository(context);
        var roleAssignmentRepository = new RoleAssignmentRepository(context);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            (await userRepository.TryAdvanceAuthorizationVersionAsync(user.Id, 0)).Should().BeTrue();
            await roleAssignmentRepository.ActivateAsync(CreateDelegatedAssignment(user, organization, departments[0]));
            await transaction.RollbackAsync();
        }

        context.ChangeTracker.Clear();
        (await context.Users.SingleAsync()).AuthorizationVersion.Should().Be(0);
        (await context.RoleAssignments.CountAsync()).Should().Be(0);
    }

    private static async Task<(User User, Organization Organization, Department[] Departments)> SeedMembershipScopeAsync(AppDbContext context)
    {
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            Username = "entra-user",
            FullName = "Entra User",
            AuthenticationProvider = "entra",
            EntraTenantId = Guid.NewGuid().ToString(),
            EntraObjectId = Guid.NewGuid().ToString(),
            PasswordHash = null,
            IsActive = true,
        };
        var organization = new Organization
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Contoso",
            UpdatedBy = user.Id,
        };
        var departments = new[]
        {
            new Department { Id = Guid.NewGuid().ToString(), OrganizationId = organization.Id, Name = "Engineering", UpdatedBy = user.Id },
            new Department { Id = Guid.NewGuid().ToString(), OrganizationId = organization.Id, Name = "Finance", UpdatedBy = user.Id },
        };
        context.AddRange(user, organization);
        context.Departments.AddRange(departments);
        await context.SaveChangesAsync();
        return (user, organization, departments);
    }

    private static OrganizationMembership CreateOrganizationMembership(string userId, string organizationId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserId = userId,
        OrganizationId = organizationId,
        UpdatedBy = userId,
    };

    private static DepartmentMembership CreateDepartmentMembership(string userId, string organizationId, string departmentId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserId = userId,
        OrganizationId = organizationId,
        DepartmentId = departmentId,
        UpdatedBy = userId,
    };

    private static RoleAssignment CreateDelegatedAssignment(User user, Organization organization, Department department) => new()
    {
        Id = Guid.NewGuid().ToString(),
        UserId = user.Id,
        TenantId = user.EntraTenantId!,
        UserObjectId = user.EntraObjectId!,
        Role = "recruiter",
        OrganizationId = organization.Id,
        DepartmentId = department.Id,
        Source = "delegated",
        UpdatedBy = user.Id,
    };

    private static bool HasActiveDelegatedFilter(string? filter) =>
        filter is not null
        && filter.Contains("Status", StringComparison.OrdinalIgnoreCase)
        && filter.Contains("delegated", StringComparison.OrdinalIgnoreCase);
}