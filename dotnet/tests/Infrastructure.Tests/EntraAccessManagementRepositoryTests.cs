using FluentAssertions;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using EntraAdministrationTestContract = TalentMatch.Infrastructure.Tests.EntraAuthorizationPersistenceTests.EntraAdministrationTestContract;

namespace TalentMatch.Infrastructure.Tests;

public sealed class EntraAccessManagementRepositoryTests
{
    [Fact]
    public async Task Node_and_dotnet_concurrent_first_access_share_one_identity_context_default_and_atomic_audit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "package.json"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(Path.GetTempPath(), $"talentmatch-cross-stack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "shared.sqlite");
        try
        {
            await using var fixture = await RepositoryFixture.CreateAsync(databasePath: path);
            await fixture.SeedAutomaticConfigurationAsync();
            var user = await fixture.Context.Users.SingleAsync(x => x.EntraObjectId == TargetObjectId);
            fixture.Context.Users.Remove(user);
            await fixture.Context.SaveChangesAsync();
            var request = AutomaticRequest() with { ExpectedAuthorizationVersion = 0 };
            var input = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            input["configurationId"] = (await fixture.Context.SystemAdministrationConfigurations.SingleAsync()).Id;
            var json = input.ToJsonString();
            var start = new ProcessStartInfo("node") { WorkingDirectory = root!.FullName,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--import"); start.ArgumentList.Add("tsx");
            start.ArgumentList.Add("tests/integration/converge-group-context.mjs");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            start.Environment["STORAGE_PROVIDER"] = "local";
            start.Environment["SQLITE_DB_PATH"] = path;
            using var node = Process.Start(start)!;
            var errors = node.StandardError.ReadToEndAsync();
            try
            {
                string? line;
                do { line = await node.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                while (line is not null && line != "READY");
                Assert.Equal("READY", line);
                var dotnet = fixture.ConvergeAutomaticAsync(request);
                await node.StandardInput.WriteLineAsync("GO");
                await node.StandardInput.FlushAsync();
                node.StandardInput.Close();
                await dotnet;
                await node.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(node.ExitCode == 0, await errors);
                Assert.Contains("CONVERGED", await node.StandardOutput.ReadToEndAsync());
                fixture.Context.ChangeTracker.Clear();
                var converged = await fixture.Context.Users.SingleAsync(x => x.EntraObjectId == TargetObjectId);
                await fixture.AssertAutomaticScopeAsync(converged.Id, DepartmentAId);
                Assert.Equal(1, await fixture.Context.OrganizationMemberships.CountAsync(x => x.UserId == converged.Id));
                Assert.False(await fixture.Context.RoleAssignments.AnyAsync(x => x.UserId == converged.Id && x.Role == "admin"));
                Assert.Equal(1, await fixture.Context.ProcessingEvents.CountAsync(x => x.EventType == "auth.admin.context.provisioned"));
            }
            finally { if (!node.HasExited) { node.Kill(entireProcessTree: true); await node.WaitForExitAsync(); } }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
            Directory.Delete(directory);
        }
    }
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
    public async Task ComputedAdmin_ManualLifecycleNeedsNoPersonalGrantAndPreservesDisabledIdentity()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        fixture.Context.RoleAssignments.RemoveRange(await fixture.Context.RoleAssignments
            .Where(x => x.UserId == fixture.ActorUserId).ToListAsync());
        await fixture.Context.SaveChangesAsync();
        var admin = AdminActor();
        (await fixture.Repository.ListAsync(admin, new(null, null, "pending", null, 1))).Items.Should().HaveCount(1);
        (await fixture.Repository.GetAsync(admin, TargetObjectId))!.Organizations.Should().BeEmpty();
        await fixture.Repository.PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, ActiveRequest());
        for (var repeat = 0; repeat < 2; repeat++)
            (await fixture.Repository.PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, ActiveRequest()))
                .AuthorizationVersion.Should().Be(1);
        await using (var first = fixture.CreateConcurrentContext(new BeforeAutomaticTransaction()))
        await using (var second = fixture.CreateConcurrentContext(new BeforeAutomaticTransaction()))
        {
            var results = await Task.WhenAll(
                new EntraAccessManagementRepository(first).PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, ActiveRequest()),
                new EntraAccessManagementRepository(second).PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, ActiveRequest()));
            results.Should().OnlyContain(x => x.AuthorizationVersion == 1);
        }
        fixture.Context.ChangeTracker.Clear();
        var replacement = ActiveRequest() with
        {
            ExpectedVersion = 1,
            Membership = new("active", [DepartmentBId], DepartmentBId),
            RoleAssignments = [new("recruiter", DepartmentBId)],
        };
        var updated = await fixture.Repository.PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, replacement);
        updated.AuthorizationVersion.Should().Be(2);
        updated.Organizations.Single().DefaultDepartmentId.Should().Be(DepartmentBId);
        var assignmentId = updated.Organizations.Single().RoleAssignments.Single().Id;
        await fixture.Repository.RevokeRoleAsync(admin, TargetObjectId, OrganizationId, assignmentId, 2);
        (await fixture.Repository.RevokeRoleAsync(admin, TargetObjectId, OrganizationId, assignmentId, 2)).AuthorizationVersion.Should().Be(3);
        await fixture.Repository.UpdateUserAsync(admin, TargetObjectId, new(3, null, false));
        var disabled = await fixture.Repository.PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId,
            replacement with { ExpectedVersion = 4, RoleAssignments = [] });
        disabled.IsActive.Should().BeFalse();
        disabled.AuthorizationVersion.Should().Be(4);
        (await fixture.Repository.UpdateUserAsync(admin, TargetObjectId, new(4, null, true))).IsActive.Should().BeTrue();
        (await fixture.Context.RoleAssignments.CountAsync(x => x.Role == "admin")).Should().Be(0);
        (await fixture.Context.ProcessingEvents.CountAsync(x => x.CorrelationId == CorrelationId)).Should().Be(11);
        Func<Task> unknown = () => fixture.Repository.PutOrganizationAccessAsync(admin, OtherDepartmentId, OrganizationId, ActiveRequest());
        await unknown.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "not_found");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("mapping")]
    [InlineData("expired")]
    [InlineData("missing")]
    public async Task ComputedAdmin_RevalidatesAllReadsAndMutations(string invalid)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        var admin = AdminActor();
        if (invalid == "disabled") await fixture.Context.Database.ExecuteSqlRawAsync(
            $"UPDATE Users SET IsActive=0 WHERE Id='{fixture.ActorUserId}'");
        if (invalid == "version") await fixture.Context.Database.ExecuteSqlRawAsync(
            $"UPDATE Users SET AuthorizationVersion=1 WHERE Id='{fixture.ActorUserId}'");
        if (invalid == "generation") await fixture.Context.Database.ExecuteSqlRawAsync(
            "UPDATE SystemAdministrationConfigurations SET AuthorityVersion=2");
        if (invalid == "mapping") await fixture.Context.Database.ExecuteSqlRawAsync("UPDATE RoleGroupMappings SET Enabled=0");
        if (invalid == "expired") admin = admin with { AdministrationProof = admin.AdministrationProof! with { ValidUntil = DateTime.UtcNow.AddSeconds(-1) } };
        if (invalid == "missing") admin = admin with { AdministrationProof = null };
        fixture.Context.ChangeTracker.Clear();
        Func<Task>[] operations = [
            async () => { await fixture.Repository.ListAsync(admin, new(null, null, null, null, 25)); },
            async () => { await fixture.Repository.GetAsync(admin, TargetObjectId); },
            async () => { await fixture.Repository.UpdateUserAsync(admin, TargetObjectId, new(0, null, true)); },
            async () => { await fixture.Repository.PutOrganizationAccessAsync(admin, TargetObjectId, OrganizationId, ActiveRequest()); },
            async () => { await fixture.Repository.RevokeRoleAsync(admin, TargetObjectId, OrganizationId, "absent", 0); },
        ];
        foreach (var operation in operations)
            await operation.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Users.SingleAsync(x => x.Id == fixture.TargetUserId)).AuthorizationVersion.Should().Be(0);
        (await fixture.Context.ProcessingEvents.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("version")]
    [InlineData("generation")]
    public async Task ManualMutation_UsesCurrentDatabaseNotTrackedAuthority(string race)
    {
        var interceptor = new BeforeAutomaticTransaction();
        await using var fixture = await RepositoryFixture.CreateAsync(interceptor);
        await fixture.SeedAutomaticConfigurationAsync();
        // Deliberately leave the actor and configuration tracked from the earlier resolution.
        interceptor.Callback = async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = race switch
            {
                "disabled" => $"UPDATE Users SET IsActive=0 WHERE Id='{fixture.ActorUserId}'",
                "version" => $"UPDATE Users SET AuthorizationVersion=1 WHERE Id='{fixture.ActorUserId}'",
                _ => "UPDATE SystemAdministrationConfigurations SET AuthorityVersion=2",
            };
            await command.ExecuteNonQueryAsync();
        };
        Func<Task> mutation = () => fixture.Repository.PutOrganizationAccessAsync(AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());
        await mutation.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.OrganizationMemberships.CountAsync(x => x.UserId == fixture.TargetUserId)).Should().Be(0);
    }

    [Fact]
    public async Task OrganizationAdmin_CannotReactivateOrGrantGlobalAdminAtRepositoryBoundary()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        await fixture.SeedOrganizationAdminAsync();
        Func<Task> reactivate = () => fixture.Repository.UpdateUserAsync(OrganizationActor(), TargetObjectId, new(0, null, true));
        await reactivate.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
        Func<Task> globalGrant = () => fixture.Repository.PutOrganizationAccessAsync(AdminActor(), TargetObjectId, OrganizationId,
            ActiveRequest() with { RoleAssignments = [new("admin", null)] });
        await globalGrant.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "invalid_scope");
    }

    [Fact]
    public async Task ManualSuccessAuditFailure_RollsBackProfileRolesDefaultAndVersion()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        await fixture.Context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_manual_success BEFORE INSERT ON ProcessingEvents
            WHEN NEW.Action='auth.access.onboarded'
            BEGIN SELECT RAISE(ABORT,'synthetic manual audit failure'); END
            """);
        Func<Task> mutation = () => fixture.Repository.PutOrganizationAccessAsync(AdminActor(), TargetObjectId, OrganizationId, ActiveRequest());
        await mutation.Should().ThrowAsync<DbUpdateException>();
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Users.SingleAsync(x => x.Id == fixture.TargetUserId)).AuthorizationVersion.Should().Be(0);
        (await fixture.Context.OrganizationMemberships.CountAsync(x => x.UserId == fixture.TargetUserId)).Should().Be(0);
        (await fixture.Context.DepartmentMemberships.CountAsync(x => x.UserId == fixture.TargetUserId)).Should().Be(0);
        (await fixture.Context.RoleAssignments.CountAsync(x => x.UserId == fixture.TargetUserId)).Should().Be(0);
        (await fixture.Context.ProcessingEvents.CountAsync()).Should().Be(0);
        await fixture.Repository.RecordFailureAuditAsync(AdminActor(), TargetObjectId, "put_organization_access", "internal_error");
        (await fixture.Context.ProcessingEvents.SingleAsync()).CorrelationId.Should().Be(CorrelationId);
    }

    [Fact]
    public async Task ComputedAdmin_PaginationAndSelfDisableUseCurrentAuthority()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        var first = await fixture.Repository.ListAsync(AdminActor(), new(null, null, null, null, 1));
        first.Items.Should().HaveCount(1);
        first.NextCursor.Should().NotBeNull();
        var next = await fixture.Repository.ListAsync(AdminActor(), new(null, null, null, first.NextCursor, 1));
        next.Items.Should().HaveCount(1);
        next.Items.Single().ObjectId.Should().NotBe(first.Items.Single().ObjectId);
        var disabled = await fixture.Repository.UpdateUserAsync(AdminActor(), ActorObjectId, new(0, null, false));
        disabled.IsActive.Should().BeFalse();
        Func<Task> read = () => fixture.Repository.ListAsync(AdminActor(), new(null, null, null, null, 25));
        await read.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
    }

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

    [Fact]
    public void AutomaticContext_ExposesDomainTransactionContractAndInjectableClock()
    {
        var method = typeof(IEntraAccessManagementRepository).GetMethod("ConvergeSystemAdministratorContextAsync");
        method.Should().NotBeNull("T015 automatic context belongs on the Domain repository boundary");
        method!.GetParameters().Should().HaveCount(2);
        method.GetParameters()[1].ParameterType.Should().Be(typeof(CancellationToken));
        typeof(EntraAccessManagementRepository).GetConstructor(new[] { typeof(AppDbContext), typeof(TimeProvider) })
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("pending")]
    [InlineData("preexisting")]
    public async Task AutomaticContext_ConvergesEligibleIdentityWithoutPersonalAdminGrant(string state)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        if (state == "unknown")
        {
            fixture.Context.Remove(await fixture.Context.Users.SingleAsync(user => user.Id == fixture.TargetUserId));
            await fixture.Context.SaveChangesAsync();
        }
        else if (state == "preexisting")
        {
            await fixture.SeedUnrelatedTargetAccessAsync();
        }
        var oldAssignments = await fixture.Context.RoleAssignments.AsNoTracking().ToListAsync();
        var oldAuditCount = await fixture.Context.ProcessingEvents.CountAsync();

        await fixture.ConvergeAutomaticAsync(AutomaticRequest() with
        {
            ExpectedAuthorizationVersion = state == "unknown" ? null : 0,
        });

        fixture.Context.ChangeTracker.Clear();
        var user = await fixture.Context.Users.SingleAsync(item =>
            item.EntraTenantId == TenantId && item.EntraObjectId == TargetObjectId);
        user.AuthenticationProvider.Should().Be("entra");
        user.PasswordHash.Should().BeNull();
        user.PasswordResetRequired.Should().BeFalse();
        user.IsActive.Should().BeTrue();
        user.AuthorizationVersion.Should().Be(1);
        if (state == "unknown")
            user.Id.Should().Be(TargetObjectId, "first access keys new Entra profiles by immutable object ID");
        else
            user.Id.Should().Be(fixture.TargetUserId, "existing application identity keys remain immutable");
        await fixture.AssertAutomaticScopeAsync(user.Id, DepartmentAId);
        var userCount = await fixture.Context.Users.CountAsync();
        var assignmentIds = await fixture.Context.RoleAssignments.Select(item => item.Id).ToListAsync();
        assignmentIds.Should().BeEquivalentTo(oldAssignments.Select(item => item.Id),
            "computed group Admin never materializes a personal RoleAssignment");
        var audit = await fixture.Context.ProcessingEvents.SingleAsync(item =>
            item.EventType == "auth.admin.context.provisioned");
        audit.Actor.Should().Be(TargetObjectId);
        audit.CorrelationId.Should().Be(CorrelationId);
        audit.PayloadJson.Should().Contain(EntraAdministrationTestContract.GroupId)
            .And.NotContain("DirectMemberObjectIds").And.NotContain("access_token");

        await fixture.ConvergeAutomaticAsync(AutomaticRequest() with { ExpectedAuthorizationVersion = 0 });

        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Users.CountAsync()).Should().Be(userCount);
        (await fixture.Context.Users.SingleAsync(item => item.Id == user.Id)).AuthorizationVersion.Should().Be(1);
        (await fixture.Context.ProcessingEvents.CountAsync()).Should().Be(oldAuditCount + 1,
            "equivalent retries do not duplicate provisioning success");
        await fixture.AssertAutomaticScopeAsync(user.Id, DepartmentAId);
        if (state == "preexisting")
            (await fixture.Context.OrganizationMemberships.CountAsync(item => item.UserId == user.Id))
                .Should().Be(2);
    }

    [Fact]
    public async Task AutomaticContext_PreservesValidDefaultScopedGrantsWorkloadsAndAuditHistory()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        await fixture.Repository.PutOrganizationAccessAsync(AdminActor(), TargetObjectId, OrganizationId,
            ActiveRequest() with
            {
                Membership = new EntraAccessMembership("active", new[] { DepartmentAId, DepartmentBId }, DepartmentBId),
            });
        await fixture.SeedUnrelatedTargetAccessAsync();
        fixture.Context.Jobs.Add(new Job
        {
            Id = "72000000-0000-4000-8000-000000000001", Title = "Synthetic preserved workload",
            Department = "B", Organisation = "Primary", OrganizationId = OrganizationId, DepartmentId = DepartmentBId,
        });
        await fixture.Context.SaveChangesAsync();
        var before = await EntraAdministrationTestContract.SnapshotAsync(fixture.Context, ignoreLastLogin: true);

        await fixture.ConvergeAutomaticAsync(AutomaticRequest() with
        {
            Profile = new EntraAccessProfile("target@example.com", "Target User", "target@example.com"),
            ExpectedAuthorizationVersion = 1,
        });

        (await EntraAdministrationTestContract.SnapshotAsync(fixture.Context, ignoreLastLogin: true)).Should().Be(before,
            "a fully converged profile keeps valid defaults, workload data, scoped roles and immutable history");
        await fixture.AssertAutomaticScopeAsync(fixture.TargetUserId, DepartmentBId);
    }

    [Theory]
    [InlineData("disabled", "identity_disabled")]
    [InlineData("retired-organization", "invalid_scope")]
    [InlineData("retired-department", "invalid_scope")]
    [InlineData("foreign-default", "invalid_scope")]
    [InlineData("stale-user-version", "version_conflict")]
    [InlineData("stale-generation", "administration_transition_conflict")]
    [InlineData("wrong-group", "group_evidence_unavailable")]
    [InlineData("wrong-tenant", "group_evidence_unavailable")]
    [InlineData("wrong-actor", "group_evidence_unavailable")]
    [InlineData("incomplete", "group_evidence_unavailable")]
    [InlineData("invalid-composition", "group_evidence_unavailable")]
    [InlineData("not-direct", "group_evidence_unavailable")]
    [InlineData("not-eligible", "group_evidence_unavailable")]
    [InlineData("evidence-generation", "group_evidence_unavailable")]
    [InlineData("null-account-enabled", "group_evidence_unavailable")]
    [InlineData("null-user-type", "group_evidence_unavailable")]
    [InlineData("directory-disabled", "identity_disabled")]
    [InlineData("guest", "group_evidence_unavailable")]
    [InlineData("expired", "group_evidence_unavailable")]
    [InlineData("late-pagination", "group_evidence_unavailable")]
    [InlineData("future-evidence", "group_evidence_unavailable")]
    public async Task AutomaticContext_RejectsInvalidIdentityScopeVersionOrEvidenceWithoutMutation(string invalid, string code)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        var request = AutomaticRequest();
        if (invalid == "disabled")
            (await fixture.Context.Users.SingleAsync(user => user.Id == fixture.TargetUserId)).IsActive = false;
        if (invalid == "retired-organization")
            (await fixture.Context.Organizations.SingleAsync(item => item.Id == OrganizationId)).Status = "retired";
        if (invalid == "retired-department")
            (await fixture.Context.Departments.SingleAsync(item => item.Id == DepartmentAId)).Status = "retired";
        if (invalid == "foreign-default")
        {
            await fixture.SeedUnrelatedTargetAccessAsync();
            var defaultId = await fixture.Context.DepartmentMemberships
                .Where(item => item.UserId == fixture.TargetUserId).Select(item => item.Id).SingleAsync();
            await fixture.Context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            fixture.Context.OrganizationMemberships.Add(new OrganizationMembership
            {
                UserId = fixture.TargetUserId, OrganizationId = OrganizationId,
                DefaultDepartmentMembershipId = defaultId, UpdatedBy = ActorObjectId,
            });
            await fixture.Context.SaveChangesAsync();
            await fixture.Context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        if (invalid == "stale-user-version")
            request = request with { ExpectedAuthorizationVersion = 5 };
        if (invalid == "stale-generation")
            request = request with { ExpectedAuthorityVersion = 0 };
        request = request with
        {
            Evidence = invalid switch
            {
                "wrong-group" => request.Evidence with { GroupObjectId = OtherOrganizationId },
                "wrong-tenant" => request.Evidence with { TenantId = OtherOrganizationId },
                "wrong-actor" => request.Evidence with { ActorObjectId = ActorObjectId },
                "incomplete" => request.Evidence with { Complete = false },
                "invalid-composition" => request.Evidence with { CompositionValidated = false },
                "not-direct" => request.Evidence with { DirectMemberObjectIds = Array.Empty<string>() },
                "not-eligible" => request.Evidence with { EligibleUserObjectIds = Array.Empty<string>() },
                "evidence-generation" => request.Evidence with { AuthorityVersion = 0 },
                "null-account-enabled" => request.Evidence with { AccountEnabled = null },
                "null-user-type" => request.Evidence with { UserType = null },
                "directory-disabled" => request.Evidence with { AccountEnabled = false },
                "guest" => request.Evidence with { UserType = "Guest" },
                "expired" => request.Evidence with { ExpiresAt = EntraAdministrationTestContract.Now.AddSeconds(-1) },
                "late-pagination" => request.Evidence with
                {
                    CollectionStartedAt = EntraAdministrationTestContract.Now.AddMinutes(-6),
                    ExpiresAt = EntraAdministrationTestContract.Now.AddMinutes(4),
                },
                "future-evidence" => request.Evidence with
                {
                    CollectionStartedAt = EntraAdministrationTestContract.Now.AddMinutes(1),
                    CollectedAt = EntraAdministrationTestContract.Now.AddMinutes(2),
                },
                _ => request.Evidence,
            },
        };
        await fixture.Context.SaveChangesAsync();
        var before = await EntraAdministrationTestContract.SnapshotAsync(fixture.Context);

        var action = () => fixture.ConvergeAutomaticAsync(request);

        await action.Should().ThrowAsync<EntraAccessManagementException>().Where(error => error.Code == code);
        (await EntraAdministrationTestContract.SnapshotAsync(fixture.Context, excludeAdministrationDenials: true)).Should().Be(before);
        (await fixture.Context.ProcessingEvents.CountAsync(x => x.EventType == "auth.admin.group.denied")).Should().Be(1);
    }

    [Theory]
    [InlineData("user-disabled", "identity_disabled")]
    [InlineData("user-version", "version_conflict")]
    [InlineData("configuration-version", "administration_transition_conflict")]
    [InlineData("evidence-expired", "group_evidence_unavailable")]
    public async Task AutomaticContext_RechecksAtTransactionBoundaryAfterExternalEvidence(string race, string code)
    {
        var interceptor = new BeforeAutomaticTransaction();
        await using var fixture = await RepositoryFixture.CreateAsync(interceptor);
        await fixture.SeedAutomaticConfigurationAsync();
        interceptor.Callback = async connection =>
        {
            if (race == "evidence-expired")
            {
                fixture.AutomaticClock.Advance(TimeSpan.FromMinutes(6));
                return;
            }
            await using var command = connection.CreateCommand();
            command.CommandText = race switch
            {
                "user-disabled" => $"UPDATE Users SET IsActive = 0 WHERE Id = '{fixture.TargetUserId}'",
                "user-version" => $"UPDATE Users SET AuthorizationVersion = 7 WHERE Id = '{fixture.TargetUserId}'",
                _ => $"UPDATE \"{EntraAdministrationTestContract.RequireEntity(fixture.Context, "SystemAdministrationConfiguration").GetTableName()}\" SET AuthorityVersion = 2",
            };
            await command.ExecuteNonQueryAsync();
        };

        var action = () => fixture.ConvergeAutomaticAsync(AutomaticRequest());

        await action.Should().ThrowAsync<EntraAccessManagementException>().Where(error => error.Code == code);
        interceptor.CallCount.Should().Be(1, "evidence is collected outside the serializable write transaction");
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.OrganizationMemberships.CountAsync(item => item.UserId == fixture.TargetUserId)).Should().Be(0);
        (await fixture.Context.DepartmentMemberships.CountAsync(item => item.UserId == fixture.TargetUserId)).Should().Be(0);
        (await fixture.Context.ProcessingEvents.CountAsync(item => item.EventType == "auth.admin.context.provisioned")).Should().Be(0);
        var user = await fixture.Context.Users.SingleAsync(item => item.Id == fixture.TargetUserId);
        user.IsActive.Should().Be(race != "user-disabled");
        user.AuthorizationVersion.Should().Be(race == "user-version" ? 7 : 0);
    }

    [Fact]
    public async Task AutomaticContext_MissingConfiguredMembershipIsRepairedWithoutLosingExistingDefault()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        await fixture.Repository.PutOrganizationAccessAsync(AdminActor(), TargetObjectId, OrganizationId,
            ActiveRequest() with
            {
                Membership = new EntraAccessMembership("active", new[] { DepartmentBId }, DepartmentBId),
                RoleAssignments = Array.Empty<EntraDesiredRole>(),
            });

        await fixture.ConvergeAutomaticAsync(AutomaticRequest() with { ExpectedAuthorizationVersion = 1 });

        fixture.Context.ChangeTracker.Clear();
        await fixture.AssertAutomaticScopeAsync(fixture.TargetUserId, DepartmentBId);
        (await fixture.Context.Users.SingleAsync(item => item.Id == fixture.TargetUserId)).AuthorizationVersion.Should().Be(2);
        (await fixture.Context.RoleAssignments.CountAsync(item => item.UserId == fixture.TargetUserId)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticContext_AuditFailureRollsBackProfileMembershipsDefaultAndVersion(bool unknown)
    {
        var interceptor = new RejectAutomaticSuccessAudit();
        await using var fixture = await RepositoryFixture.CreateAsync(interceptor);
        await fixture.SeedAutomaticConfigurationAsync();
        if (unknown)
        {
            fixture.Context.Remove(await fixture.Context.Users.SingleAsync(user => user.Id == fixture.TargetUserId));
            await fixture.Context.SaveChangesAsync();
        }
        var before = await EntraAdministrationTestContract.SnapshotAsync(fixture.Context);
        interceptor.Armed = true;

        var action = () => fixture.ConvergeAutomaticAsync(AutomaticRequest() with
        {
            ExpectedAuthorizationVersion = unknown ? null : 0,
        });

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic automatic audit failure.");
        interceptor.SawAutomaticSuccess.Should().BeTrue();
        (await EntraAdministrationTestContract.SnapshotAsync(fixture.Context, excludeAdministrationDenials: true)).Should().Be(before);
        (await fixture.Context.ProcessingEvents.CountAsync(x => x.EventType == "auth.admin.group.denied")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticContext_ConcurrentFirstAccessUsesSeparateContextsAndConvergesOnce(bool unknown)
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        if (unknown)
        {
            fixture.Context.Remove(await fixture.Context.Users.SingleAsync(user => user.Id == fixture.TargetUserId));
            await fixture.Context.SaveChangesAsync();
        }
        var gate = new FirstAccessTransactionGate();
        await using var first = fixture.CreateConcurrentContext(gate);
        await using var second = fixture.CreateConcurrentContext(gate);

        var request = AutomaticRequest() with { ExpectedAuthorizationVersion = unknown ? null : 0 };
        await Task.WhenAll(fixture.ConvergeAutomaticAsync(request, first), fixture.ConvergeAutomaticAsync(request, second))
            .WaitAsync(TimeSpan.FromSeconds(20));

        gate.Arrivals.Should().BeGreaterThanOrEqualTo(2);
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Users.CountAsync(item => item.EntraObjectId == TargetObjectId)).Should().Be(1);
        var user = await fixture.Context.Users.SingleAsync(item => item.EntraObjectId == TargetObjectId);
        user.AuthorizationVersion.Should().Be(1);
        await fixture.AssertAutomaticScopeAsync(user.Id, DepartmentAId);
        (await fixture.Context.DepartmentMemberships.CountAsync(item => item.UserId == user.Id)).Should().Be(1);
        (await fixture.Context.ProcessingEvents.CountAsync(item => item.EventType == "auth.admin.context.provisioned")).Should().Be(1);
        (await fixture.Context.RoleAssignments.CountAsync(item => item.UserId == user.Id)).Should().Be(0);
    }

    private sealed record AutomaticContextInput(
        string TenantId, string ObjectId, EntraAccessProfile Profile, int? ExpectedAuthorizationVersion,
        int ExpectedAuthorityVersion, string CorrelationId, AutomaticEvidence Evidence);

    private sealed record AutomaticEvidence(
        string TenantId, string GroupObjectId, int AuthorityVersion, string ActorObjectId,
        DateTime CollectionStartedAt, DateTime CollectedAt, DateTime ExpiresAt,
        bool Complete, bool CompositionValidated, bool? AccountEnabled, string? UserType,
        string[] DirectMemberObjectIds, string[] EligibleUserObjectIds);

    private static AutomaticContextInput AutomaticRequest() => new(
        TenantId, TargetObjectId, new EntraAccessProfile("target@example.com", "Target User", "target@example.com"),
        0, 1, CorrelationId, new AutomaticEvidence(TenantId, EntraAdministrationTestContract.GroupId, 1,
            TargetObjectId, EntraAdministrationTestContract.Now.AddMinutes(-1),
            EntraAdministrationTestContract.Now.AddSeconds(-30), EntraAdministrationTestContract.Now.AddMinutes(4),
            true, true, true, "Member", new[] { TargetObjectId }, new[] { TargetObjectId }));

    private sealed class BeforeAutomaticTransaction : DbTransactionInterceptor
    {
        internal Func<DbConnection, Task>? Callback { get; set; }
        internal int CallCount { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            var callback = Callback;
            if (callback is not null)
            {
                Callback = null;
                CallCount++;
                await callback(connection);
            }
            return result;
        }
    }

    private sealed class RejectAutomaticSuccessAudit : SaveChangesInterceptor
    {
        internal bool Armed { get; set; }
        internal bool SawAutomaticSuccess { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<ProcessingEvent>().Any(entry =>
                entry.State == EntityState.Added && entry.Entity.EventType == "auth.admin.context.provisioned"))
            {
                SawAutomaticSuccess = true;
                throw new InvalidOperationException("Synthetic automatic audit failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FirstAccessTransactionGate : DbTransactionInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        internal int Arrivals => _arrivals;

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) >= 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task Committed_group_access_boundary_ignores_old_SQL_Admin_and_accepts_only_fresh_server_context()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.SeedAutomaticConfigurationAsync();
        var request = new EntraAccessSearch(null, null, null, null, 25);
        var legacy = AdminActor() with { GlobalAdmin = false, AdministrationProof = null };
        var denied = () => fixture.Repository.ListAsync(legacy, request);
        await denied.Should().ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
        foreach (var grant in await fixture.Context.RoleAssignments.Where(x => x.UserId == fixture.ActorUserId).ToListAsync())
        { grant.Status = "revoked"; grant.RevokedAt = DateTime.UtcNow; }
        await fixture.Context.SaveChangesAsync();
        (await fixture.Repository.ListAsync(AdminActor(), request)).Items.Should().NotBeEmpty();
        var stale = AdminActor() with { AdministrationProof = AdminActor().AdministrationProof! with { AuthorityVersion = 2 } };
        await ((Func<Task>)(() => fixture.Repository.ListAsync(stale, request))).Should()
            .ThrowAsync<EntraAccessManagementException>().Where(x => x.Code == "forbidden");
    }

    private static AccessManagementActor AdminActor() =>
        new(TenantId, ActorObjectId, true, Array.Empty<string>(), CorrelationId,
            new AdministrationActorProof("synthetic-environment", EntraAdministrationTestContract.TargetKey, 1, 0,
                "group", "system_group", DateTime.UtcNow.AddMinutes(3)));

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

        public EntraAdministrationTestContract.FixedClock AutomaticClock { get; } = new();

        public async Task SeedAutomaticConfigurationAsync() =>
            await EntraAdministrationTestContract.SeedAdministrationAsync(Context,
                organizationId: OrganizationId, departmentId: DepartmentAId);

        public async Task<object?> ConvergeAutomaticAsync(object request, AppDbContext? context = null)
        {
            var constructor = typeof(EntraAccessManagementRepository).GetConstructor(
                new[] { typeof(AppDbContext), typeof(TimeProvider) });
            constructor.Should().NotBeNull("T015 automatic context must recheck evidence against a deterministic clock");
            var repository = constructor!.Invoke(new object[] { context ?? Context, AutomaticClock });
            return await EntraAdministrationTestContract.InvokeAsync(
                repository, "ConvergeSystemAdministratorContextAsync", request);
        }

        public AppDbContext CreateConcurrentContext(IInterceptor interceptor) =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection.ConnectionString)
                .AddInterceptors(interceptor).Options);

        public async Task AssertAutomaticScopeAsync(string userId, string defaultDepartmentId)
        {
            var membership = await Context.OrganizationMemberships.SingleAsync(item =>
                item.UserId == userId && item.OrganizationId == OrganizationId && item.Status == "active");
            var departments = await Context.DepartmentMemberships.Where(item =>
                item.UserId == userId && item.OrganizationId == OrganizationId && item.Status == "active").ToListAsync();
            departments.Should().Contain(item => item.DepartmentId == DepartmentAId);
            departments.Single(item => item.Id == membership.DefaultDepartmentMembershipId)
                .DepartmentId.Should().Be(defaultDepartmentId);
        }

        public static async Task<RepositoryFixture> CreateAsync(IInterceptor? interceptor = null, string? databasePath = null)
        {
            var connection = new SqliteConnection(databasePath is null
                ? $"Data Source=entra-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"
                : $"Data Source={databasePath};Default Timeout=15;Pooling=False");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            var context = new AppDbContext(options.Options);
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
            var groupMapping = new RoleGroupMapping
            {
                TenantId = TenantId, GroupObjectId = "73000000-0000-4000-8000-000000000001",
                Role = "business_panel", OrganizationId = OrganizationId, DepartmentId = DepartmentBId,
                UpdatedBy = ActorObjectId,
            };
            Context.RoleGroupMappings.Add(groupMapping);
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
                    RoleGroupMappingId = groupMapping.Id,
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