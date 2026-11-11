using System.Text.Json;
using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Authorization;

public sealed record VerifyAdministrationCandidateCommand(string TransitionId, int CandidateVersion)
    : IRequest<CandidateVerificationReceipt>;
public sealed record CandidateVerificationReceipt(string ReceiptId, string TransitionId, int CandidateVersion,
    string MemberObjectId, string Stack, string RuntimeVersion, string CapabilityInventoryVersion,
    DateTime VerifiedAt, DateTime ExpiresAt, bool ProductionAuthorityGranted, string CorrelationId);

public sealed class VerifyAdministrationCandidateCommandHandler(
    ICurrentUserService currentUser, IAdministrationRepository administration, IUserRepository users,
    IEntraAccessManagementRepository access, TimeProvider clock)
    : IRequestHandler<VerifyAdministrationCandidateCommand, CandidateVerificationReceipt>
{
    public const string RuntimeVersion = "administration-candidate-v1";
    public const string InventoryVersion = "admin-capabilities-v1";
    public async Task<CandidateVerificationReceipt> Handle(VerifyAdministrationCandidateCommand request, CancellationToken cancellationToken)
    {
        var transition = await administration.GetTransitionAsync(request.TransitionId, cancellationToken);
        if (transition is null || transition.SupersededByTransitionId is not null
            || transition.Phase is not ("sql_switched" or "replacement_verified")
            || transition.LeaseExpiresAt <= clock.GetUtcNow().UtcDateTime || transition.CandidateVersion != request.CandidateVersion)
            throw Failure("administration_transition_conflict");
        SystemAdministrationConfiguration candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<SystemAdministrationConfiguration>(transition.CandidateConfiguration,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw Failure("administration_transition_conflict");
            using var metadata = JsonDocument.Parse(transition.CandidateConfiguration);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object
                || !metadata.RootElement.TryGetProperty("verificationRuntimes", out var runtimes)
                || runtimes.ValueKind != JsonValueKind.Object
                || !runtimes.TryGetProperty("stack-b", out var runtime) || runtime.ValueKind != JsonValueKind.String || runtime.GetString() != RuntimeVersion
                || !metadata.RootElement.TryGetProperty("capabilityInventoryVersion", out var inventory)
                || inventory.ValueKind != JsonValueKind.String || inventory.GetString() != InventoryVersion
                || candidate.AuthorityVersion != request.CandidateVersion
                || (candidate.Mode == "group" ? string.IsNullOrWhiteSpace(candidate.GroupObjectId)
                    || candidate.LegacyIndividualObjectId is not null || candidate.SystemRoleGroupMappingId is null
                    : candidate.Mode != "individual" || candidate.GroupObjectId is not null
                        || candidate.SystemRoleGroupMappingId is not null || string.IsNullOrWhiteSpace(candidate.LegacyIndividualObjectId)))
                throw Failure("administration_transition_conflict");
        }
        catch (JsonException) { throw Failure("administration_transition_conflict"); }
        var committed = await administration.GetCommittedAsync(new(candidate.EnvironmentKey, candidate.TenantId, candidate.SqlTargetKey), cancellationToken);
        if ((committed?.AuthorityVersion ?? 0) != transition.CommittedVersion) throw Failure("administration_transition_conflict");
        var candidateUser = new CandidateUser(currentUser, candidate);
        CurrentAuthorizationState state;
        try { state = await candidateUser.GetAuthorizationStateAsync(cancellationToken) ?? throw Failure("auth_required"); }
        catch (DirectoryAdministrationEvidenceException exception) { throw Failure(exception.Code); }
        if (state.User?.IsActive == false) throw Failure("identity_disabled");
        if (candidate.Mode == "individual")
        {
            var proof = state.AdministrationIndividualEvidence;
            var now = clock.GetUtcNow().UtcDateTime;
            if (candidate.LegacyIndividualObjectId != state.Claims.ObjectId || candidate.GroupObjectId is not null
                || proof is null || proof.AccountEnabled != true || proof.UserType != "Member" || !proof.DirectAdminRoleBinding
                || proof.TenantId != candidate.TenantId || proof.ObjectId != state.Claims.ObjectId
                || proof.AuthorityVersion != candidate.AuthorityVersion || proof.CollectionStartedAt > proof.CollectedAt
                || proof.CollectedAt > now || proof.ExpiresAt <= now || proof.ExpiresAt > proof.CollectionStartedAt.AddMinutes(5))
                throw Failure("group_evidence_unavailable");
            if (!state.Claims.Roles.Contains("admin")) throw Failure("role_missing");
            if (!state.Assignments.Any(x => x.UserId == state.User?.Id && x.TenantId == candidate.TenantId
                && x.UserObjectId == state.Claims.ObjectId && x.Status == "active" && x.Role == "admin"
                && x.Source == "bootstrap" && x.OrganizationId is null && x.DepartmentId is null
                && x.RoleGroupMappingId is null && x.ManagedTransitionId == transition.Id))
                throw Failure("assignment_missing");
        }
        else if (candidate.Mode != "group") throw Failure("administration_transition_conflict");
        var result = await new ResolveUserAuthorizationQueryHandler(candidateUser, users, access, clock)
            .Handle(new(transition.Id), cancellationToken);
        if (!result.IsSuccess) throw Failure(result.Error!.Code);
        var resolved = result.Context!;
        if (resolved.GlobalRole != "admin" || !resolved.Memberships.Any(x => x.OrganizationId == candidate.InitialOrganizationId
            && x.Departments.Any(d => d.DepartmentId == candidate.InitialDepartmentId)))
            throw Failure("membership_missing");
        // The resolver may converge the profile; re-read to preserve disable/version/evidence checks.
        state = await candidateUser.GetAuthorizationStateAsync(cancellationToken) ?? throw Failure("auth_required");
        if (state.User?.IsActive != true) throw Failure("identity_disabled");
        if (state.User.AuthorizationVersion != resolved.AuthorizationVersion) throw Failure("administration_transition_conflict");
        if (!SystemAdministrationAuthority.IsGlobalAdmin(state, clock.GetUtcNow())) throw Failure("group_evidence_unavailable");
        var evidenceStart = candidate.Mode == "group" ? state.AdministrationEvidence!.CollectionStartedAt : state.AdministrationIndividualEvidence!.CollectionStartedAt;
        var evidenceCollected = candidate.Mode == "group" ? state.AdministrationEvidence!.CollectedAt : state.AdministrationIndividualEvidence!.CollectedAt;
        var evidenceExpiry = candidate.Mode == "group" ? state.AdministrationEvidence!.ExpiresAt : state.AdministrationIndividualEvidence!.ExpiresAt;
        var principal = candidate.GroupObjectId ?? candidate.LegacyIndividualObjectId!;
        var receipt = new VerificationReceipt {
            TransitionId = transition.Id, CandidateVersion = transition.CandidateVersion, Mode = candidate.Mode,
            PrincipalObjectId = principal, TenantId = candidate.TenantId, MemberObjectId = state.Claims.ObjectId,
            CollectionStartedAt = evidenceStart, CollectedAt = evidenceCollected, EvidenceExpiresAt = evidenceExpiry,
            Complete = true, CompositionValidated = true, AccountEnabled = true, UserType = "Member", IsActive = true,
            Stack = "stack-b", RuntimeVersion = RuntimeVersion, CapabilityInventoryVersion = InventoryVersion,
            CapabilityInventoryPassed = true, SqlTargetKey = candidate.SqlTargetKey, SqlAdministratorObjectId = principal,
            SqlBootstrapPassed = false, VerifiedAt = clock.GetUtcNow().UtcDateTime, ExpiresAt = evidenceExpiry,
            CorrelationId = currentUser.CorrelationId ?? Guid.NewGuid().ToString()
        };
        if (!await administration.RecordReceiptAsync(new(new(transition.Id, transition.LeaseOwner, transition.FencingToken,
            transition.CommittedVersion), receipt, resolved.AuthorizationVersion), cancellationToken))
            throw Failure("administration_transition_conflict");
        return new(receipt.Id, receipt.TransitionId, receipt.CandidateVersion, receipt.MemberObjectId, receipt.Stack,
            receipt.RuntimeVersion, receipt.CapabilityInventoryVersion, receipt.VerifiedAt, receipt.ExpiresAt, false, receipt.CorrelationId);
    }
    private static EntraAccessManagementException Failure(string code) =>
        new(code, "Candidate verification is incomplete. Refresh proof or contact the deployment operator.");

    private sealed class CandidateUser(ICurrentUserService source, SystemAdministrationConfiguration candidate) : ICurrentUserService
    {
        public string? UserId => source.UserId;
        public string? Username => source.Username;
        public string? Role => null;
        public string? Department => null;
        public bool IsAdmin => false;
        public string? CorrelationId => source.CorrelationId;
        public Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default) =>
            source.GetCandidateAuthorizationStateAsync(candidate, cancellationToken);
    }
}
