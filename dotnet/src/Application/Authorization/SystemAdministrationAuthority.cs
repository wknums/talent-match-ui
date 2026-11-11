using TalentMatch.Application.Common.Interfaces;

namespace TalentMatch.Application.Authorization;

/// <summary>Request-local authority, never a persisted personal Admin grant.</summary>
public static class SystemAdministrationAuthority
{
    public static bool HasFreshGroupEvidence(CurrentAuthorizationState state, DateTimeOffset now)
    {
        var configuration = state.AdministrationConfiguration;
        var evidence = state.AdministrationEvidence;
        return configuration?.Mode == "group" && evidence is not null
            && evidence.Complete && evidence.CompositionValidated && evidence.AccountEnabled == true
            && evidence.UserType == "Member" && evidence.TenantId == state.Claims.TenantId
            && evidence.ActorObjectId == state.Claims.ObjectId
            && evidence.GroupObjectId == configuration.GroupObjectId
            && evidence.AuthorityVersion == configuration.AuthorityVersion
            && evidence.CollectionStartedAt <= evidence.CollectedAt && evidence.CollectedAt <= now.UtcDateTime
            && evidence.ExpiresAt > now.UtcDateTime
            && evidence.ExpiresAt <= evidence.CollectionStartedAt.AddMinutes(5);
    }

    public static bool IsGlobalAdmin(CurrentAuthorizationState state, DateTimeOffset? evaluationTime = null)
    {
        var now = evaluationTime ?? DateTimeOffset.UtcNow;
        if (state.User?.IsActive != true || state.Claims.TenantId != state.ExpectedTenantId
            || state.Claims.TokenIssuedAt > now.AddSeconds(30)
            || state.Claims.TokenIssuedAt.AddMinutes(15) <= now || state.Claims.TokenExpiresAt <= now)
            return false;
        var configuration = state.AdministrationConfiguration;
        if (configuration?.Mode == "group")
            return HasFreshGroupEvidence(state, now)
                && state.AdministrationEvidence!.DirectMemberObjectIds.Contains(state.Claims.ObjectId)
                && state.AdministrationEvidence.EligibleUserObjectIds.Contains(state.Claims.ObjectId);
        if (configuration is not null)
        {
            var proof = state.AdministrationIndividualEvidence;
            if (configuration.Mode != "individual" || configuration.LegacyIndividualObjectId != state.Claims.ObjectId
                || proof is null || proof.AccountEnabled != true || proof.UserType != "Member"
                || !proof.DirectAdminRoleBinding || proof.TenantId != state.Claims.TenantId
                || proof.ObjectId != state.Claims.ObjectId || proof.AuthorityVersion != configuration.AuthorityVersion
                || proof.CollectionStartedAt > proof.CollectedAt || proof.CollectedAt > now.UtcDateTime
                || proof.ExpiresAt <= now.UtcDateTime || proof.ExpiresAt > proof.CollectionStartedAt.AddMinutes(5))
                return false;
        }
        return state.Assignments.Any(x => x.Status == "active" && x.Role == "admin"
            && x.OrganizationId is null && x.DepartmentId is null
            && (configuration is null || x.Source == "bootstrap"));
    }
}
