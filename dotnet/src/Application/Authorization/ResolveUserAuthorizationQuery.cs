using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Authorization;

public sealed record ResolveUserAuthorizationQuery(string? CandidateTransitionId = null) : IRequest<AuthorizationResolutionResult>;

public sealed record AuthorizationResolutionResult(
    AuthorizationContextResponse? Context,
    AuthorizationErrorResponse? Error,
    bool PendingProfileCreated = false)
{
    public bool IsSuccess => Context is not null;

    public static AuthorizationResolutionResult Success(AuthorizationContextResponse context) => new(context, null);

    public static AuthorizationResolutionResult Failure(
        string code,
        string message,
        int statusCode,
        bool pendingProfileCreated = false) =>
        new(null, new AuthorizationErrorResponse(code, message, statusCode), pendingProfileCreated);
}

public sealed record AuthorizationErrorResponse(string Code, string Message, int StatusCode);

public sealed record AuthorizationContextResponse(
    string UserId,
    string TenantId,
    string ObjectId,
    string Username,
    string FullName,
    string? Email,
    string? GlobalRole,
    int AuthorizationVersion,
    IReadOnlyList<OrganizationMembershipResponse> Memberships,
    IReadOnlyList<ScopedAuthorizationResponse> Authorizations,
    DateTimeOffset TokenIssuedAt,
    DateTimeOffset RefreshRequiredAt,
    string AdministrationMode = "individual",
    int AuthorityVersion = 0,
    AdminEntitlementResponse? AdminEntitlement = null);

public sealed record AdminEntitlementResponse(string Source, DateTimeOffset EvaluatedAt, DateTimeOffset ValidUntil);

public sealed record OrganizationMembershipResponse(
    string OrganizationId,
    string OrganizationName,
    string DefaultDepartmentId,
    IReadOnlyList<DepartmentMembershipResponse> Departments);

public sealed record DepartmentMembershipResponse(string DepartmentId, string DepartmentName);

public sealed record ScopedAuthorizationResponse(
    string Role,
    string RoleLabel,
    string? OrganizationId,
    string? DepartmentId,
    string AssignmentSource);

public sealed class ResolveUserAuthorizationQueryHandler(
    ICurrentUserService currentUser,
    IUserRepository userRepository,
    IEntraAccessManagementRepository? accessRepository = null,
    TimeProvider? clock = null)
    : IRequestHandler<ResolveUserAuthorizationQuery, AuthorizationResolutionResult>
{
    private static readonly HashSet<string> SupportedRoles =
    [
        "admin",
        "organization_admin",
        "recruiter",
        "business_panel",
    ];

    public async Task<AuthorizationResolutionResult> Handle(
        ResolveUserAuthorizationQuery request,
        CancellationToken cancellationToken)
    {
        CurrentAuthorizationState? state;
        try { state = await currentUser.GetAuthorizationStateAsync(cancellationToken); }
        catch (DirectoryAdministrationEvidenceException exception) { return Failure(exception.Code, "", 403); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure("group_evidence_unavailable", "", 403); }
        if (state is null)
            return Failure("auth_required", "Sign in is required to access this application.", 401);

        if (!string.Equals(state.Claims.TenantId, state.ExpectedTenantId, StringComparison.OrdinalIgnoreCase))
            return Failure("wrong_tenant", "Sign in with an account from the configured organization.", 401);

        var resolutionTime = (clock ?? TimeProvider.System).GetUtcNow();
        if (resolutionTime - state.Claims.TokenIssuedAt > TimeSpan.FromMinutes(15))
            return Failure("token_stale", "Your session must be refreshed before continuing.", 401);
        if (state.Claims.TokenIssuedAt > resolutionTime.AddSeconds(30))
            return Failure("invalid_token", "Your session could not be validated.", 401);
        if (state.Claims.TokenExpiresAt <= resolutionTime)
            return Failure("invalid_token", "Your session has expired.", 401);

        if (state.Claims.Roles.Any(role => !SupportedRoles.Contains(role)))
            return Failure("role_conflict", "Your assigned access could not be verified.", 403);

        var administration = state.AdministrationConfiguration;
        var evidence = state.AdministrationEvidence;
        var computedAdmin = false;
        if (state.User is { IsActive: false })
            return Failure("identity_disabled", "", 403);
        if (state.DirectoryActorEvidence is { } actorEvidence)
        {
            if (!actorEvidence.AccountEnabled) return Failure("identity_disabled", "", 403);
            if (actorEvidence.UserType != "Member" || actorEvidence.TenantId != state.Claims.TenantId
                || actorEvidence.ObjectId != state.Claims.ObjectId || actorEvidence.AuthorityVersion != administration?.AuthorityVersion
                || actorEvidence.CollectionStartedAt > actorEvidence.CollectedAt || actorEvidence.CollectedAt > resolutionTime.UtcDateTime
                || actorEvidence.ExpiresAt <= resolutionTime.UtcDateTime || actorEvidence.ExpiresAt > actorEvidence.CollectionStartedAt.AddMinutes(5))
                return Failure("group_evidence_unavailable", "", 403);
        }
        if (administration?.Mode == "group")
        {
            var now = resolutionTime.UtcDateTime;
            if (evidence?.AccountEnabled == false) return Failure("identity_disabled", "", 403);
            if (evidence is null || !evidence.Complete || !evidence.CompositionValidated
                || evidence.AccountEnabled != true || evidence.UserType != "Member"
                || evidence.TenantId != state.Claims.TenantId || evidence.ActorObjectId != state.Claims.ObjectId
                || evidence.GroupObjectId != administration.GroupObjectId || evidence.AuthorityVersion != administration.AuthorityVersion
                || evidence.CollectionStartedAt > evidence.CollectedAt || evidence.CollectedAt > now
                || evidence.ExpiresAt <= now || evidence.ExpiresAt > evidence.CollectionStartedAt.AddMinutes(5))
                return Failure("group_evidence_unavailable", "", 403);
            computedAdmin = evidence.DirectMemberObjectIds.Contains(state.Claims.ObjectId)
                && evidence.EligibleUserObjectIds.Contains(state.Claims.ObjectId);
            if (computedAdmin)
            {
                if (accessRepository is null) return Failure("admin_configuration_invalid", "", 403);
                try
                {
                    await accessRepository.ConvergeSystemAdministratorContextAsync(new(
                        state.Claims.TenantId, state.Claims.ObjectId,
                        new(state.Claims.Username, state.Claims.FullName, state.Claims.Email),
                        state.User?.AuthorizationVersion, administration.AuthorityVersion,
                        currentUser.CorrelationId ?? Guid.NewGuid().ToString(), evidence, administration.Id,
                        request.CandidateTransitionId), cancellationToken);
                }
                catch (EntraAccessManagementException exception) { return Failure(exception.Code, "", 403); }
                try { state = await currentUser.GetAuthorizationStateAsync(cancellationToken); }
                catch (DirectoryAdministrationEvidenceException exception) { return Failure(exception.Code, "", 403); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return Failure("group_evidence_unavailable", "", 403); }
                if (state?.AdministrationConfiguration?.Id != administration.Id
                    || state.AdministrationConfiguration.AuthorityVersion != administration.AuthorityVersion)
                    return Failure("administration_transition_conflict", "", 409);
                if (state.User is { IsActive: false } || state.AdministrationEvidence?.AccountEnabled == false)
                    return Failure("identity_disabled", "", 403);
                evidence = state.AdministrationEvidence;
                if (!SystemAdministrationAuthority.HasFreshGroupEvidence(state, (clock ?? TimeProvider.System).GetUtcNow()))
                    return Failure("group_evidence_unavailable", "", 403);
                computedAdmin = evidence!.DirectMemberObjectIds.Contains(state.Claims.ObjectId)
                    && evidence.EligibleUserObjectIds.Contains(state.Claims.ObjectId);
            }
        }
        var user = state.User;
        if (user is null)
        {
            await userRepository.UpsertEntraProfileAsync(new User
            {
                Id = state.Claims.ObjectId,
                AuthenticationProvider = "entra",
                EntraTenantId = state.Claims.TenantId,
                EntraObjectId = state.Claims.ObjectId,
                Username = state.Claims.Username,
                FullName = state.Claims.FullName,
                Email = state.Claims.Email,
                PasswordHash = null,
                IsActive = true,
                AuthorizationVersion = 0,
            }, cancellationToken);
            return Failure(
                "assignment_missing",
                "No active application access is assigned to this identity.",
                403,
                pendingProfileCreated: true);
        }

        if (!string.Equals(user.AuthenticationProvider, "entra", StringComparison.Ordinal)
            || !string.Equals(user.EntraTenantId, state.Claims.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.EntraObjectId, state.Claims.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("assignment_missing", "No active application access is assigned to this identity.", 403);
        }

        user = await userRepository.UpsertEntraProfileAsync(new User
        {
            Id = user.Id,
            AuthenticationProvider = "entra",
            EntraTenantId = state.Claims.TenantId,
            EntraObjectId = state.Claims.ObjectId,
            Username = state.Claims.Username,
            FullName = state.Claims.FullName,
            Email = state.Claims.Email,
            PasswordHash = null,
            IsActive = user.IsActive,
            AuthorizationVersion = user.AuthorizationVersion,
            CreatedAt = user.CreatedAt,
            LastLogin = user.LastLogin,
        }, cancellationToken);

        if (!user.IsActive)
            return Failure("identity_disabled", "This application identity is disabled.", 403);

        var assignments = administration is null ? state.Assignments.ToList()
            : state.Assignments.Where(x => x.Role != "admin" || (administration.Mode == "individual"
                && administration.LegacyIndividualObjectId == state.Claims.ObjectId && x.Source == "bootstrap")).ToList();
        if (assignments.Count == 0 && !computedAdmin)
            return Failure("assignment_missing", "No active application access is assigned to this identity.", 403);

        var memberships = BuildMemberships(state);
        if (memberships is null || memberships.Count == 0)
            return Failure("membership_missing", "An active organization and department membership is required.", 403);

        var organizationIds = memberships.Select(membership => membership.OrganizationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var departmentScopes = memberships
            .SelectMany(membership => membership.Departments.Select(department => (membership.OrganizationId, department.DepartmentId)))
            .ToHashSet();
        var mappings = state.GroupMappings.ToDictionary(mapping => mapping.Id, StringComparer.OrdinalIgnoreCase);
        var authorizations = new List<ScopedAuthorizationResponse>();

        foreach (var assignment in assignments)
        {
            var validationError = ValidateAssignment(
                assignment,
                state,
                organizationIds,
                departmentScopes,
                mappings, resolutionTime.UtcDateTime);
            if (validationError is not null)
            {
                if (computedAdmin) continue;
                return validationError;
            }

            authorizations.Add(new ScopedAuthorizationResponse(
                assignment.Role,
                GetRoleLabel(assignment.Role),
                assignment.OrganizationId,
                assignment.DepartmentId,
                assignment.Source));
        }

        if (computedAdmin)
            authorizations.Add(new("admin", "Admin", null, null, "system_group"));
        if (evidence is not null && evidence.ExpiresAt <= (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime)
            return Failure("group_evidence_unavailable", "", 403);
        var refresh = state.Claims.TokenIssuedAt.AddMinutes(15);
        if (state.Claims.TokenExpiresAt is { } tokenExpiry && tokenExpiry < refresh)
            refresh = tokenExpiry;
        if (evidence is not null && new DateTimeOffset(evidence.ExpiresAt) < refresh)
            refresh = new DateTimeOffset(evidence.ExpiresAt);
        var individualEvidence = state.AdministrationIndividualEvidence;
        if (individualEvidence is not null && new DateTimeOffset(individualEvidence.ExpiresAt) < refresh)
            refresh = new DateTimeOffset(individualEvidence.ExpiresAt);
        if (state.DirectoryActorEvidence is { } directoryActor && new DateTimeOffset(directoryActor.ExpiresAt) < refresh)
            refresh = new DateTimeOffset(directoryActor.ExpiresAt);
        return AuthorizationResolutionResult.Success(new AuthorizationContextResponse(
            user.Id,
            state.Claims.TenantId,
            state.Claims.ObjectId,
            user.Username,
            user.FullName,
            string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
            authorizations.Any(authorization => authorization.Role == "admin") ? "admin" : null,
            user.AuthorizationVersion,
            memberships,
            authorizations,
            state.Claims.TokenIssuedAt,
            refresh,
            administration?.Mode ?? "individual",
            administration?.AuthorityVersion ?? 0,
            computedAdmin ? new("system_group", new DateTimeOffset(evidence!.CollectedAt), new DateTimeOffset(evidence.ExpiresAt))
                : authorizations.Any(x => x.Role == "admin") && individualEvidence is not null
                    ? new("legacy_individual", new DateTimeOffset(individualEvidence.CollectedAt), new DateTimeOffset(individualEvidence.ExpiresAt)) : null));
    }

    private static IReadOnlyList<OrganizationMembershipResponse>? BuildMemberships(CurrentAuthorizationState state)
    {
        var result = new List<OrganizationMembershipResponse>();
        foreach (var membership in state.OrganizationMemberships.Where(membership =>
            membership.Status == "active" && membership.Organization.Status == "active"))
        {
            var departmentMemberships = state.DepartmentMemberships
                .Where(departmentMembership =>
                    departmentMembership.Status == "active"
                    && departmentMembership.OrganizationId == membership.OrganizationId
                    && departmentMembership.Organization.Status == "active"
                    && departmentMembership.Department.Status == "active")
                .ToList();
            var defaultMembership = departmentMemberships.SingleOrDefault(departmentMembership =>
                departmentMembership.Id == membership.DefaultDepartmentMembershipId);
            if (defaultMembership is null)
                return null;

            result.Add(new OrganizationMembershipResponse(
                membership.OrganizationId,
                membership.Organization.Name,
                defaultMembership.DepartmentId,
                departmentMemberships
                    .Select(departmentMembership => new DepartmentMembershipResponse(
                        departmentMembership.DepartmentId,
                        departmentMembership.Department.Name))
                    .ToList()));
        }
        return result;
    }

    private static AuthorizationResolutionResult? ValidateAssignment(
        RoleAssignment assignment,
        CurrentAuthorizationState state,
        IReadOnlySet<string> organizationIds,
        IReadOnlySet<(string OrganizationId, string DepartmentId)> departmentScopes,
        IReadOnlyDictionary<string, RoleGroupMapping> mappings,
        DateTime now)
    {
        if (assignment.Status != "active"
            || !string.Equals(assignment.UserId, state.User!.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.TenantId, state.Claims.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.UserObjectId, state.Claims.ObjectId, StringComparison.OrdinalIgnoreCase)
            || !SupportedRoles.Contains(assignment.Role)
            || !HasValidScopeShape(assignment))
        {
            return Failure("role_conflict", "Your assigned access could not be verified.", 403);
        }

        if (assignment.OrganizationId is not null
            && !organizationIds.Contains(assignment.OrganizationId))
        {
            return Failure("membership_missing", "An active membership is required for the assigned organization.", 403);
        }

        if (assignment.DepartmentId is not null
            && !departmentScopes.Contains((assignment.OrganizationId!, assignment.DepartmentId)))
        {
            return Failure("membership_missing", "An active membership is required for the assigned department.", 403);
        }

        return assignment.Source switch
        {
            "delegated" => ValidateDelegatedAssignment(assignment),
            "bootstrap" => ValidateBootstrapAssignment(assignment, state, now),
            "group" => ValidateGroupAssignment(assignment, state, mappings),
            _ => Failure("role_conflict", "Your assigned access could not be verified.", 403),
        };
    }

    private static AuthorizationResolutionResult? ValidateDelegatedAssignment(RoleAssignment assignment)
        => assignment.Role != "admin" && assignment.RoleGroupMappingId is null
            ? null
            : Failure("role_conflict", "Your delegated access could not be verified.", 403);

    private static AuthorizationResolutionResult? ValidateBootstrapAssignment(
        RoleAssignment assignment,
        CurrentAuthorizationState state, DateTime now)
    {
        if (state.AdministrationConfiguration is { } configured)
        {
            var proof = state.AdministrationIndividualEvidence;
            if (configured.Mode != "individual" || configured.LegacyIndividualObjectId != state.Claims.ObjectId
                || proof is null || !proof.DirectAdminRoleBinding || proof.AccountEnabled != true || proof.UserType != "Member"
                || proof.ObjectId != state.Claims.ObjectId || proof.TenantId != state.Claims.TenantId
                || proof.AuthorityVersion != configured.AuthorityVersion || proof.ExpiresAt <= now
                || proof.CollectionStartedAt > proof.CollectedAt || proof.CollectedAt > now
                || proof.ExpiresAt > proof.CollectionStartedAt.AddMinutes(5))
                return Failure("assignment_missing", "", 403);
        }
        return assignment.Role == "admin"
            && assignment.RoleGroupMappingId is null
            && string.Equals(state.AdministrationConfiguration?.LegacyIndividualObjectId ?? state.BootstrapObjectId,
                state.Claims.ObjectId, StringComparison.OrdinalIgnoreCase)
            && state.Claims.Roles.Contains("admin")
                ? null
                : Failure("role_conflict", "Your bootstrap access could not be verified.", 403);
    }

    private static AuthorizationResolutionResult? ValidateGroupAssignment(
        RoleAssignment assignment,
        CurrentAuthorizationState state,
        IReadOnlyDictionary<string, RoleGroupMapping> mappings)
    {
        if (assignment.RoleGroupMappingId is null
            || !mappings.TryGetValue(assignment.RoleGroupMappingId, out var mapping))
        {
            return Failure("scope_unmapped", "Your group access is not mapped to an active application scope.", 403);
        }

        if (!mapping.Enabled
            || !string.Equals(mapping.TenantId, assignment.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mapping.Role, assignment.Role, StringComparison.Ordinal)
            || !string.Equals(mapping.OrganizationId, assignment.OrganizationId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mapping.DepartmentId, assignment.DepartmentId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("role_conflict", "Your group role and application assignment do not agree.", 403);
        }

        if (!state.Claims.Roles.Contains(assignment.Role))
            return Failure("role_missing", "The required application role is missing from your session.", 403);

        return state.Claims.Groups.Contains(mapping.GroupObjectId)
            ? null
            : Failure("scope_unmapped", "Your group access is not mapped to an active application scope.", 403);
    }

    private static bool HasValidScopeShape(RoleAssignment assignment)
        => assignment.Role switch
        {
            "admin" => assignment.OrganizationId is null && assignment.DepartmentId is null,
            "organization_admin" => assignment.OrganizationId is not null && assignment.DepartmentId is null,
            "recruiter" => assignment.OrganizationId is not null && assignment.DepartmentId is not null,
            "business_panel" => assignment.OrganizationId is not null,
            _ => false,
        };

    private static string GetRoleLabel(string role)
        => role switch
        {
            "admin" => "Admin",
            "organization_admin" => "Organization Admin",
            "recruiter" => "Recruiter",
            "business_panel" => "Analytics Viewer",
            _ => throw new InvalidOperationException("Unsupported role."),
        };

    private static AuthorizationResolutionResult Failure(
        string code,
        string message,
        int statusCode,
        bool pendingProfileCreated = false)
    {
        var error = AuthorizationErrorCodes.Resolve(code);
        return AuthorizationResolutionResult.Failure(
            error.Code,
            error.Message,
            error.StatusCode,
            pendingProfileCreated);
    }
}