using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Infrastructure.Persistence;
using TalentMatch.Infrastructure.Persistence.Repositories;
using Contract = TalentMatch.Infrastructure.Tests.EntraAuthorizationPersistenceTests.EntraAdministrationTestContract;

namespace TalentMatch.Infrastructure.Tests;

public sealed class AdministrationRepositoryTests
{
    private static AdministrationTransition Rollback(AdministrationTransition previous, long fence) => new()
    {
        TargetKey = previous.TargetKey, DesiredFingerprint = "explicit-individual-rollback",
        CandidateConfiguration = JsonSerializer.Serialize(new
        {
            EnvironmentKey = "synthetic-environment", TenantId = Contract.TenantId, SqlTargetKey = Contract.TargetKey,
            Mode = "individual", LegacyIndividualObjectId = Contract.MemberId,
            InitialOrganizationId = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(previous.CandidateConfiguration)!.InitialOrganizationId,
            InitialDepartmentId = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(previous.CandidateConfiguration)!.InitialDepartmentId,
            AuthorityVersion = 3,
            verificationRuntimes = new Dictionary<string, string> { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" },
            capabilityInventoryVersion = "synthetic-inventory",
        }),
        Phase = "preflight", CommittedVersion = 1, CandidateVersion = 3, FencingToken = fence,
        LeaseOwner = previous.LeaseOwner, LeaseExpiresAt = Contract.Now.AddMinutes(2), ActorId = Contract.MemberId,
        CorrelationId = Guid.NewGuid().ToString(), SupersedesTransitionId = previous.Id,
    };

    [Theory]
    [InlineData("automatic")]
    [InlineData("stale-fence")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-fingerprint")]
    [InlineData("wrong-version")]
    [InlineData("wrong-target")]
    [InlineData("non-monotonic")]
    public async Task Supersession_rejects_unselected_stale_or_cross_target_handoff_atomically(string invalid)
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "failed");
        var old = (AdministrationTransition)seed.Transition;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var lease = await repository.TryAcquireLeaseAsync(new(old.Id, old.LeaseOwner, 1, Contract.Now.AddMinutes(2)));
        var rollback = Rollback(old, lease!.FencingToken);
        var request = new AdministrationSupersession("explicit_rollback",
            new(old.Id, old.LeaseOwner, lease.FencingToken, 1), old.DesiredFingerprint, 2, 1);
        if (invalid == "automatic") request = request with { Kind = "automatic_fallback" };
        if (invalid == "stale-fence") request = request with { Lease = request.Lease with { FencingToken = 1 } };
        if (invalid == "wrong-owner") request = request with { Lease = request.Lease with { LeaseOwner = "other" } };
        if (invalid == "wrong-fingerprint") request = request with { ExpectedFingerprint = "other" };
        if (invalid == "wrong-version") request = request with { ExpectedCandidateVersion = 99 };
        if (invalid == "wrong-target") rollback.TargetKey = "other-target";
        if (invalid == "non-monotonic") request = request with { AbandonedFencingToken = lease.FencingToken };
        var snapshot = await Contract.SnapshotAsync(context);
        var stage = () => repository.StageAsync(rollback, supersession: request);
        await stage.Should().ThrowAsync<EntraAccessManagementException>();
        context.ChangeTracker.Clear();
        (await Contract.SnapshotAsync(context)).Should().Be(snapshot);
    }

    private sealed class RejectNewRollbackSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AdministrationTransition>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.SupersedesTransitionId != null))
                throw new InvalidOperationException("synthetic insertion/audit failure after slot release");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Supersession_slot_release_rolls_back_if_new_insert_or_audit_fails()
    {
        await using var context = await OpenAsync(new RejectNewRollbackSave());
        var seed = await Contract.SeedAdministrationAsync(context, "failed");
        var old = (AdministrationTransition)seed.Transition;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var lease = await repository.TryAcquireLeaseAsync(new(old.Id, old.LeaseOwner, 1, Contract.Now.AddMinutes(2)));
        var rollback = Rollback(old, lease!.FencingToken);
        var before = await Contract.SnapshotAsync(context);
        var stage = () => repository.StageAsync(rollback, supersession: new("explicit_rollback",
            new(old.Id, old.LeaseOwner, lease.FencingToken, 1), old.DesiredFingerprint, 2, 1));
        await stage.Should().ThrowAsync<InvalidOperationException>();
        context.ChangeTracker.Clear();
        (await Contract.SnapshotAsync(context)).Should().Be(before);
        (await repository.GetTransitionAsync(old.Id))!.SupersededByTransitionId.Should().BeNull();
        (await repository.GetTransitionAsync(rollback.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Explicit_rollback_atomically_supersedes_exact_failed_candidate_without_rewriting_history_or_authority()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "failed");
        var old = (AdministrationTransition)seed.Transition;
        var oldCandidate = old.CandidateConfiguration;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var lease = await repository.TryAcquireLeaseAsync(new(old.Id, old.LeaseOwner, 1, Contract.Now.AddMinutes(2)));
        lease.Should().NotBeNull();
        var desired = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(oldCandidate)!;
        desired.Mode = "individual"; desired.GroupObjectId = null; desired.SystemRoleGroupMappingId = null;
        desired.LegacyIndividualObjectId = Contract.MemberId; desired.AuthorityVersion = 3;
        var json = JsonSerializer.SerializeToNode(desired)!.AsObject();
        json["verificationRuntimes"] = JsonSerializer.SerializeToNode(new Dictionary<string, string>
            { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" });
        json["capabilityInventoryVersion"] = "synthetic-inventory";
        var rollback = new AdministrationTransition
        {
            TargetKey = old.TargetKey, DesiredFingerprint = "explicit-individual-rollback", CandidateConfiguration = json.ToJsonString(),
            Phase = "preflight", CommittedVersion = 1, CandidateVersion = 3, FencingToken = lease!.FencingToken,
            LeaseOwner = lease.LeaseOwner, LeaseExpiresAt = Contract.Now.AddMinutes(2), ActorId = Contract.MemberId,
            CorrelationId = Guid.NewGuid().ToString(), SupersedesTransitionId = old.Id,
        };
        var request = new AdministrationSupersession("explicit_rollback",
            new(old.Id, lease.LeaseOwner, lease.FencingToken, 1), old.DesiredFingerprint, 2, 1);
        var staged = await repository.StageAsync(rollback, supersession: request);
        staged.Phase.Should().Be("rollback_pending");
        staged.CandidateVersion.Should().Be(3);
        context.ChangeTracker.Clear();
        var previous = await repository.GetTransitionAsync(old.Id);
        previous!.Phase.Should().Be("failed");
        previous.CandidateConfiguration.Should().Be(oldCandidate);
        previous.SupersededByTransitionId.Should().Be(rollback.Id);
        (await repository.GetCommittedAsync(new("synthetic-environment", Contract.TenantId, Contract.TargetKey)))!
            .AuthorityVersion.Should().Be(1);
        (await repository.TryAcquireLeaseAsync(new(old.Id, old.LeaseOwner, lease.FencingToken, Contract.Now.AddMinutes(3))))
            .Should().BeNull();
    }

    [Fact]
    public async Task Postcommit_failure_can_resume_only_cleanup_not_restore_precommit_authority()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "failed");
        var transition = (AdministrationTransition)seed.Transition;
        var desired = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(transition.CandidateConfiguration)!;
        var committed = await context.SystemAdministrationConfigurations.SingleAsync();
        committed.AuthorityVersion = transition.CandidateVersion;
        committed.CommittedTransitionId = transition.Id;
        committed.GroupObjectId = desired.GroupObjectId;
        committed.SystemRoleGroupMappingId = desired.SystemRoleGroupMappingId;
        await context.SaveChangesAsync();
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var request = new AdministrationCheckpointRequest(transition.Id, 1, 1, transition.LeaseOwner,
            "failed", "preflight", "[]", Contract.CorrelationId);
        (await repository.TryCheckpointAsync(request)).Should().BeFalse();
        (await repository.TryCheckpointAsync(request with { Phase = "retiring" })).Should().BeTrue();
        (await context.SystemAdministrationConfigurations.SingleAsync()).AuthorityVersion.Should().Be(2);
    }

    [Theory]
    [InlineData("failed", "preflight")]
    [InlineData("rollback_pending", "prepared")]
    public async Task Explicit_precommit_failure_or_rollback_resumes_without_changing_committed_generation(string phase, string next)
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, phase);
        var transition = (AdministrationTransition)seed.Transition;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        (await repository.TryCheckpointAsync(new(transition.Id, 1, 1, transition.LeaseOwner, phase,
            next, "[]", Contract.CorrelationId))).Should().BeTrue();
        (await context.SystemAdministrationConfigurations.SingleAsync()).AuthorityVersion.Should().Be(1);
        (await context.AdministrationTransitions.SingleAsync(x => x.Id == transition.Id)).Phase.Should().Be(next);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("principalObjectId")]
    [InlineData("principalSid")]
    [InlineData("resource")]
    [InlineData("permission")]
    [InlineData("managementSource")]
    [InlineData("transitionId")]
    [InlineData("reviewReference")]
    public async Task Provenance_requires_an_immutable_exact_binding_and_reviewed_adoption(string field)
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context);
        var transition = (AdministrationTransition)seed.Transition;
        var before = new Dictionary<string, object?> {
            ["principalObjectId"] = Contract.MemberId, ["principalSid"] = "0x0011", ["resource"] = "synthetic-sql",
            ["permission"] = "db_owner", ["managementSource"] = "deployment", ["transitionId"] = transition.Id,
            ["reviewReference"] = "review", ["reviewedBy"] = Contract.MemberId, ["reviewedBeforeHash"] = "hash"
        };
        if (field != "valid") before[field] = null;
        var grant = new ManagedGrantProvenance {
            PrincipalObjectId = Contract.MemberId, PrincipalSid = "0x0011", Resource = "synthetic-sql",
            Permission = "db_owner", ManagementSource = "deployment", AdoptingTransitionId = transition.Id,
            BeforeState = JsonSerializer.Serialize(before), AfterState = JsonSerializer.Serialize(before)
        };
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var request = new AdministrationProvenanceRequest(new(transition.Id, transition.LeaseOwner, 1, 1), grant);
        (await repository.RecordProvenanceAsync(request)).Should().Be(field == "valid");
        if (field == "valid")
        {
            (await repository.RecordProvenanceAsync(request)).Should().BeFalse();
            (await context.ManagedGrantProvenance.SingleAsync()).PrincipalSid.Should().Be("0x0011");
        }
    }

    [Fact]
    public async Task App_receipts_do_not_substitute_for_a_self_SQL_receipt()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "replacement_verified");
        var first = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-a");
        var second = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-b");
        await context.SaveChangesAsync();
        var request = CommitRequest((AdministrationTransition)seed.Transition, first, second);
        (await new AdministrationRepository(context, new Contract.FixedClock()).TryCommitAsync(request)).Should().BeFalse();
    }
    [Fact]
    public async Task Stage_resumes_exact_candidate_and_lease_takeover_fences_previous_owner()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "complete");
        var candidate = (AdministrationTransition)seed.Transition;
        var desired = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(candidate.CandidateConfiguration)!;
        desired.EnvironmentKey = "synthetic-environment";
        candidate.CandidateConfiguration = JsonSerializer.Serialize(desired);
        candidate.Id = Guid.NewGuid().ToString();
        candidate.DesiredFingerprint = Guid.NewGuid().ToString("N");
        candidate.Phase = "preflight";
        var clock = new Contract.FixedClock();
        var repository = new AdministrationRepository(context, clock);

        var staged = await repository.StageAsync(candidate);
        (await repository.StageAsync(candidate)).Id.Should().Be(staged.Id);
        (await repository.TryAcquireLeaseAsync(new(staged.Id, "other-owner", 1, Contract.Now.AddMinutes(3)))).Should().BeNull();
        clock.Advance(TimeSpan.FromMinutes(3));
        var acquired = await repository.TryAcquireLeaseAsync(new(staged.Id, "other-owner", 1, Contract.Now.AddMinutes(5)));
        acquired.Should().NotBeNull();
        acquired!.FencingToken.Should().Be(2);
        (await repository.TryCheckpointAsync(new(staged.Id, 1, 1, "synthetic-owner", "preflight",
            "prepared", "[]", Contract.CorrelationId))).Should().BeFalse();
        (await repository.TryCheckpointAsync(new(staged.Id, 1, 2, "other-owner", "preflight",
            "prepared", "[]", Contract.CorrelationId))).Should().BeTrue();
    }

    [Fact]
    public async Task Checkpoint_requires_durable_intent_before_verified_external_effect()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context);
        var transition = (AdministrationTransition)seed.Transition;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var intent = """[{"operationId":"synthetic-operation","plane":"sql","intentRecorded":true,"completed":false}]""";
        var verified = """[{"operationId":"synthetic-operation","plane":"sql","intentRecorded":true,"completed":true,"readBackHash":"synthetic-readback"}]""";
        (await repository.TryCheckpointAsync(new(transition.Id, 1, 1, transition.LeaseOwner,
            "prepared", "prepared", intent, Contract.CorrelationId))).Should().BeTrue();
        (await repository.TryCheckpointAsync(new(transition.Id, 1, 1, transition.LeaseOwner,
            "prepared", "sql_switched", verified, Contract.CorrelationId))).Should().BeTrue();
        (await repository.TryCheckpointAsync(new(transition.Id, 1, 1, transition.LeaseOwner,
            "sql_switched", "replacement_verified", intent, Contract.CorrelationId))).Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-runtime")]
    [InlineData("wrong-runtime")]
    [InlineData("wrong-inventory")]
    [InlineData("duplicate-receipt")]
    [InlineData("missing-stack")]
    public async Task Commit_requires_exact_runtime_inventory_and_receipt_set(string invalid)
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "replacement_verified");
        var first = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-a");
        var second = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-b");
        var sqlReceipt = (VerificationReceipt)Contract.AddReceipt(context, seed, "sql");
        await context.SaveChangesAsync();
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var request = CommitRequest((AdministrationTransition)seed.Transition, first, second, sqlReceipt);
        request = invalid switch
        {
            "missing-runtime" => request with { RequiredRuntimes = null },
            "wrong-runtime" => request with { RequiredRuntimes = new Dictionary<string, string> { ["stack-a"] = "wrong", ["stack-b"] = "synthetic-runtime" } },
            "wrong-inventory" => request with { CapabilityInventoryVersion = "wrong" },
            "duplicate-receipt" => request with { ReceiptIds = new[] { first.Id, first.Id } },
            _ => request with { ReceiptIds = new[] { first.Id } },
        };
        var before = await Contract.SnapshotAsync(context);
        (await repository.TryCommitAsync(request)).Should().BeFalse();
        (await Contract.SnapshotAsync(context)).Should().Be(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_individual_outside_previous_group_requires_direct_role_and_candidate_bootstrap(bool directRole)
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "replacement_verified");
        var transition = (AdministrationTransition)seed.Transition;
        var candidate = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(transition.CandidateConfiguration)!;
        candidate.Mode = "individual";
        candidate.LegacyIndividualObjectId = Contract.MemberId;
        candidate.GroupObjectId = null;
        candidate.SystemRoleGroupMappingId = null;
        var metadata = JsonSerializer.SerializeToNode(candidate)!.AsObject();
        metadata["verificationRuntimes"] = JsonSerializer.SerializeToNode(new Dictionary<string, string>
            { ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime" });
        metadata["capabilityInventoryVersion"] = "synthetic-inventory";
        Contract.Set(context, transition, "CandidateConfiguration", metadata.ToJsonString());
        var assignment = new RoleAssignment
        {
            UserId = Contract.MemberId, TenantId = Contract.TenantId, UserObjectId = Contract.MemberId,
            Role = "admin", Source = "bootstrap", ManagementSource = "deployment",
            ManagedTransitionId = transition.Id, UpdatedBy = Contract.MemberId,
        };
        context.RoleAssignments.Add(assignment);
        var first = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-a");
        var second = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-b");
        foreach (var receipt in new[] { first, second })
        {
            receipt.Mode = "individual";
            receipt.PrincipalObjectId = receipt.SqlAdministratorObjectId = Contract.MemberId;
        }
        var sqlReceipt = (VerificationReceipt)Contract.AddReceipt(context, seed, "sql");
        sqlReceipt.Mode = "individual";
        sqlReceipt.PrincipalObjectId = sqlReceipt.SqlAdministratorObjectId = Contract.MemberId;
        await context.SaveChangesAsync();
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var request = CommitRequest(transition, first, second) with
        {
            ReceiptIds = new[] { first.Id, second.Id, sqlReceipt.Id },
            IndividualProof = new(Contract.TenantId, Contract.MemberId, true, "Member",
                Contract.Now.AddMinutes(-1), Contract.Now.AddSeconds(-30), Contract.Now.AddMinutes(3),
                directRole, true, assignment.Id),
        };
        (await repository.TryCommitAsync(request)).Should().Be(directRole);
        var current = await repository.GetCommittedAsync(new("synthetic-environment", Contract.TenantId, Contract.TargetKey));
        current!.Mode.Should().Be(directRole ? "individual" : "group");
    }

    [Fact]
    public async Task Record_receipt_and_provenance_append_correlated_history_without_rosters()
    {
        await using var context = await OpenAsync();
        var seed = await Contract.SeedAdministrationAsync(context, "sql_switched");
        var transition = (AdministrationTransition)seed.Transition;
        var receipt = (VerificationReceipt)Contract.AddReceipt(context, seed, "stack-b");
        context.Entry(receipt).State = EntityState.Detached;
        var repository = new AdministrationRepository(context, new Contract.FixedClock());
        var lease = new AdministrationLease(transition.Id, transition.LeaseOwner, 1, 1);
        (await repository.RecordReceiptAsync(new(lease, receipt))).Should().BeTrue();
        (await repository.RecordProvenanceAsync(new(lease, new ManagedGrantProvenance
        {
            PrincipalObjectId = Contract.MemberId, PrincipalSid = "synthetic-sid",
            Resource = "synthetic-resource", Permission = "bootstrap", ManagementSource = "independent",
            OriginatingTransitionId = transition.Id,
        }))).Should().BeTrue();
        (await context.ManagedGrantProvenance.CountAsync()).Should().Be(1);
        (await context.VerificationReceipts.CountAsync()).Should().Be(1);
        var audit = await context.ProcessingEvents.SingleAsync(x => x.Id == receipt.AuditEventId);
        audit.CorrelationId.Should().Be(Contract.CorrelationId);
        audit.PayloadJson.Should().NotContain("MemberObjectIds");
    }

    [Fact]
    public void Migration_scripts_are_provider_specific_and_additive()
    {
        using var sqlite = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var sqlServer = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SyntheticScriptOnly;Trusted_Connection=True").Options);
        foreach (var context in new[] { sqlite, sqlServer })
        {
            var script = context.GetService<IMigrator>().GenerateScript(
                "20261019173434_AddUiPollingIntervals", "20261030180000_AddSystemAdministration");
            script.Should().Contain("SystemAdministrationConfigurations").And.Contain("VerificationReceipts")
                .And.Contain("ManagedGrantProvenance").And.NotContain("DROP TABLE [talentmatch].[Jobs]");
            if (context.Database.IsSqlServer())
                script.Should().Contain("[talentmatch].[AdministrationTransitions]").And.Contain("DATEADD")
                    .And.Contain("nvarchar(36)").And.NotContain("julianday").And.NotContain(" TEXT");
            else
                script.Should().Contain("__AdministrationUpgradeGuard").And.Contain("julianday");
        }
        sqlite.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static AdministrationCommitRequest CommitRequest(
        AdministrationTransition transition, VerificationReceipt first, VerificationReceipt second, VerificationReceipt? sqlReceipt = null) =>
        new(transition.Id, 1, 1, transition.LeaseOwner, new[] { "stack-a", "stack-b" },
            sqlReceipt is null ? new[] { first.Id, second.Id } : new[] { first.Id, second.Id, sqlReceipt.Id },
            Contract.CorrelationId, new Dictionary<string, string>
            {
                ["stack-a"] = "synthetic-runtime", ["stack-b"] = "synthetic-runtime",
            }, "synthetic-inventory", GroupProof: Contract.GroupEvidence());

    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate-delegated")]
    [InlineData("unmapped-group")]
    public async Task Upgrade_preserves_authorization_history_or_refuses_invalid_legacy_assignments(string state)
    {
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await context.Database.OpenConnectionAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261019173434_AddUiPollingIntervals");
        var user = new User
        {
            Id = Contract.MemberId, Username = "synthetic@example.com", AuthenticationProvider = "entra",
            EntraTenantId = Contract.TenantId, EntraObjectId = Contract.MemberId, PasswordHash = null,
        };
        context.Users.Add(user);
        context.Organizations.Add(new Organization { Id = "synthetic-organization", Name = "Synthetic", UpdatedBy = user.Id });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO ProcessingEvents (Id, Actor, EventType, EntityType, EntityId, PayloadJson, Timestamp, CorrelationId)
            VALUES ('synthetic-history', {0}, 'synthetic.preserved', 'User', {0}, '{{}}', '2026-10-30', {1})
            """, user.Id, Contract.CorrelationId);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO RoleAssignments (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, Source,
                Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy)
            VALUES ('synthetic-assignment', {0}, {1}, {0}, 'business_panel', 'synthetic-organization', {2},
                'active', '2026-10-30', '2026-10-30', '2026-10-30', {0})
            """, Contract.MemberId, Contract.TenantId, state == "unmapped-group" ? "group" : "delegated");
        if (state == "duplicate-delegated")
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO RoleAssignments (Id, UserId, TenantId, UserObjectId, Role, OrganizationId, Source,
                    Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy)
                SELECT 'synthetic-duplicate', UserId, TenantId, UserObjectId, Role, OrganizationId, Source,
                    Status, EffectiveAt, CreatedAt, UpdatedAt, UpdatedBy FROM RoleAssignments
                """);
        if (state == "valid")
        {
            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            (await context.Users.SingleAsync()).Id.Should().Be(Contract.MemberId);
            (await context.ProcessingEvents.SingleAsync()).Id.Should().Be("synthetic-history");
            var assignment = await context.RoleAssignments.SingleAsync();
            assignment.Id.Should().Be("synthetic-assignment");
            assignment.ManagementSource.Should().Be("independent");
            (await context.SystemAdministrationConfigurations.CountAsync()).Should().Be(0);
        }
        else
        {
            var migrate = () => migrator.MigrateAsync();
            await migrate.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
            (await context.Database.GetAppliedMigrationsAsync()).Should().NotContain("20261030180000_AddSystemAdministration");
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM RoleAssignments";
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(state == "duplicate-delegated" ? 2 : 1);
        }
    }

    private static async Task<AppDbContext> OpenAsync(params IInterceptor[] interceptors)
    {
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:")
            .AddInterceptors(interceptors).Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        return context;
    }
}
