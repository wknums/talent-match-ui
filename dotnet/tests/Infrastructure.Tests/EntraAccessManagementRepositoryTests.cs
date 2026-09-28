using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;

namespace TalentMatch.Infrastructure.Tests;

public sealed class EntraAccessManagementRepositoryTests
{
    private const string TenantId = "10000000-0000-4000-8000-000000000001";
    private const string ActorObjectId = "10000000-0000-4000-8000-000000000002";
    private const string TargetObjectId = "20000000-0000-4000-8000-000000000001";
    private const string OrganizationId = "30000000-0000-4000-8000-000000000001";
    private const string OtherOrganizationId = "30000000-0000-4000-8000-000000000002";
    private const string DepartmentAId = "40000000-0000-4000-8000-000000000001";
    private const string DepartmentBId = "40000000-0000-4000-8000-000000000002";
    private const string OtherDepartmentId = "40000000-0000-4000-8000-000000000003";
    private const string CorrelationId = "50000000-0000-4000-8000-000000000001";

    [Fact]
    public async Task PutOrganizationAccess_AtomicallyConvergesDefaultRolesVersionAndSuccessAudit()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();

        var aggregate = await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(),
            TargetObjectId,
            OrganizationId,
            ActiveRequest());

        aggregate.AuthorizationVersion.Should().Be(1);
        aggregate.Organizations.Should().ContainSingle(scope =>
            scope.OrganizationId == OrganizationId
            && scope.DepartmentIds.SequenceEqual(new[] { DepartmentAId, DepartmentBId })
            && scope.DefaultDepartmentId == DepartmentAId);

        var organizationMembership = await fixture.Context.OrganizationMemberships
            .SingleAsync(item => item.UserId == fixture.TargetUserId && item.OrganizationId == OrganizationId);
        var defaultMembership = await fixture.Context.DepartmentMemberships
            .SingleAsync(item => item.Id == organizationMembership.DefaultDepartmentMembershipId);
        defaultMembership.UserId.Should().Be(fixture.TargetUserId);
        defaultMembership.OrganizationId.Should().Be(OrganizationId);
        defaultMembership.DepartmentId.Should().Be(DepartmentAId);

        var audit = await fixture.Context.ProcessingEvents.SingleAsync();
        audit.EventType.Should().Be("auth.access.onboarded");
        audit.CorrelationId.Should().Be(CorrelationId);
        audit.PayloadJson.Should().Contain("success");
    }

    [Fact]
    public async Task PutOrganizationAccess_RereadsActorAuthorityAndRollsBackDeniedMutation()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var actorAssignment = await fixture.Context.RoleAssignments
            .SingleAsync(item => item.UserObjectId == ActorObjectId);
        actorAssignment.Status = "revoked";
        actorAssignment.RevokedAt = DateTime.UtcNow;
        await fixture.Context.SaveChangesAsync();

        var action = () => fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(),
            TargetObjectId,
            OrganizationId,
            ActiveRequest());

        await action.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "forbidden");
        (await fixture.Context.Users.SingleAsync(item => item.Id == fixture.TargetUserId))
            .AuthorizationVersion.Should().Be(0);
        (await fixture.Context.OrganizationMemberships.CountAsync(item => item.UserId == fixture.TargetUserId))
            .Should().Be(0);
        (await fixture.Context.ProcessingEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutOrganizationAccess_EquivalentRetryIsIdempotentButDivergentStaleStateConflicts()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());

        var equivalent = await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());
        var divergent = ActiveRequest() with
        {
            Profile = new EntraAccessProfile("changed@example.com", "Changed User", null),
        };
        var conflict = () => fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, divergent);

        equivalent.AuthorizationVersion.Should().Be(1);
        (await fixture.Context.OrganizationMemberships.CountAsync(item => item.UserId == fixture.TargetUserId))
            .Should().Be(1);
        (await fixture.Context.DepartmentMemberships.CountAsync(item => item.UserId == fixture.TargetUserId))
            .Should().Be(2);
        (await fixture.Context.RoleAssignments.CountAsync(item =>
            item.UserId == fixture.TargetUserId && item.Source == "delegated" && item.Status == "active"))
            .Should().Be(1);
        await conflict.Should().ThrowAsync<EntraAccessManagementException>()
            .Where(error => error.Code == "version_conflict");
        (await fixture.Context.Users.SingleAsync(item => item.Id == fixture.TargetUserId)).Username
            .Should().Be("target@example.com");
    }

    [Fact]
    public async Task PutOrganizationAccess_PreservesUnrelatedGroupAndBootstrapAssignments()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedUnrelatedTargetAccessAsync();

        var aggregate = await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());

        aggregate.Organizations.Select(item => item.OrganizationId)
            .Should().BeEquivalentTo(new[] { OrganizationId, OtherOrganizationId });
        var preserved = await fixture.Context.RoleAssignments
            .Where(item => item.UserId == fixture.TargetUserId && item.Status == "active")
            .Select(item => item.Source)
            .ToListAsync();
        preserved.Should().Contain(new[] { "delegated", "group", "bootstrap" });
    }

    [Fact]
    public async Task RevokeRole_RevokesOnlyTheTargetDelegatedAssignment()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedUnrelatedTargetAccessAsync();
        var aggregate = await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());
        var delegatedId = aggregate.Organizations.Single(item => item.OrganizationId == OrganizationId)
            .RoleAssignments.Single(item => item.Source == "delegated").Id;

        var updated = await fixture.Repository.RevokeRoleAsync(
            AdminActor(), TargetObjectId, OrganizationId, delegatedId, 1);

        updated.AuthorizationVersion.Should().Be(2);
        updated.Organizations.Single(item => item.OrganizationId == OrganizationId)
            .RoleAssignments.Should().OnlyContain(item => item.Source == "group");
        (await fixture.Context.RoleAssignments.CountAsync(item =>
            item.UserId == fixture.TargetUserId && item.Source == "bootstrap" && item.Status == "active"))
            .Should().Be(1);
    }

    [Fact]
    public async Task OrganizationAdmin_ReadsOnlyAdministeredScopesAndPendingProfiles()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());
        await fixture.SeedOrganizationAdminAsync();
        await fixture.SeedUnrelatedTargetAccessAsync();

        var page = await fixture.Repository.ListAsync(
            OrganizationActor(), new EntraAccessSearch(null, null, null, null, 25));
        var detail = await fixture.Repository.GetAsync(OrganizationActor(), TargetObjectId);

        page.Items.Should().Contain(item => item.ObjectId == TargetObjectId);
        detail.Should().NotBeNull();
        detail!.Organizations.Should().OnlyContain(item => item.OrganizationId == OrganizationId);
    }

    [Fact]
    public async Task RecordFailureAudit_AfterRollbackPersistsOneCorrelatedOutcome()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var action = () => fixture.Repository.PutOrganizationAccessAsync(
            AdminActor(), TargetObjectId, OrganizationId, ActiveRequest() with
            {
                Membership = new EntraAccessMembership("active", new[] { DepartmentAId }, DepartmentBId),
            });

        await action.Should().ThrowAsync<EntraAccessManagementException>();
        await fixture.Repository.RecordFailureAuditAsync(
            AdminActor(), TargetObjectId, "put_organization_access", "invalid_scope");

        var audit = await fixture.Context.ProcessingEvents.SingleAsync();
        audit.EventType.Should().Be("auth.access.failed");
        audit.CorrelationId.Should().Be(CorrelationId);
        audit.PayloadJson.Should().Contain("invalid_scope");
        (await fixture.Context.Users.SingleAsync(item => item.Id == fixture.TargetUserId))
            .AuthorizationVersion.Should().Be(0);
    }

    private static AccessManagementActor AdminActor() =>
        new(TenantId, ActorObjectId, true, Array.Empty<string>(), CorrelationId);

    private static AccessManagementActor OrganizationActor() =>
        new(TenantId, ActorObjectId, false, new[] { OrganizationId }, CorrelationId);

    private static PutEntraOrganizationAccess ActiveRequest() => new(
        0,
        new EntraAccessProfile("target@example.com", "Target User", "target@example.com"),
        new EntraAccessMembership("active", new[] { DepartmentAId, DepartmentBId }, DepartmentAId),
        new[] { new EntraDesiredRole("recruiter", DepartmentAId) });

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private RepositoryFixture(SqliteConnection connection, AppDbContext context)
        {
            _connection = connection;
            Context = context;
            Repository = new EntraAccessManagementRepository(context);
        }

        public AppDbContext Context { get; }
        public EntraAccessManagementRepository Repository { get; }
        public string ActorUserId { get; } = "10000000-0000-4000-8000-000000000003";
        public string TargetUserId { get; } = "20000000-0000-4000-8000-000000000002";

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var fixture = new RepositoryFixture(connection, context);
            await fixture.SeedBaseAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        public async Task SeedOrganizationAdminAsync()
        {
            var assignment = await Context.RoleAssignments.SingleAsync(item => item.UserId == ActorUserId);
            assignment.Role = "organization_admin";
            assignment.Source = "delegated";
            assignment.OrganizationId = OrganizationId;
            var departmentMembership = new DepartmentMembership
            {
                Id = Guid.NewGuid().ToString(),
                UserId = ActorUserId,
                OrganizationId = OrganizationId,
                DepartmentId = DepartmentAId,
                UpdatedBy = ActorObjectId,
            };
            Context.DepartmentMemberships.Add(departmentMembership);
            Context.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid().ToString(),
                UserId = ActorUserId,
                OrganizationId = OrganizationId,
                DefaultDepartmentMembershipId = departmentMembership.Id,
                DefaultDepartmentMembership = departmentMembership,
                UpdatedBy = ActorObjectId,
            });
            await Context.SaveChangesAsync();
        }

        public async Task SeedUnrelatedTargetAccessAsync()
        {
            var otherDepartmentMembership = new DepartmentMembership
            {
                Id = Guid.NewGuid().ToString(),
                UserId = TargetUserId,
                OrganizationId = OtherOrganizationId,
                DepartmentId = OtherDepartmentId,
                UpdatedBy = ActorObjectId,
            };
            Context.DepartmentMemberships.Add(otherDepartmentMembership);
            Context.OrganizationMemberships.Add(new OrganizationMembership
            {
                Id = Guid.NewGuid().ToString(),
                UserId = TargetUserId,
                OrganizationId = OtherOrganizationId,
                DefaultDepartmentMembershipId = otherDepartmentMembership.Id,
                UpdatedBy = ActorObjectId,
            });
            Context.RoleAssignments.AddRange(
                new RoleAssignment
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = TargetUserId,
                    TenantId = TenantId,
                    UserObjectId = TargetObjectId,
                    Role = "business_panel",
                    OrganizationId = OtherOrganizationId,
                    DepartmentId = OtherDepartmentId,
                    Source = "delegated",
                    UpdatedBy = ActorObjectId,
                },
                new RoleAssignment
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = TargetUserId,
                    TenantId = TenantId,
                    UserObjectId = TargetObjectId,
                    Role = "business_panel",
                    OrganizationId = OrganizationId,
                    DepartmentId = DepartmentBId,
                    Source = "group",
                    RoleGroupMappingId = null,
                    UpdatedBy = ActorObjectId,
                },
                new RoleAssignment
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = TargetUserId,
                    TenantId = TenantId,
                    UserObjectId = TargetObjectId,
                    Role = "admin",
                    Source = "bootstrap",
                    UpdatedBy = ActorObjectId,
                });
            await Context.SaveChangesAsync();
        }

        private async Task SeedBaseAsync()
        {
            Context.Users.AddRange(
                new User
                {
                    Id = ActorUserId,
                    Username = "admin@example.com",
                    FullName = "Admin User",
                    Email = "admin@example.com",
                    AuthenticationProvider = "entra",
                    EntraTenantId = TenantId,
                    EntraObjectId = ActorObjectId,
                    PasswordHash = null,
                },
                new User
                {
                    Id = TargetUserId,
                    Username = "target@example.com",
                    FullName = "Target User",
                    Email = "target@example.com",
                    AuthenticationProvider = "entra",
                    EntraTenantId = TenantId,
                    EntraObjectId = TargetObjectId,
                    PasswordHash = null,
                });
            Context.Organizations.AddRange(
                new Organization { Id = OrganizationId, Name = "Primary", UpdatedBy = ActorObjectId },
                new Organization { Id = OtherOrganizationId, Name = "Other", UpdatedBy = ActorObjectId });
            Context.Departments.AddRange(
                new Department { Id = DepartmentAId, OrganizationId = OrganizationId, Name = "A", UpdatedBy = ActorObjectId },
                new Department { Id = DepartmentBId, OrganizationId = OrganizationId, Name = "B", UpdatedBy = ActorObjectId },
                new Department { Id = OtherDepartmentId, OrganizationId = OtherOrganizationId, Name = "Other", UpdatedBy = ActorObjectId });
            Context.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid().ToString(),
                UserId = ActorUserId,
                TenantId = TenantId,
                UserObjectId = ActorObjectId,
                Role = "admin",
                Source = "bootstrap",
                UpdatedBy = ActorObjectId,
            });
            await Context.SaveChangesAsync();
        }
    }
}