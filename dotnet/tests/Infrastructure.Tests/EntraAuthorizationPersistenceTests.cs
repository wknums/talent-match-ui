using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;
using System.Text.Json;
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

    [Fact]
    public async Task SyntheticSnapshot_CoversExistingTablesAndDetectsPersistedMutation()
    {
        await using var context = await OpenContextAsync();
        var (user, _, _) = await SeedMembershipScopeAsync(context);
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        await context.Users.Where(item => item.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.FullName, "Changed synthetic profile"));

        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().NotBe(before);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlserver")]
    public void Model_ActiveGroupUniquenessIsSourceAware(string provider)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == "sqlite")
            options.UseSqlite("Data Source=:memory:");
        else
            options.UseSqlServer("Server=synthetic.invalid;Database=synthetic;Integrated Security=true");
        using var context = new AppDbContext(options.Options);

        var assignment = context.Model.FindEntityType(typeof(RoleAssignment))!;
        var index = assignment.GetIndexes().Single(item => item.IsUnique
            && item.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "TenantId", "UserObjectId", "RoleGroupMappingId" }));

        index.GetFilter().Should().Contain("Source")
            .And.Contain("'group'").And.Contain("'active'").And.Contain("IS NOT NULL");
    }

    [Fact]
    public async Task DelegatedAssignments_PermitIndependentOrganizationsDepartmentsAndRoles()
    {
        await using var context = await OpenContextAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        var otherOrganization = new Organization { Name = "Other synthetic organization", UpdatedBy = user.Id };
        var otherDepartment = new Department
        {
            OrganizationId = otherOrganization.Id, Name = "Other department", UpdatedBy = user.Id,
        };
        context.AddRange(otherOrganization, otherDepartment);
        await context.SaveChangesAsync();
        await ActivateAssignmentScopeAsync(context, user, organization, departments);
        await ActivateAssignmentScopeAsync(context, user, otherOrganization, new[] { otherDepartment });
        var assignments = new[]
        {
            CreateDelegatedAssignment(user, organization, departments[0]),
            CreateDelegatedAssignment(user, organization, departments[1]),
            CreateDelegatedAssignment(user, otherOrganization, otherDepartment),
            CreateDelegatedAssignment(user, organization, departments[0]),
            CreateDelegatedAssignment(user, organization, departments[0]),
        };
        assignments[3].Role = "business_panel";
        assignments[4].Role = "organization_admin";
        assignments[4].DepartmentId = null;
        context.RoleAssignments.AddRange(assignments);

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.RoleAssignments.CountAsync()).Should().Be(5);

        var repository = new RoleAssignmentRepository(context);
        foreach (var assignment in assignments)
        {
            var retry = CreateDelegatedAssignment(user, organization, departments[0]);
            retry.Role = assignment.Role;
            retry.OrganizationId = assignment.OrganizationId;
            retry.DepartmentId = assignment.DepartmentId;
            (await repository.ActivateAsync(retry)).Id.Should().Be(assignment.Id);
        }
        (await context.RoleAssignments.CountAsync()).Should().Be(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelegatedAssignments_RejectDuplicateActiveScopeIncludingNullDepartment(bool organizationWide)
    {
        await using var context = await OpenContextAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        await ActivateAssignmentScopeAsync(context, user, organization, departments);
        var first = CreateDelegatedAssignment(user, organization, departments[0]);
        var duplicate = CreateDelegatedAssignment(user, organization, departments[0]);
        if (organizationWide)
        {
            first.Role = duplicate.Role = "business_panel";
            first.DepartmentId = duplicate.DepartmentId = null;
        }
        context.RoleAssignments.Add(first);
        await context.SaveChangesAsync();
        context.RoleAssignments.Add(duplicate);

        var save = () => context.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
        context.ChangeTracker.Clear();
        (await context.RoleAssignments.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("delegated")]
    public async Task Assignments_ActiveUniquenessPreservesMultipleRevokedHistoryRows(string source)
    {
        await using var context = await OpenContextAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        await ActivateAssignmentScopeAsync(context, user, organization, departments);
        var mapping = new RoleGroupMapping
        {
            TenantId = user.EntraTenantId!, GroupObjectId = Guid.NewGuid().ToString(),
            Role = "recruiter", OrganizationId = organization.Id, DepartmentId = departments[0].Id,
            UpdatedBy = user.Id,
        };
        context.RoleGroupMappings.Add(mapping);
        await context.SaveChangesAsync();
        var rows = Enumerable.Range(0, 3).Select(_ =>
        {
            var row = CreateDelegatedAssignment(user, organization, departments[0]);
            row.Source = source;
            row.RoleGroupMappingId = source == "group" ? mapping.Id : null;
            return row;
        }).ToArray();
        foreach (var row in rows.Take(2))
        {
            row.Status = "revoked";
            row.RevokedAt = DateTime.UtcNow;
        }
        context.RoleAssignments.AddRange(rows);
        await context.SaveChangesAsync();

        var duplicate = CreateDelegatedAssignment(user, organization, departments[0]);
        duplicate.Source = source;
        duplicate.RoleGroupMappingId = source == "group" ? mapping.Id : null;
        context.RoleAssignments.Add(duplicate);
        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
        context.ChangeTracker.Clear();
        (await context.RoleAssignments.CountAsync(item => item.Status == "revoked")).Should().Be(2);
        (await context.RoleAssignments.CountAsync(item => item.Status == "active")).Should().Be(1);
    }

    [Theory]
    [InlineData("system_group", false)]
    [InlineData("group", false)]
    [InlineData("delegated", true)]
    [InlineData("bootstrap", true)]
    public async Task Assignments_RejectComputedSourceOrInvalidSourceMappingCombination(string source, bool hasMapping)
    {
        await using var context = await OpenContextAsync();
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        await ActivateAssignmentScopeAsync(context, user, organization, departments);
        var mapping = new RoleGroupMapping
        {
            TenantId = user.EntraTenantId!, GroupObjectId = Guid.NewGuid().ToString(),
            Role = "recruiter", OrganizationId = organization.Id, DepartmentId = departments[0].Id,
            UpdatedBy = user.Id,
        };
        context.Add(mapping);
        await context.SaveChangesAsync();
        var assignment = CreateDelegatedAssignment(user, organization, departments[0]);
        assignment.Source = source;
        assignment.RoleGroupMappingId = hasMapping ? mapping.Id : null;
        context.Add(assignment);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public void Model_MapsPurposeManagementSourceAndTransitionAttribution()
    {
        using var context = CreateContext();
        var mapping = context.Model.FindEntityType(typeof(RoleGroupMapping))!;
        mapping.FindProperty("Purpose").Should().NotBeNull("system and scoped mappings have different authority");
        var assignment = context.Model.FindEntityType(typeof(RoleAssignment))!;
        assignment.FindProperty("ManagementSource").Should().NotBeNull();
        assignment.FindProperty("ManagedTransitionId").Should().NotBeNull();
        assignment.GetForeignKeys().Should().Contain(key =>
            key.PrincipalEntityType.ClrType.Name == "AdministrationTransition"
            && key.Properties.Any(property => property.Name == "ManagedTransitionId")
            && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Theory]
    [InlineData("SystemAdministrationConfiguration", "EnvironmentKey,TenantId,SqlTargetKey,Mode,GroupObjectId,LegacyIndividualObjectId,SystemRoleGroupMappingId,InitialOrganizationId,InitialDepartmentId,AuthorityVersion,CommittedTransitionId,CommittedAt,CommittedBy")]
    [InlineData("AdministrationTransition", "TargetKey,DesiredFingerprint,PriorConfiguration,CandidateConfiguration,Phase,CommittedVersion,CandidateVersion,LeaseOwner,LeaseExpiresAt,FencingToken,ExternalOperations,PriorSqlAdministrator,GrantInventory,StartedAt,UpdatedAt,ActorId,CorrelationId,FailureCode")]
    [InlineData("VerificationReceipt", "TransitionId,CandidateVersion,Mode,PrincipalObjectId,TenantId,MemberObjectId,CollectionStartedAt,CollectedAt,EvidenceExpiresAt,Stack,RuntimeVersion,CapabilityInventoryVersion,SqlTargetKey,SqlAdministratorObjectId,VerifiedAt,ExpiresAt,AuditEventId")]
    [InlineData("ManagedGrantProvenance", "PrincipalObjectId,PrincipalSid,Resource,Permission,ManagementSource,OriginatingTransitionId,AdoptingTransitionId,RetiringTransitionId,BeforeState,AfterState")]
    public void Model_MapsAdministrationLedgerWithoutReusableEvidence(string entityName, string fields)
    {
        using var context = CreateContext();
        var entity = EntraAdministrationTestContract.RequireEntity(context, entityName);
        foreach (var field in fields.Split(','))
            entity.FindProperty(field).Should().NotBeNull($"{entityName}.{field} is required by the administration ledger");
        entity.GetProperties().Select(property => property.Name).Should().NotContain(name =>
            name.Contains("Token", StringComparison.OrdinalIgnoreCase) && name != "FencingToken"
            || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RawClaims", StringComparison.OrdinalIgnoreCase)
            || name.Contains("MemberObjectIds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_ConfigurationGenerationAndTransitionFenceAreConcurrencyTokens()
    {
        using var context = CreateContext();
        var configuration = EntraAdministrationTestContract.RequireEntity(context, "SystemAdministrationConfiguration");
        configuration.FindProperty("AuthorityVersion").Should().NotBeNull();
        configuration.FindProperty("AuthorityVersion")!.IsConcurrencyToken.Should().BeTrue();
        var transition = EntraAdministrationTestContract.RequireEntity(context, "AdministrationTransition");
        transition.FindProperty("FencingToken").Should().NotBeNull();
        transition.FindProperty("FencingToken")!.IsConcurrencyToken.Should().BeTrue();
        foreach (var entity in new[] { configuration, transition })
            entity.GetForeignKeys().Should().NotContain(key => key.DeleteBehavior == DeleteBehavior.Cascade,
                "authority and transition history must survive retirement");
    }

    [Theory]
    [InlineData("Mode", "invalid")]
    [InlineData("GroupObjectId", null)]
    [InlineData("Mode", "individual")]
    [InlineData("SystemRoleGroupMappingId", null)]
    [InlineData("SystemRoleGroupMappingId", "60000000-0000-4000-8000-000000000003")]
    [InlineData("LegacyIndividualObjectId", "60000000-0000-4000-8000-000000000001")]
    [InlineData("AuthorityVersion", -1)]
    [InlineData("InitialDepartmentId", "60000000-0000-4000-8000-000000000002")]
    public async Task Configuration_RejectsInvalidModePrincipalGenerationOrInitialScope(string property, object? value)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        EntraAdministrationTestContract.Set(context, seed.Configuration, property, value);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Configuration_RejectsSecondCommittedRowForSameTarget()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var duplicate = context.Entry(seed.Configuration).CurrentValues.Clone();
        var entity = Activator.CreateInstance(seed.Configuration.GetType())!;
        context.Add(entity).CurrentValues.SetValues(duplicate);
        EntraAdministrationTestContract.Set(context, entity, "Id", Guid.NewGuid().ToString());

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Configuration_IndividualModeStoresNoGroupAuthorityOrMapping()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        EntraAdministrationTestContract.Set(context, seed.Configuration, "Mode", "individual");
        EntraAdministrationTestContract.Set(context, seed.Configuration, "GroupObjectId", null);
        EntraAdministrationTestContract.Set(context, seed.Configuration, "SystemRoleGroupMappingId", null);
        EntraAdministrationTestContract.Set(context, seed.Configuration, "LegacyIndividualObjectId",
            EntraAdministrationTestContract.MemberId);
        await context.SaveChangesAsync();
        EntraAdministrationTestContract.Set(context, seed.Configuration, "LegacyIndividualObjectId", null);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public void Model_ConfigurationInitialDepartmentUsesCompositeParentAndExactSystemMapping()
    {
        using var context = CreateContext();
        var configuration = EntraAdministrationTestContract.RequireEntity(context, "SystemAdministrationConfiguration");
        configuration.GetForeignKeys().Should().Contain(key =>
            key.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { "InitialDepartmentId", "InitialOrganizationId" })
            && key.PrincipalEntityType.ClrType == typeof(Department)
            && key.DeleteBehavior == DeleteBehavior.Restrict);
        configuration.GetForeignKeys().Should().Contain(key =>
            key.Properties.Any(property => property.Name == "SystemRoleGroupMappingId")
            && key.PrincipalEntityType.ClrType == typeof(RoleGroupMapping)
            && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public async Task Configuration_RejectsExistingDepartmentOwnedByDifferentOrganization()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var (_, _, departments) = await SeedMembershipScopeAsync(context);
        EntraAdministrationTestContract.Set(context, seed.Configuration, "InitialDepartmentId", departments[0].Id);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("SystemAdministrationConfiguration", "AuthorityVersion")]
    [InlineData("AdministrationTransition", "FencingToken")]
    public async Task AdministrationConcurrency_RejectsTrackedStaleGenerationOrFence(string name, string field)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var entity = name == "SystemAdministrationConfiguration" ? seed.Configuration : seed.Transition;
        var id = context.Entry(entity).Property("Id").CurrentValue!;
        await using var competing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(context.Database.GetDbConnection()).Options);
        var stale = await competing.FindAsync(entity.GetType(), id);
        stale.Should().NotBeNull();
        EntraAdministrationTestContract.Set(context, entity, field, 2);
        await context.SaveChangesAsync();
        EntraAdministrationTestContract.Set(competing, stale!, field, 2);

        var save = () => competing.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await context.Entry(entity).ReloadAsync();
        Convert.ToInt64(context.Entry(entity).Property(field).CurrentValue).Should().Be(2);
    }

    [Theory]
    [InlineData("CandidateVersion")]
    [InlineData("TransitionId")]
    [InlineData("ExpiresAt")]
    [InlineData("EvidenceExpiresAt")]
    [InlineData("CollectedAt")]
    [InlineData("Mode")]
    public async Task Receipt_RejectsInvalidBindingModeOrEvidenceInterval(string invalid)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var receipt = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-b");
        var value = invalid switch
        {
            "CandidateVersion" => (object)0,
            "TransitionId" => Guid.NewGuid().ToString(),
            "ExpiresAt" => EntraAdministrationTestContract.Now.AddMinutes(6),
            "EvidenceExpiresAt" => EntraAdministrationTestContract.Now.AddMinutes(6),
            "CollectedAt" => EntraAdministrationTestContract.Now.AddMinutes(-2),
            _ => "invalid",
        };
        EntraAdministrationTestContract.Set(context, receipt, invalid, value);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("ManagementSource", "unknown")]
    [InlineData("OriginatingTransitionId", "60000000-0000-4000-8000-000000000004")]
    [InlineData("RetiringTransitionId", "60000000-0000-4000-8000-000000000005")]
    public async Task Provenance_RejectsUnknownManagementOrDanglingTransition(string field, string value)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var provenance = EntraAdministrationTestContract.AddProvenance(context, seed, "deployment");
        EntraAdministrationTestContract.Set(context, provenance, field, value);

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("Phase", "unknown")]
    [InlineData("FencingToken", -1)]
    [InlineData("CandidateVersion", -1)]
    public async Task Transition_RejectsInvalidPhaseVersionOrFence(string property, object value)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var save = async () =>
        {
            EntraAdministrationTestContract.Set(context, seed.Transition, property, value);
            await context.SaveChangesAsync();
        };
        if (property == "CandidateVersion")
        {
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*part of a key*");
            return;
        }
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Transition_OneLiveTargetAllowsCompletedAndRolledBackHistory()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        foreach (var phase in new[] { "complete", "rolled_back" })
        {
            var historic = EntraAdministrationTestContract.AddTransition(context, seed.Scope, phase);
            await context.SaveChangesAsync();
            context.Entry(historic).State = EntityState.Unchanged;
        }
        EntraAdministrationTestContract.AddTransition(context, seed.Scope, "prepared");

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("VerificationReceipt", "TransitionId")]
    [InlineData("ManagedGrantProvenance", "OriginatingTransitionId")]
    public void Model_ReceiptsAndProvenanceHaveRestrictedTransitionReferences(string entityName, string property)
    {
        using var context = CreateContext();
        var entity = EntraAdministrationTestContract.RequireEntity(context, entityName);
        entity.GetForeignKeys().Should().Contain(key =>
            key.Properties.Any(item => item.Name == property)
            && key.PrincipalEntityType.ClrType.Name == "AdministrationTransition"
            && key.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Theory]
    [InlineData("VerificationReceipt", false)]
    [InlineData("VerificationReceipt", true)]
    [InlineData("ManagedGrantProvenance", false)]
    [InlineData("ManagedGrantProvenance", true)]
    public async Task ReceiptAndProvenanceHistory_IsAppendOnly(string entityName, bool delete)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var row = entityName == "VerificationReceipt"
            ? EntraAdministrationTestContract.AddReceipt(context, seed, "stack-b")
            : EntraAdministrationTestContract.AddProvenance(context, seed, "independent");
        await context.SaveChangesAsync();
        if (delete)
            context.Remove(row);
        else
            EntraAdministrationTestContract.Set(context, row,
                entityName == "VerificationReceipt" ? "RuntimeVersion" : "Permission", "tampered");

        var save = () => context.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("ExpectedAuthorityVersion", 0)]
    [InlineData("FencingToken", 0)]
    [InlineData("LeaseOwner", "stale-owner")]
    [InlineData("LeaseExpiresAt", "expired")]
    [InlineData("Phase", "prepared")]
    [InlineData("Receipt", "expired")]
    [InlineData("Receipt", "wrong-candidate")]
    [InlineData("Receipt", "wrong-principal")]
    [InlineData("Receipt", "wrong-mode")]
    [InlineData("Receipt", "wrong-target")]
    [InlineData("Receipt", "missing-stack")]
    [InlineData("Receipt", "different-sql-actor")]
    [InlineData("Receipt", "incomplete-capabilities")]
    [InlineData("Receipt", "sql-not-verified")]
    [InlineData("Receipt", "disabled-identity")]
    [InlineData("Receipt", "disabled-directory")]
    [InlineData("Receipt", "unvalidated-composition")]
    [InlineData("Receipt", "incomplete-directory")]
    [InlineData("Receipt", "wrong-tenant")]
    [InlineData("Receipt", "wrong-sql-admin")]
    [InlineData("Verifier", "locally-disabled-after-receipt")]
    public async Task Commit_RejectsStaleGenerationFenceLeaseOrUnboundReplacementProof(string field, object value)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context, "replacement_verified");
        var first = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-a");
        var second = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-b");
        var sqlReceipt = EntraAdministrationTestContract.AddReceipt(context, seed, "sql");
        if (field == "LeaseExpiresAt")
            EntraAdministrationTestContract.Set(context, seed.Transition, field, EntraAdministrationTestContract.Now.AddSeconds(-1));
        if (field == "Phase")
            EntraAdministrationTestContract.Set(context, seed.Transition, field, value);
        if (field == "Verifier")
            (await context.Users.SingleAsync(user => user.Id == EntraAdministrationTestContract.MemberId)).IsActive = false;
        if (field == "Receipt")
        {
            var (property, replacement) = (string)value switch
            {
                "expired" => ("ExpiresAt", (object)EntraAdministrationTestContract.Now.AddSeconds(-1)),
                "wrong-candidate" => ("CandidateVersion", 99),
                "wrong-principal" => ("PrincipalObjectId", Guid.NewGuid().ToString()),
                "wrong-mode" => ("Mode", "individual"),
                "wrong-target" => ("SqlTargetKey", "other-synthetic-target"),
                "different-sql-actor" => ("MemberObjectId", Guid.NewGuid().ToString()),
                "incomplete-capabilities" => ("CapabilityInventoryPassed", false),
                "sql-not-verified" => ("SqlBootstrapPassed", false),
                "disabled-identity" => ("IsActive", false),
                "disabled-directory" => ("AccountEnabled", false),
                "unvalidated-composition" => ("CompositionValidated", false),
                "incomplete-directory" => ("Complete", false),
                "wrong-tenant" => ("TenantId", Guid.NewGuid().ToString()),
                "wrong-sql-admin" => ("SqlAdministratorObjectId", Guid.NewGuid().ToString()),
                _ => ("Stack", "stack-a"),
            };
            await context.SaveChangesAsync();
            // Deliberately corrupt stored proof to exercise the repository's defense in depth.
            // Normal writes reject these rows through constraints and append-only tracking.
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON");
            try
            {
                var id = context.Entry((string)value == "sql-not-verified" ? sqlReceipt : second).Property("Id").CurrentValue!;
                await context.Database.ExecuteSqlRawAsync(
                    $"UPDATE VerificationReceipts SET \"{property}\" = {{0}} WHERE Id = {{1}}",
                    replacement, id);
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = OFF");
                await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
            }
        }
        await context.SaveChangesAsync();
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);

        var result = await EntraAdministrationTestContract.InvokeAsync(repository, "TryCommitAsync", new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = field == "ExpectedAuthorityVersion" ? value : 1,
            FencingToken = field == "FencingToken" ? value : 1,
            LeaseOwner = field == "LeaseOwner" ? value : "synthetic-owner",
            IncludedStacks = new[] { "stack-a", "stack-b" },
            RequiredRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" },
            CapabilityInventoryVersion = "synthetic-inventory",
            ReceiptIds = new[] { context.Entry(first).Property("Id").CurrentValue, context.Entry(second).Property("Id").CurrentValue,
                context.Entry(sqlReceipt).Property("Id").CurrentValue },
            GroupProof = EntraAdministrationTestContract.GroupEvidence(),
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        });

        result.Should().Be(false);
        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task Commit_RejectsCallerOmittingAnIncludedCandidateRuntime()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context, "replacement_verified");
        var first = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-a");
        var sql = EntraAdministrationTestContract.AddReceipt(context, seed, "sql");
        await context.SaveChangesAsync();
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);
        var result = await EntraAdministrationTestContract.InvokeAsync(repository, "TryCommitAsync", new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = 1, FencingToken = 1, LeaseOwner = "synthetic-owner",
            IncludedStacks = new[] { "stack-a" },
            RequiredRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime" },
            CapabilityInventoryVersion = "synthetic-inventory",
            ReceiptIds = new[] { context.Entry(first).Property("Id").CurrentValue, context.Entry(sql).Property("Id").CurrentValue },
            GroupProof = EntraAdministrationTestContract.GroupEvidence(),
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        });
        result.Should().Be(false);
        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task Commit_AdvancesExactlyOnceAndRetainsScopedWorkloadsDefaultsAndHistory()
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context, "replacement_verified");
        var (user, organization, departments) = await SeedMembershipScopeAsync(context);
        await ActivateAssignmentScopeAsync(context, user, organization, departments);
        context.RoleAssignments.Add(CreateDelegatedAssignment(user, organization, departments[0]));
        var retainedJob = new Job
        {
            Title = "Synthetic retained job", OrganizationId = organization.Id, DepartmentId = departments[0].Id,
            Organisation = organization.Name, Department = departments[0].Name,
        };
        context.Jobs.Add(retainedJob);
        context.Applications.Add(new TalentMatch.Domain.Entities.Application
        {
            JobId = retainedJob.Id, CandidateRef = "preserved-candidate", FinalScore = 83, Status = "Scored",
        });
        var disabled = new User
        {
            Username = "disabled-preserved@example.test", FullName = "Preserved Disabled", AuthenticationProvider = "entra",
            EntraTenantId = EntraAdministrationTestContract.TenantId, EntraObjectId = Guid.NewGuid().ToString(),
            PasswordHash = null, IsActive = false, AuthorizationVersion = 6,
        };
        context.Users.Add(disabled);
        context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = user.Id, EventType = "synthetic.history", EntityType = "User", EntityId = user.Id,
        });
        var first = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-a");
        var second = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-b");
        var sqlReceipt = EntraAdministrationTestContract.AddReceipt(context, seed, "sql");
        await context.SaveChangesAsync();
        var memberships = await context.OrganizationMemberships.AsNoTracking().ToListAsync();
        var jobIds = await context.Jobs.Select(job => job.Id).ToListAsync();
        var applications = await context.Applications.AsNoTracking().ToListAsync();
        var disabledBefore = await context.Users.AsNoTracking().SingleAsync(x => x.Id == disabled.Id);
        var assignmentIds = await context.RoleAssignments.Select(assignment => assignment.Id).ToListAsync();
        var oldAuditIds = await context.ProcessingEvents.Select(audit => audit.Id).ToListAsync();
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);
        var request = new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = 1, FencingToken = 1, LeaseOwner = "synthetic-owner",
            IncludedStacks = new[] { "stack-a", "stack-b" },
            RequiredRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" },
            CapabilityInventoryVersion = "synthetic-inventory",
            ReceiptIds = new[] { context.Entry(first).Property("Id").CurrentValue, context.Entry(second).Property("Id").CurrentValue,
                context.Entry(sqlReceipt).Property("Id").CurrentValue },
            GroupProof = EntraAdministrationTestContract.GroupEvidence(),
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        };

        (await EntraAdministrationTestContract.InvokeAsync(repository, "TryCommitAsync", request)).Should().Be(true);

        context.ChangeTracker.Clear();
        await context.Entry(seed.Configuration).ReloadAsync();
        Convert.ToInt64(context.Entry(seed.Configuration).Property("AuthorityVersion").CurrentValue).Should().Be(2);
        context.Entry(seed.Configuration).Property("GroupObjectId").CurrentValue.Should().Be(EntraAdministrationTestContract.CandidateGroupId);
        context.Entry(seed.Configuration).Property("CommittedTransitionId").CurrentValue.Should().Be(request.TransitionId);
        (await context.OrganizationMemberships.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(memberships);
        (await context.Jobs.Select(job => job.Id).ToListAsync()).Should().BeEquivalentTo(jobIds);
        (await context.Applications.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(applications);
        (await context.Users.AsNoTracking().SingleAsync(x => x.Id == disabled.Id)).Should().BeEquivalentTo(disabledBefore);
        (await context.RoleAssignments.Select(assignment => assignment.Id).ToListAsync()).Should().BeEquivalentTo(assignmentIds);
        (await context.ProcessingEvents.Select(audit => audit.Id).ToListAsync()).Should().Contain(oldAuditIds);
        var committed = await context.ProcessingEvents.SingleAsync(audit => audit.EventType == "auth.admin.configuration.changed");
        committed.CorrelationId.Should().Be(EntraAdministrationTestContract.CorrelationId);
        var after = await EntraAdministrationTestContract.SnapshotAsync(context);

        await EntraAdministrationTestContract.InvokeAsync(repository, "TryCommitAsync", request);

        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(after);
    }

    [Theory]
    [InlineData(0, "synthetic-owner", false)]
    [InlineData(1, "stale-owner", false)]
    [InlineData(1, "synthetic-owner", true)]
    public async Task Checkpoint_RejectsStaleFenceOwnerOrExpiredLease(int fence, string owner, bool expired)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        if (expired)
        {
            EntraAdministrationTestContract.Set(context, seed.Transition, "LeaseExpiresAt",
                EntraAdministrationTestContract.Now.AddSeconds(-1));
            await context.SaveChangesAsync();
        }
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);

        var result = await EntraAdministrationTestContract.InvokeAsync(repository, "TryCheckpointAsync", new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = 1, FencingToken = fence, LeaseOwner = owner,
            ExpectedPhase = "prepared", Phase = "sql_switched",
            ExternalOperations = "[{\"plane\":\"sql\",\"operationId\":\"synthetic-operation\",\"intentRecorded\":true,\"readBackHash\":\"synthetic-hash\",\"completed\":true}]",
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        });

        result.Should().Be(false);
        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(before);
    }

    [Theory]
    [InlineData("authority_committed", "[]")]
    [InlineData("complete", "[]")]
    [InlineData("sql_switched", "[]")]
    [InlineData("sql_switched", "[{\"plane\":\"sql\",\"operationId\":\"synthetic-operation\",\"completed\":true}]")]
    public async Task Checkpoint_RejectsSkippedVerificationOrUnjournaledExternalEffect(string nextPhase, string operations)
    {
        await using var context = await OpenContextAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context);
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);

        var result = await EntraAdministrationTestContract.InvokeAsync(repository, "TryCheckpointAsync", new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = 1, FencingToken = 1, LeaseOwner = "synthetic-owner",
            ExpectedPhase = "prepared", Phase = nextPhase, ExternalOperations = operations,
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        });

        result.Should().Be(false);
        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(before);
    }

    [Fact]
    public async Task AdministrationMutation_AtomicSuccessAuditFailureRollsBackGenerationAndTransition()
    {
        var interceptor = new RejectConfigurationAudit();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:")
            .AddInterceptors(interceptor).Options;
        await using var context = new AppDbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var seed = await EntraAdministrationTestContract.SeedAdministrationAsync(context, "replacement_verified");
        var first = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-a");
        var second = EntraAdministrationTestContract.AddReceipt(context, seed, "stack-b");
        var sqlReceipt = EntraAdministrationTestContract.AddReceipt(context, seed, "sql");
        await context.SaveChangesAsync();
        var before = await EntraAdministrationTestContract.SnapshotAsync(context);
        var repository = EntraAdministrationTestContract.CreateAdministrationRepository(context);
        interceptor.Armed = true;

        var action = () => EntraAdministrationTestContract.InvokeAsync(repository, "TryCommitAsync", new
        {
            TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
            ExpectedAuthorityVersion = 1, FencingToken = 1, LeaseOwner = "synthetic-owner",
            IncludedStacks = new[] { "stack-a", "stack-b" },
            RequiredRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" },
            CapabilityInventoryVersion = "synthetic-inventory",
            ReceiptIds = new[] { context.Entry(first).Property("Id").CurrentValue, context.Entry(second).Property("Id").CurrentValue,
                context.Entry(sqlReceipt).Property("Id").CurrentValue },
            GroupProof = EntraAdministrationTestContract.GroupEvidence(),
            CorrelationId = EntraAdministrationTestContract.CorrelationId,
        });

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic configuration audit failure.");
        interceptor.SawSuccess.Should().BeTrue();
        (await EntraAdministrationTestContract.SnapshotAsync(context)).Should().Be(before);
    }

    private sealed class RejectConfigurationAudit : SaveChangesInterceptor
    {
        internal bool Armed { get; set; }
        internal bool SawSuccess { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<ProcessingEvent>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.EventType == "auth.admin.configuration.changed"))
            {
                SawSuccess = true;
                throw new InvalidOperationException("Synthetic configuration audit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static Task ActivateAssignmentScopeAsync(
        AppDbContext context, User user, Organization organization, Department[] departments) =>
        new OrganizationRepository(context).ActivateMembershipsAsync(
            CreateOrganizationMembership(user.Id, organization.Id),
            departments.Select(department => CreateDepartmentMembership(user.Id, organization.Id, department.Id)).ToArray(),
            departments[0].Id);

    private static async Task<AppDbContext> OpenContextAsync()
    {
        var context = CreateContext();
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        return context;
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

    // These reflection seams keep T005 executable before T007/T008/T013/T015 add the shared contracts.
    // The intended-red gate is a missing mapped invariant/API assertion, never a missing type import.
    internal static class EntraAdministrationTestContract
    {
        internal static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        internal const string TenantId = "10000000-0000-4000-8000-000000000001";
        internal const string GroupId = "71000000-0000-4000-8000-000000000002";
        internal const string CandidateGroupId = "71000000-0000-4000-8000-000000000005";
        internal const string MemberId = "71000000-0000-4000-8000-000000000003";
        internal const string CorrelationId = "71000000-0000-4000-8000-000000000004";
        internal const string TargetKey = "synthetic-environment/sql-target";

        internal static TalentMatch.Domain.Interfaces.DirectoryAdministrationEvidence GroupEvidence() =>
            new(TenantId, CandidateGroupId, 2, MemberId, Now.AddMinutes(-1), Now.AddSeconds(-30),
                Now.AddMinutes(3), true, true, true, "Member", new[] { MemberId }, new[] { MemberId });

        internal sealed record Seed(object Configuration, object Transition, Organization Scope, Department Department);

        internal static IEntityType RequireEntity(AppDbContext context, string name)
        {
            var entity = context.Model.GetEntityTypes().SingleOrDefault(item => item.ClrType.Name == name);
            entity.Should().NotBeNull($"{name} must be mapped before administration persistence can satisfy T005");
            return entity!;
        }

        internal static object Add(AppDbContext context, string name, object values)
        {
            var entity = RequireEntity(context, name);
            var instance = Activator.CreateInstance(entity.ClrType)!;
            context.Add(instance);
            foreach (var property in values.GetType().GetProperties())
                Set(context, instance, property.Name, property.GetValue(values));
            return instance;
        }

        internal static void Set(AppDbContext context, object entity, string name, object? value)
        {
            if (context.Entry(entity).State == EntityState.Detached)
                context.Attach(entity);
            var property = context.Entry(entity).Metadata.FindProperty(name);
            property.Should().NotBeNull($"{entity.GetType().Name}.{name} must persist the contract invariant");
            var type = Nullable.GetUnderlyingType(property!.ClrType) ?? property.ClrType;
            context.Entry(entity).Property(name).CurrentValue = value is null || type.IsInstanceOfType(value)
                ? value
                : Convert.ChangeType(value, type);
        }

        internal static async Task<Seed> SeedAdministrationAsync(AppDbContext context, string phase = "prepared",
            string? organizationId = null, string? departmentId = null)
        {
            RequireEntity(context, "SystemAdministrationConfiguration");
            var organization = organizationId is null
                ? new Organization { Name = "Synthetic initial context", UpdatedBy = MemberId }
                : await context.Organizations.SingleAsync(item => item.Id == organizationId);
            var department = departmentId is null
                ? new Department { OrganizationId = organization.Id, Name = "Initial", UpdatedBy = MemberId }
                : await context.Departments.SingleAsync(item => item.Id == departmentId);
            if (organizationId is null) context.Add(organization);
            if (departmentId is null) context.Add(department);
            var mapping = new RoleGroupMapping
            {
                TenantId = TenantId, GroupObjectId = GroupId, Role = "admin", UpdatedBy = MemberId,
            };
            context.Add(mapping);
            Set(context, mapping, "Purpose", "system_admin");
            var candidateMapping = new RoleGroupMapping
            {
                TenantId = TenantId, GroupObjectId = CandidateGroupId, Role = "admin", UpdatedBy = MemberId,
            };
            context.Add(candidateMapping);
            Set(context, candidateMapping, "Purpose", "system_admin");
            var committedDescriptor = JsonSerializer.Serialize(new
            {
                EnvironmentKey = "synthetic-environment",
                Mode = "group", TenantId, GroupObjectId = GroupId, SystemRoleGroupMappingId = mapping.Id,
                InitialOrganizationId = organization.Id, InitialDepartmentId = department.Id,
                SqlTargetKey = TargetKey, AuthorityVersion = 1,
            });
            var priorTransition = AddTransition(context, organization, "complete");
            Set(context, priorTransition, "CommittedVersion", 0);
            Set(context, priorTransition, "CandidateVersion", 1);
            Set(context, priorTransition, "CandidateConfiguration", committedDescriptor);
            var transition = AddTransition(context, organization, phase);
            Set(context, transition, "PriorConfiguration", committedDescriptor);
            Set(context, transition, "CandidateConfiguration", JsonSerializer.Serialize(new
            {
                EnvironmentKey = "synthetic-environment",
                Mode = "group", TenantId, GroupObjectId = CandidateGroupId, SystemRoleGroupMappingId = candidateMapping.Id,
                InitialOrganizationId = organization.Id, InitialDepartmentId = department.Id,
                SqlTargetKey = TargetKey, AuthorityVersion = 2,
                verificationRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" },
                capabilityInventoryVersion = "synthetic-inventory",
            }));
            await context.SaveChangesAsync();
            var configuration = Add(context, "SystemAdministrationConfiguration", new
            {
                Id = Guid.NewGuid().ToString(), EnvironmentKey = "synthetic-environment",
                TenantId, SqlTargetKey = TargetKey, Mode = "group", GroupObjectId = GroupId,
                LegacyIndividualObjectId = (string?)null, SystemRoleGroupMappingId = mapping.Id,
                InitialOrganizationId = organization.Id, InitialDepartmentId = department.Id,
                AuthorityVersion = 1, CommittedTransitionId = context.Entry(priorTransition).Property("Id").CurrentValue,
                CommittedAt = Now.AddMinutes(-1), CommittedBy = MemberId,
            });
            await context.SaveChangesAsync();
            if (!await context.Users.AnyAsync(user => user.EntraTenantId == TenantId && user.EntraObjectId == MemberId))
            {
                context.Users.Add(new User
                {
                    Id = MemberId, Username = "verifier@example.com", FullName = "Synthetic Verifier",
                    AuthenticationProvider = "entra", EntraTenantId = TenantId, EntraObjectId = MemberId,
                    PasswordHash = null,
                });
                await context.SaveChangesAsync();
            }
            await new OrganizationRepository(context).ActivateMembershipsAsync(
                new OrganizationMembership { UserId = MemberId, OrganizationId = organization.Id, UpdatedBy = MemberId },
                new[]
                {
                    new DepartmentMembership
                    {
                        UserId = MemberId, OrganizationId = organization.Id, DepartmentId = department.Id, UpdatedBy = MemberId,
                    },
                },
                department.Id);
            return new Seed(configuration, transition, organization, department);
        }

        internal static object AddTransition(AppDbContext context, Organization organization, string phase) =>
            Add(context, "AdministrationTransition", new
            {
                Id = Guid.NewGuid().ToString(), TargetKey, DesiredFingerprint = Guid.NewGuid().ToString("N"),
                PriorConfiguration = "{\"mode\":\"individual\",\"authorityVersion\":1}",
                CandidateConfiguration = JsonSerializer.Serialize(new
                {
                    Mode = "group", TenantId, GroupObjectId = GroupId, InitialOrganizationId = organization.Id,
                    SqlTargetKey = TargetKey, AuthorityVersion = 2,
                }),
                Phase = phase, CommittedVersion = 1, CandidateVersion = 2,
                LeaseOwner = "synthetic-owner", LeaseExpiresAt = Now.AddMinutes(2), FencingToken = 1,
                ExternalOperations = "[]", PriorSqlAdministrator = "{}", GrantInventory = "[]",
                StartedAt = Now.AddMinutes(-1), UpdatedAt = Now, ActorId = MemberId, CorrelationId,
            });

        internal static object AddReceipt(AppDbContext context, Seed seed, string stack)
        {
            var audit = new ProcessingEvent
            {
                Actor = MemberId, EventType = "auth.admin.transition.checkpoint",
                EntityType = "AdministrationTransition",
                EntityId = (string)context.Entry(seed.Transition).Property("Id").CurrentValue!,
                CorrelationId = CorrelationId, Timestamp = Now,
            };
            context.ProcessingEvents.Add(audit);
            return Add(context, "VerificationReceipt", new
            {
                Id = Guid.NewGuid().ToString(), TransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
                CandidateVersion = 2, Mode = "group", PrincipalObjectId = CandidateGroupId, TenantId, MemberObjectId = MemberId,
                CollectionStartedAt = Now.AddMinutes(-1), CollectedAt = Now.AddSeconds(-30),
                EvidenceExpiresAt = Now.AddMinutes(4), Complete = true, CompositionValidated = true,
                AccountEnabled = true, UserType = "Member", IsActive = true, Stack = stack,
                RuntimeVersion = "synthetic-runtime", CapabilityInventoryVersion = "synthetic-inventory",
                CapabilityInventoryPassed = true, SqlTargetKey = TargetKey, SqlAdministratorObjectId = CandidateGroupId,
                SqlBootstrapPassed = stack == "sql", VerifiedAt = Now, ExpiresAt = Now.AddMinutes(3),
                AuditEventId = audit.Id, CorrelationId,
            });
    }

        internal static object AddProvenance(AppDbContext context, Seed seed, string source) =>
            Add(context, "ManagedGrantProvenance", new
            {
                Id = Guid.NewGuid().ToString(), PrincipalObjectId = MemberId, PrincipalSid = "synthetic-sid",
                Resource = "synthetic-database", Permission = "bootstrap", ManagementSource = source,
                OriginatingTransitionId = context.Entry(seed.Transition).Property("Id").CurrentValue,
                BeforeState = "{}", AfterState = "{\"present\":true}",
            });

        internal static object CreateAdministrationRepository(AppDbContext context)
        {
            var type = typeof(EntraAccessManagementRepository).Assembly.GetType(
                "TalentMatch.Infrastructure.Persistence.Repositories.AdministrationRepository");
            type.Should().NotBeNull("T013 must provide the fenced administration repository");
            var constructor = type!.GetConstructor(new[] { typeof(AppDbContext), typeof(TimeProvider) });
            constructor.Should().NotBeNull("administration decisions need an injected clock");
            return constructor!.Invoke(new object[] { context, new FixedClock() });
        }

        internal static async Task<object?> InvokeAsync(object repository, string name, object request)
        {
            var method = repository.GetType().GetMethods().SingleOrDefault(item =>
                item.Name == name && item.GetParameters().Length == 2
                && item.GetParameters()[1].ParameterType == typeof(CancellationToken));
            method.Should().NotBeNull($"{repository.GetType().Name}.{name}(request, CancellationToken) must enforce the transaction contract");
            var input = JsonSerializer.Deserialize(JsonSerializer.Serialize(request), method!.GetParameters()[0].ParameterType,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Task task;
            try
            {
                task = (Task)method.Invoke(repository, new[] { input, (object)CancellationToken.None })!;
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        internal static async Task<string> SnapshotAsync(AppDbContext context, bool ignoreLastLogin = false, bool excludeAdministrationDenials = false)
        {
            context.ChangeTracker.Clear();
            var tables = new SortedDictionary<string, List<SortedDictionary<string, object?>>>();
            var connection = context.Database.GetDbConnection();
            foreach (var table in context.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Distinct().Order())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<SortedDictionary<string, object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new SortedDictionary<string, object?>();
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        if (ignoreLastLogin && table == "Users" && reader.GetName(index) == "LastLogin")
                            continue;
                        row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    }
                    if (excludeAdministrationDenials && table == "ProcessingEvents"
                        && (row.GetValueOrDefault("Action")?.ToString() ?? row.GetValueOrDefault("EventType")?.ToString()) == "auth.admin.group.denied")
                        continue;
                    rows.Add(row);
                }
                tables.Add(table, rows.OrderBy(row => JsonSerializer.Serialize(row), StringComparer.Ordinal).ToList());
            }
            return JsonSerializer.Serialize(tables);
        }

        internal sealed class FixedClock : TimeProvider
        {
            private DateTimeOffset _utcNow = new(Now);
            public override DateTimeOffset GetUtcNow() => _utcNow;
            internal void Advance(TimeSpan duration) => _utcNow += duration;
        }
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