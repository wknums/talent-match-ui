using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class AdministrationRepository(AppDbContext context, TimeProvider clock) : IAdministrationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<SystemAdministrationConfiguration?> GetCommittedAsync(
        AdministrationTarget target, CancellationToken cancellationToken = default) =>
        context.SystemAdministrationConfigurations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EnvironmentKey == target.EnvironmentKey && x.TenantId == target.TenantId
            && x.SqlTargetKey == target.SqlTargetKey, cancellationToken);

    public Task<AdministrationTransition?> GetTransitionAsync(string id, CancellationToken cancellationToken = default) =>
        context.AdministrationTransitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<AdministrationTransition> StageAsync(AdministrationTransition candidate, CancellationToken cancellationToken = default,
        AdministrationSupersession? supersession = null) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var desired = Parse(candidate.CandidateConfiguration);
            if (desired is null || candidate.Phase != "preflight" || candidate.LeaseExpiresAt <= Now
                || candidate.FencingToken < 1
                || candidate.SupersededByTransitionId is not null || candidate.SupersededAt is not null
                || string.IsNullOrWhiteSpace(candidate.LeaseOwner) || string.IsNullOrWhiteSpace(candidate.DesiredFingerprint)
                || (supersession is null && candidate.SupersedesTransitionId is null && candidate.CandidateVersion != candidate.CommittedVersion + 1)
                || desired.AuthorityVersion != candidate.CandidateVersion || !ValidMode(desired))
                throw Conflict();
            var equivalent = await context.AdministrationTransitions.SingleOrDefaultAsync(x =>
                x.TargetKey == candidate.TargetKey && x.DesiredFingerprint == candidate.DesiredFingerprint
                && x.CandidateVersion == candidate.CandidateVersion, token);
            if (equivalent is not null)
            {
                if (equivalent.SupersededByTransitionId is not null
                    || equivalent.CandidateConfiguration != candidate.CandidateConfiguration
                    || supersession is not null && (equivalent.Id != candidate.Id
                        || equivalent.SupersedesTransitionId != supersession.Lease.TransitionId
                        || equivalent.FencingToken != candidate.FencingToken || equivalent.LeaseOwner != candidate.LeaseOwner
                        || equivalent.LeaseExpiresAt <= Now))
                    throw Conflict();
                return equivalent;
            }
            AdministrationTransition? superseded = null;
            if (supersession is not null)
            {
                superseded = await ReadLeasedAsync(supersession.Lease, token);
                var previous = superseded is null ? null : Parse(superseded.CandidateConfiguration);
                if (superseded is null || previous is null || supersession.Kind != "explicit_rollback"
                    || superseded.Phase != "failed" || superseded.TargetKey != candidate.TargetKey
                    || superseded.Id == candidate.Id || candidate.SupersedesTransitionId != superseded.Id
                    || candidate.LeaseOwner != supersession.Lease.LeaseOwner || candidate.FencingToken != supersession.Lease.FencingToken
                    || supersession.AbandonedFencingToken < 1 || candidate.FencingToken <= supersession.AbandonedFencingToken
                    || superseded.DesiredFingerprint != supersession.ExpectedFingerprint
                    || superseded.CandidateVersion != supersession.ExpectedCandidateVersion
                    || candidate.CandidateVersion != Math.Max(candidate.CommittedVersion + 1, superseded.CandidateVersion + 1)
                    || previous.EnvironmentKey != desired.EnvironmentKey || previous.TenantId != desired.TenantId
                    || previous.SqlTargetKey != desired.SqlTargetKey
                    || previous.Mode == desired.Mode && previous.GroupObjectId == desired.GroupObjectId
                        && previous.LegacyIndividualObjectId == desired.LegacyIndividualObjectId)
                    throw Conflict();
            }
            else if (candidate.SupersedesTransitionId is not null) throw Conflict();
            var live = await context.AdministrationTransitions.SingleOrDefaultAsync(x =>
                x.TargetKey == candidate.TargetKey && x.Phase != "complete" && x.Phase != "rolled_back"
                    && x.SupersededByTransitionId == null, token);
            if (live is not null)
            {
                if (superseded?.Id == live.Id) { /* Exact slot released below, inside this transaction. */ }
                else if (supersession is null && live.DesiredFingerprint == candidate.DesiredFingerprint
                    && live.CandidateVersion == candidate.CandidateVersion
                    && live.CandidateConfiguration == candidate.CandidateConfiguration)
                    return live;
                else throw Conflict();
            }
            var committed = await context.SystemAdministrationConfigurations.SingleOrDefaultAsync(x =>
                x.EnvironmentKey == desired.EnvironmentKey && x.TenantId == desired.TenantId
                && x.SqlTargetKey == desired.SqlTargetKey, token);
            if ((committed?.AuthorityVersion ?? 0) != candidate.CommittedVersion
                || !await ValidScopeAsync(desired, token))
                throw Conflict();
            if (superseded is not null)
            {
                superseded.SupersededByTransitionId = candidate.Id;
                superseded.SupersededAt = superseded.UpdatedAt = Now;
                superseded.LeaseExpiresAt = Now;
                Audit(superseded, "auth.admin.transition.superseded", "partial", actor: candidate.ActorId,
                    correlation: candidate.CorrelationId);
                // EF must release the filtered unique slot before INSERT, but
                // both saves and their audits are in one serializable transaction.
                await context.SaveChangesAsync(token);
                candidate.Phase = "rollback_pending";
            }
            candidate.PriorConfiguration = committed is null ? null : JsonSerializer.Serialize(committed);
            candidate.FencingToken = superseded is null ? candidate.FencingToken : supersession!.Lease.FencingToken;
            candidate.ExternalOperations = "[]";
            candidate.StartedAt = candidate.UpdatedAt = Now;
            context.AdministrationTransitions.Add(candidate);
            Audit(candidate, "auth.admin.transition.checkpoint", "partial");
            await context.SaveChangesAsync(token);
            return candidate;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<AdministrationTransition?> TryAcquireLeaseAsync(
        AdministrationLeaseRequest request, CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var transition = await context.AdministrationTransitions.SingleOrDefaultAsync(x => x.Id == request.TransitionId, token);
            if (transition is null || transition.FencingToken != request.ExpectedFencingToken
                || transition.SupersededByTransitionId is not null
                || transition.Phase is "complete" or "rolled_back" || request.LeaseExpiresAt <= Now
                || string.IsNullOrWhiteSpace(request.LeaseOwner)
                || (transition.LeaseExpiresAt > Now && transition.LeaseOwner != request.LeaseOwner))
                return null;
            transition.FencingToken++;
            transition.LeaseOwner = request.LeaseOwner;
            transition.LeaseExpiresAt = request.LeaseExpiresAt;
            transition.UpdatedAt = Now;
            Audit(transition, "auth.admin.transition.checkpoint", "partial");
            await context.SaveChangesAsync(token);
            return transition;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<bool> TryCheckpointAsync(AdministrationCheckpointRequest request, CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var transition = await ReadLeasedAsync(new(request.TransitionId, request.LeaseOwner,
                request.FencingToken, request.ExpectedAuthorityVersion), token);
            var failureResume = false;
            if (transition?.Phase == "failed")
            {
                var candidate = Parse(transition.CandidateConfiguration);
                var committed = candidate is null ? null : await GetCommittedAsync(
                    new(candidate.EnvironmentKey, candidate.TenantId, candidate.SqlTargetKey), token);
                failureResume = request.Phase == "preflight"
                    ? (committed?.AuthorityVersion ?? 0) == transition.CommittedVersion
                    : request.Phase == "retiring" && committed?.AuthorityVersion == transition.CandidateVersion
                        && committed.CommittedTransitionId == transition.Id;
            }
            if (transition is null || transition.Phase != request.ExpectedPhase
                || string.IsNullOrWhiteSpace(request.CorrelationId)
                || !ValidCheckpoint(transition, request, failureResume))
                return false;
            transition.Phase = request.Phase;
            transition.ExternalOperations = request.ExternalOperations;
            transition.UpdatedAt = Now;
            transition.CorrelationId = request.CorrelationId;
            Audit(transition, "auth.admin.transition.checkpoint", request.Phase == "complete" ? "succeeded" : "partial");
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<bool> RecordReceiptAsync(AdministrationReceiptRequest request, CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var transition = await ReadLeasedAsync(request.Lease, token);
            var candidate = transition is null ? null : Parse(transition.CandidateConfiguration);
            var receipt = request.Receipt;
            if (transition is null || candidate is null || transition.Phase is not ("sql_switched" or "replacement_verified")
                || !ValidReceipt(receipt, candidate, transition)
                || !await EnabledUserAsync(receipt.TenantId, receipt.MemberObjectId, token))
                return false;
            if (request.ExpectedAuthorizationVersion is not null
                && !await context.Users.AsNoTracking().AnyAsync(x => x.EntraTenantId == receipt.TenantId
                    && x.EntraObjectId == receipt.MemberObjectId && x.IsActive
                    && x.AuthorizationVersion == request.ExpectedAuthorizationVersion, token))
                return false;
            var committed = await GetCommittedAsync(new(candidate.EnvironmentKey, candidate.TenantId, candidate.SqlTargetKey), token);
            if ((committed?.AuthorityVersion ?? 0) != transition.CommittedVersion) return false;
            if (request.ExpectedAuthorizationVersion is not null && candidate.Mode == "individual"
                && !await context.RoleAssignments.AsNoTracking().AnyAsync(x =>
                    x.TenantId == receipt.TenantId && x.UserObjectId == receipt.MemberObjectId
                    && x.Source == "bootstrap" && x.Role == "admin" && x.Status == "active"
                    && x.OrganizationId == null && x.DepartmentId == null && x.RoleGroupMappingId == null
                    && x.ManagedTransitionId == transition.Id, token))
                return false;
            if (await context.VerificationReceipts.AnyAsync(x => x.Id == receipt.Id, token))
                return false;
            var audit = Audit(transition, "auth.admin.transition.checkpoint", "partial", receipt.MemberObjectId,
                actor: receipt.MemberObjectId, correlation: receipt.CorrelationId);
            receipt.AuditEventId = audit.Id;
            context.VerificationReceipts.Add(receipt);
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<bool> RecordProvenanceAsync(AdministrationProvenanceRequest request, CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var transition = await ReadLeasedAsync(request.Lease, token);
            var grant = request.Grant;
            if (transition is null || !new[] { grant.OriginatingTransitionId, grant.AdoptingTransitionId, grant.RetiringTransitionId }.Contains(transition.Id)
                || (grant.ManagementSource == "independent" && grant.RetiringTransitionId is not null)
                || grant.ManagementSource is not ("deployment" or "application" or "independent"))
                return false;
            if (new[] { grant.Id, grant.PrincipalObjectId, grant.Resource, grant.Permission }.Any(string.IsNullOrWhiteSpace)
                || await context.ManagedGrantProvenance.AnyAsync(x => x.Id == grant.Id, token))
                return false;
            try
            {
                using var before = JsonDocument.Parse(grant.BeforeState);
                using var after = JsonDocument.Parse(grant.AfterState);
                if (before.RootElement.ValueKind != JsonValueKind.Object || after.RootElement.ValueKind != JsonValueKind.Object)
                    return false;
                if (grant.ManagementSource != "independent" || grant.RetiringTransitionId is not null)
                {
                    string? Value(string key) => before.RootElement.TryGetProperty(key, out var property)
                        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
                    if (Value("principalObjectId") != grant.PrincipalObjectId || Value("principalSid") != grant.PrincipalSid
                        || Value("resource") != grant.Resource || Value("permission") != grant.Permission
                        || Value("managementSource") != grant.ManagementSource || Value("transitionId") != transition.Id
                        || grant.AdoptingTransitionId is not null && (string.IsNullOrWhiteSpace(Value("reviewReference"))
                            || string.IsNullOrWhiteSpace(Value("reviewedBy")) || string.IsNullOrWhiteSpace(Value("reviewedBeforeHash"))))
                        return false;
                    string? AfterValue(string key) => after.RootElement.TryGetProperty(key, out var property)
                        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
                    if (AfterValue("principalObjectId") != grant.PrincipalObjectId || AfterValue("principalSid") != grant.PrincipalSid
                        || AfterValue("resource") != grant.Resource || AfterValue("permission") != grant.Permission
                        || AfterValue("managementSource") != grant.ManagementSource || AfterValue("transitionId") != transition.Id)
                        return false;
                }
            }
            catch (JsonException) { return false; }
            context.ManagedGrantProvenance.Add(grant);
            Audit(transition, "auth.admin.transition.checkpoint", "partial", grant.PrincipalObjectId);
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    public Task<bool> TryCommitAsync(AdministrationCommitRequest request, CancellationToken cancellationToken = default) =>
        context.ExecuteInTransactionAsync(async token =>
        {
            var transition = await ReadLeasedAsync(new(request.TransitionId, request.LeaseOwner,
                request.FencingToken, request.ExpectedAuthorityVersion), token);
            var candidate = transition is null ? null : Parse(transition.CandidateConfiguration);
            if (transition is null || candidate is null || transition.Phase != "replacement_verified"
                || string.IsNullOrWhiteSpace(request.CorrelationId)
                || !ValidMode(candidate) || candidate.AuthorityVersion != transition.CandidateVersion
                || !await ValidScopeAsync(candidate, token)
                || request.IncludedStacks is null || request.ReceiptIds is null
                || request.IncludedStacks.Count == 0
                || request.RequiredRuntimes is null || string.IsNullOrWhiteSpace(request.CapabilityInventoryVersion)
                || request.IncludedStacks.Distinct().Count() != request.IncludedStacks.Count
                || request.IncludedStacks.Any(x => x is not ("stack-a" or "stack-b"))
                || request.ReceiptIds.Distinct().Count() != request.ReceiptIds.Count)
                return false;
            using (var metadata = JsonDocument.Parse(transition.CandidateConfiguration))
            {
                if (!metadata.RootElement.TryGetProperty("verificationRuntimes", out var runtimes)
                    || (runtimes.ValueKind != JsonValueKind.Object
                        || runtimes.EnumerateObject().Count() != request.RequiredRuntimes.Count
                        || request.RequiredRuntimes.Any(pair =>
                            !runtimes.TryGetProperty(pair.Key, out var version)
                            || version.ValueKind != JsonValueKind.String || version.GetString() != pair.Value)
                        || !metadata.RootElement.TryGetProperty("capabilityInventoryVersion", out var inventory)
                        || inventory.GetString() != request.CapabilityInventoryVersion))
                    return false;
            }
            var configuration = await context.SystemAdministrationConfigurations.SingleOrDefaultAsync(x =>
                x.SqlTargetKey == candidate.SqlTargetKey && x.TenantId == candidate.TenantId, token);
            if ((configuration?.AuthorityVersion ?? 0) != request.ExpectedAuthorityVersion)
                return false;
            var receipts = await context.VerificationReceipts.AsNoTracking()
                .Where(x => request.ReceiptIds.Contains(x.Id)).ToListAsync(token);
            if (receipts.Count != request.ReceiptIds.Count || receipts.Count == 0
                || receipts.Any(x => !ValidReceipt(x, candidate, transition))
                || receipts.Select(x => x.MemberObjectId).Distinct().Count() != 1
                || receipts.Select(x => x.CapabilityInventoryVersion).Distinct().Count() != 1
                || receipts.Count(x => x.Stack == "sql") != 1
                || request.IncludedStacks.Any(stack => receipts.Count(x => x.Stack == stack) != 1)
                || receipts.Any(x => !request.IncludedStacks.Contains(x.Stack) && x.Stack != "sql")
                || (request.CapabilityInventoryVersion is not null
                    && receipts.Any(x => x.CapabilityInventoryVersion != request.CapabilityInventoryVersion))
                || (request.RequiredRuntimes.Count != request.IncludedStacks.Count
                        || request.IncludedStacks.Any(stack => !request.RequiredRuntimes.TryGetValue(stack, out var runtime)
                            || string.IsNullOrWhiteSpace(runtime) || receipts.Single(x => x.Stack == stack).RuntimeVersion != runtime))
                || !await EnabledUserAsync(candidate.TenantId, receipts[0].MemberObjectId, token))
                return false;
            foreach (var receipt in receipts)
                if (!await context.ProcessingEvents.AnyAsync(x => x.Id == receipt.AuditEventId
                    && x.EntityId == transition.Id && x.CorrelationId == receipt.CorrelationId, token))
                    return false;
            if (candidate.Mode == "individual"
                && !await ValidIndividualAsync(request.IndividualProof, candidate, transition, receipts[0].MemberObjectId, token))
                return false;
            if (candidate.Mode == "group" && !ValidGroupProof(request.GroupProof, candidate, receipts[0].MemberObjectId))
                return false;
            if (configuration is null)
            {
                if (string.IsNullOrWhiteSpace(candidate.EnvironmentKey))
                    return false;
                configuration = new SystemAdministrationConfiguration { EnvironmentKey = candidate.EnvironmentKey, TenantId = candidate.TenantId, SqlTargetKey = candidate.SqlTargetKey };
                context.SystemAdministrationConfigurations.Add(configuration);
            }
            configuration.Mode = candidate.Mode;
            configuration.GroupObjectId = candidate.GroupObjectId;
            configuration.LegacyIndividualObjectId = candidate.LegacyIndividualObjectId;
            configuration.SystemRoleGroupMappingId = candidate.SystemRoleGroupMappingId;
            configuration.InitialOrganizationId = candidate.InitialOrganizationId;
            configuration.InitialDepartmentId = candidate.InitialDepartmentId;
            configuration.AuthorityVersion = transition.CandidateVersion;
            configuration.CommittedTransitionId = transition.Id;
            configuration.CommittedAt = Now;
            configuration.CommittedBy = transition.ActorId;
            transition.Phase = "authority_committed";
            transition.UpdatedAt = Now;
            transition.CorrelationId = request.CorrelationId;
            Audit(transition, "auth.admin.configuration.changed", "succeeded", receipts[0].MemberObjectId);
            await context.SaveChangesAsync(token);
            return true;
        }, cancellationToken, IsolationLevel.Serializable);

    private async Task<AdministrationTransition?> ReadLeasedAsync(AdministrationLease lease, CancellationToken token)
    {
        var transition = await context.AdministrationTransitions.SingleOrDefaultAsync(x => x.Id == lease.TransitionId, token);
        if (transition is null || transition.SupersededByTransitionId is not null
            || transition.FencingToken != lease.FencingToken || transition.LeaseOwner != lease.LeaseOwner
            || transition.LeaseExpiresAt <= Now || transition.CommittedVersion != lease.ExpectedAuthorityVersion
            || transition.Phase is "complete" or "rolled_back")
            return null;
        var candidate = Parse(transition.CandidateConfiguration);
        if (candidate is null)
            return null;
        var committed = await context.SystemAdministrationConfigurations.AsNoTracking()
            .Where(x => x.TenantId == candidate.TenantId && x.SqlTargetKey == candidate.SqlTargetKey)
            .SingleOrDefaultAsync(token);
        var expected = transition.Phase is "authority_committed" or "retiring"
            || transition.Phase == "failed" && committed?.AuthorityVersion == transition.CandidateVersion
                && committed.CommittedTransitionId == transition.Id ? transition.CandidateVersion : lease.ExpectedAuthorityVersion;
        return (committed?.AuthorityVersion ?? 0) == expected ? transition : null;
    }

    private async Task<bool> ValidScopeAsync(SystemAdministrationConfiguration candidate, CancellationToken token) =>
        await context.Organizations.AnyAsync(x => x.Id == candidate.InitialOrganizationId && x.Status == "active", token)
        && await context.Departments.AnyAsync(x => x.Id == candidate.InitialDepartmentId
            && x.OrganizationId == candidate.InitialOrganizationId && x.Status == "active", token)
        && (candidate.Mode != "group" || await context.RoleGroupMappings.AnyAsync(x =>
            x.Id == candidate.SystemRoleGroupMappingId && x.TenantId == candidate.TenantId
            && x.GroupObjectId == candidate.GroupObjectId && x.Purpose == "system_admin" && x.Enabled
            && x.Role == "admin" && x.OrganizationId == null && x.DepartmentId == null, token));

    private Task<bool> EnabledUserAsync(string tenant, string member, CancellationToken token) =>
        context.Users.AnyAsync(x => x.AuthenticationProvider == "entra" && x.EntraTenantId == tenant
            && x.EntraObjectId == member && x.IsActive, token);

    private async Task<bool> ValidIndividualAsync(IndividualAdministrationProof? proof,
        SystemAdministrationConfiguration candidate, AdministrationTransition transition, string member, CancellationToken token)
    {
        if (proof is null || member != candidate.LegacyIndividualObjectId || proof.ObjectId != member
            || proof.TenantId != candidate.TenantId || proof.AccountEnabled != true || proof.UserType != "Member"
            || !proof.DirectAdminRoleBinding || !proof.AdminClaim || proof.CollectionStartedAt > proof.CollectedAt
            || proof.CollectedAt > Now || proof.ExpiresAt <= Now || proof.ExpiresAt > proof.CollectionStartedAt.AddMinutes(5))
            return false;
        if (!await context.RoleAssignments.AnyAsync(x => x.Id == proof.BootstrapAssignmentId
            && x.TenantId == candidate.TenantId && x.UserObjectId == member && x.Role == "admin"
            && x.Source == "bootstrap" && x.Status == "active" && x.ManagedTransitionId == transition.Id
            && x.OrganizationId == null && x.DepartmentId == null && x.RoleGroupMappingId == null, token))
            return false;
        var user = await context.Users.SingleAsync(x => x.AuthenticationProvider == "entra"
            && x.EntraTenantId == candidate.TenantId && x.EntraObjectId == member, token);
        return await context.OrganizationMemberships.AnyAsync(x => x.UserId == user.Id
            && x.OrganizationId == candidate.InitialOrganizationId && x.Status == "active"
            && x.DefaultDepartmentMembership != null && x.DefaultDepartmentMembership.Status == "active"
            && x.DefaultDepartmentMembership.UserId == user.Id
            && x.DefaultDepartmentMembership.OrganizationId == candidate.InitialOrganizationId
            && x.DefaultDepartmentMembership.Department.Status == "active", token)
            && await context.DepartmentMemberships.AnyAsync(x => x.UserId == user.Id
                && x.OrganizationId == candidate.InitialOrganizationId
                && x.DepartmentId == candidate.InitialDepartmentId && x.Status == "active", token);
    }

    private bool ValidReceipt(VerificationReceipt receipt, SystemAdministrationConfiguration candidate, AdministrationTransition transition) =>
        receipt.TransitionId == transition.Id && receipt.CandidateVersion == transition.CandidateVersion
        && receipt.Mode == candidate.Mode && receipt.PrincipalObjectId == Principal(candidate)
        && receipt.TenantId == candidate.TenantId && receipt.SqlTargetKey == candidate.SqlTargetKey
        && receipt.SqlAdministratorObjectId == Principal(candidate)
        && receipt.Complete && receipt.CompositionValidated && receipt.AccountEnabled && receipt.UserType == "Member"
        && receipt.IsActive && receipt.CapabilityInventoryPassed
        && (receipt.Stack == "sql" ? receipt.SqlBootstrapPassed : !receipt.SqlBootstrapPassed)
        && !string.IsNullOrWhiteSpace(receipt.RuntimeVersion) && !string.IsNullOrWhiteSpace(receipt.CapabilityInventoryVersion)
        && !string.IsNullOrWhiteSpace(receipt.CorrelationId)
        && receipt.CollectionStartedAt <= receipt.CollectedAt && receipt.CollectedAt <= receipt.VerifiedAt
        && receipt.VerifiedAt <= Now && receipt.ExpiresAt > Now && receipt.ExpiresAt <= receipt.EvidenceExpiresAt
        && receipt.EvidenceExpiresAt <= receipt.CollectionStartedAt.AddMinutes(5);

    private bool ValidGroupProof(DirectoryAdministrationEvidence? evidence, SystemAdministrationConfiguration candidate, string actor) =>
        evidence is not null && evidence.TenantId == candidate.TenantId && evidence.GroupObjectId == candidate.GroupObjectId
        && evidence.AuthorityVersion == candidate.AuthorityVersion && evidence.ActorObjectId == actor
        && evidence.AccountEnabled == true && evidence.UserType == "Member" && evidence.Complete && evidence.CompositionValidated
        && evidence.DirectMemberObjectIds.Contains(actor) && evidence.EligibleUserObjectIds.Contains(actor)
        && evidence.CollectionStartedAt <= evidence.CollectedAt && evidence.CollectedAt <= Now
        && evidence.ExpiresAt > Now && evidence.ExpiresAt <= evidence.CollectionStartedAt.AddMinutes(5);

    private static bool ValidMode(SystemAdministrationConfiguration candidate) =>
        !string.IsNullOrWhiteSpace(candidate.TenantId) && !string.IsNullOrWhiteSpace(candidate.SqlTargetKey)
        && (candidate.Mode == "group"
            ? !string.IsNullOrWhiteSpace(candidate.GroupObjectId) && candidate.LegacyIndividualObjectId is null && candidate.SystemRoleGroupMappingId is not null
            : candidate.Mode == "individual" && candidate.GroupObjectId is null
                && !string.IsNullOrWhiteSpace(candidate.LegacyIndividualObjectId) && candidate.SystemRoleGroupMappingId is null);

    private static bool ValidCheckpoint(AdministrationTransition current, AdministrationCheckpointRequest request, bool failureResume = false)
    {
        var next = current.Phase switch
        {
            "preflight" => "prepared", "prepared" => "sql_switched", "sql_switched" => "replacement_verified",
            "authority_committed" => "retiring", "retiring" => "complete", "rollback_pending" => "prepared", _ => "",
        };
        if (request.Phase == "authority_committed"
            || (request.Phase != current.Phase && request.Phase != next && !failureResume
                && !(current.Phase == "retiring" && request.Phase == "rolled_back")
                && request.Phase is not ("failed" or "rollback_pending")))
            return false;
        try
        {
            using var old = JsonDocument.Parse(current.ExternalOperations);
            using var updated = JsonDocument.Parse(request.ExternalOperations);
            if (old.RootElement.ValueKind != JsonValueKind.Array || updated.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            var oldOperations = old.RootElement.EnumerateArray().ToArray();
            var operations = updated.RootElement.EnumerateArray().ToArray();
            string Id(JsonElement item) => item.TryGetProperty("operationId", out var id) ? id.GetString() ?? ""
                : item.TryGetProperty("id", out id) ? id.GetString() ?? "" : "";
            bool Completed(JsonElement item) => item.TryGetProperty("completed", out var complete) && complete.ValueKind == JsonValueKind.True
                || item.TryGetProperty("result", out var result) && result.GetString() == "verified";
            bool Intent(JsonElement item) => item.TryGetProperty("intentRecorded", out var intent) && intent.ValueKind == JsonValueKind.True
                || item.TryGetProperty("intentAt", out var at) && !string.IsNullOrWhiteSpace(at.GetString());
            if (operations.Any(item => string.IsNullOrWhiteSpace(Id(item)) || !Intent(item))
                || operations.Select(Id).Distinct().Count() != operations.Length)
                return false;
            foreach (var previous in oldOperations)
            {
                var matching = operations.Where(item => Id(item) == Id(previous)).ToArray();
                if (matching.Length != 1 || (Completed(previous) && matching[0].GetRawText() != previous.GetRawText()))
                    return false;
                foreach (var property in new[] { "plane", "intentAt", "intentRecorded" })
                    if (previous.TryGetProperty(property, out var before)
                        && (!matching[0].TryGetProperty(property, out var after) || before.GetRawText() != after.GetRawText()))
                        return false;
            }
            foreach (var operation in operations.Where(Completed))
                if (!oldOperations.Any(previous => Id(previous) == Id(operation) && Intent(previous))
                    || !(operation.TryGetProperty("readBackHash", out var hash) || operation.TryGetProperty("afterHash", out hash))
                    || string.IsNullOrWhiteSpace(hash.GetString()))
                    return false;
            if (request.Phase is "sql_switched" or "replacement_verified"
                && !operations.Any(item => Completed(item) && item.TryGetProperty("plane", out var plane)
                    && plane.GetString() is "sql" or "sqlAdministrator"))
                return false;
            if (request.Phase == "complete" && operations.Any(item => !Completed(item)))
                return false;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private ProcessingEvent Audit(AdministrationTransition transition, string action, string result, string? subject = null,
        string? actor = null, string? correlation = null)
    {
        var candidate = Parse(transition.CandidateConfiguration);
        var audit = new ProcessingEvent
        {
            Actor = actor ?? transition.ActorId, EventType = action, EntityType = "AdministrationTransition",
            EntityId = transition.Id, CorrelationId = correlation ?? transition.CorrelationId, Timestamp = Now,
            PayloadJson = JsonSerializer.Serialize(new
            {
                transition.TargetKey, transition.Phase, AuthorityVersion = transition.CandidateVersion,
                PrincipalObjectId = candidate is null ? null : Principal(candidate), SubjectObjectId = subject, Result = result,
                transition.SupersedesTransitionId, transition.SupersededByTransitionId, transition.FencingToken,
            }),
        };
        context.ProcessingEvents.Add(audit);
        return audit;
    }

    private static SystemAdministrationConfiguration? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<SystemAdministrationConfiguration>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
    private static string? Principal(SystemAdministrationConfiguration candidate) =>
        candidate.Mode == "group" ? candidate.GroupObjectId : candidate.LegacyIndividualObjectId;
    private static EntraAccessManagementException Conflict() =>
        new("administration_transition_conflict", "Administration state changed. Refresh and retry the current transition.");
}
