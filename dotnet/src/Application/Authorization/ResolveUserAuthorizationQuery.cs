using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Authorization;

public sealed record ResolveUserAuthorizationQuery : IRequest<AuthorizationResolutionResult>;

public sealed record AuthorizationResolutionResult(
    AuthorizationContextResponse? Context,
    AuthorizationErrorResponse? Error)
{
    public bool IsSuccess => Context is not null;

    public static AuthorizationResolutionResult Success(AuthorizationContextResponse context) => new(context, null);

    public static AuthorizationResolutionResult Failure(string code, string message, int statusCode) =>
        new(null, new AuthorizationErrorResponse(code, message, statusCode));
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
    IReadOnlyList<OrganizationMembershipResponse> Memberships,
    IReadOnlyList<ScopedAuthorizationResponse> Authorizations,
    DateTimeOffset TokenIssuedAt,
    DateTimeOffset RefreshRequiredAt);

public sealed record OrganizationMembershipResponse(
    string OrganizationId,
    string OrganizationName,
    IReadOnlyList<DepartmentMembershipResponse> Departments);

public sealed record DepartmentMembershipResponse(string DepartmentId, string DepartmentName);

public sealed record ScopedAuthorizationResponse(
    string Role,
    string RoleLabel,
    string? OrganizationId,
    string? DepartmentId,
    string AssignmentSource);

public sealed class ResolveUserAuthorizationQueryHandler(ICurrentUserService currentUser)
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
        var state = await currentUser.GetAuthorizationStateAsync(cancellationToken);
        if (state is null)
            return Failure("auth_required", "Sign in is required to access this application.", 401);

        if (!string.Equals(state.Claims.TenantId, state.ExpectedTenantId, StringComparison.OrdinalIgnoreCase))
            return Failure("wrong_tenant", "Sign in with an account from the configured organization.", 401);

        if (DateTimeOffset.UtcNow - state.Claims.TokenIssuedAt > TimeSpan.FromMinutes(15))
            return Failure("token_stale", "Your session must be refreshed before continuing.", 401);

        if (state.Claims.Roles.Any(role => !SupportedRoles.Contains(role)))
            return Failure("role_conflict", "Your assigned access could not be verified.", 403);

        var user = state.User;
        if (user is null
            || !string.Equals(user.AuthenticationProvider, "entra", StringComparison.Ordinal)
            || !string.Equals(user.EntraTenantId, state.Claims.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(user.EntraObjectId, state.Claims.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("assignment_missing", "No active application access is assigned to this identity.", 403);
        }

        if (!user.IsActive)
            return Failure("identity_disabled", "This application identity is disabled.", 403);

        if (state.Assignments.Count == 0)
            return Failure("assignment_missing", "No active application access is assigned to this identity.", 403);

        var memberships = BuildMemberships(state);
        if (memberships.Count == 0)
            return Failure("membership_missing", "An active organization and department membership is required.", 403);

        var organizationIds = memberships.Select(membership => membership.OrganizationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var departmentScopes = memberships
            .SelectMany(membership => membership.Departments.Select(department => (membership.OrganizationId, department.DepartmentId)))
            .ToHashSet();
        var mappings = state.GroupMappings.ToDictionary(mapping => mapping.Id, StringComparer.OrdinalIgnoreCase);
        var authorizations = new List<ScopedAuthorizationResponse>();

        foreach (var assignment in state.Assignments)
        {
            var validationError = ValidateAssignment(
                assignment,
                state,
                organizationIds,
                departmentScopes,
                mappings);
            if (validationError is not null)
                return validationError;

            authorizations.Add(new ScopedAuthorizationResponse(
                assignment.Role,
                GetRoleLabel(assignment.Role),
                assignment.OrganizationId,
                assignment.DepartmentId,
                assignment.Source));
        }

        return AuthorizationResolutionResult.Success(new AuthorizationContextResponse(
            user.Id,
            state.Claims.TenantId,
            state.Claims.ObjectId,
            user.Username,
            user.FullName,
            string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
            authorizations.Any(authorization => authorization.Role == "admin") ? "admin" : null,
            memberships,
            authorizations,
            state.Claims.TokenIssuedAt,
            state.Claims.TokenIssuedAt.AddMinutes(15)));
    }

    private static IReadOnlyList<OrganizationMembershipResponse> BuildMemberships(CurrentAuthorizationState state)
    {
        return state.OrganizationMemberships
            .Where(membership => membership.Status == "active")
            .Select(membership => new OrganizationMembershipResponse(
                membership.OrganizationId,
                membership.Organization.Name,
                state.DepartmentMemberships
                    .Where(departmentMembership =>
                        departmentMembership.Status == "active"
                        && departmentMembership.OrganizationId == membership.OrganizationId)
                    .Select(departmentMembership => new DepartmentMembershipResponse(
                        departmentMembership.DepartmentId,
                        departmentMembership.Department.Name))
                    .ToList()))
            .Where(membership => membership.Departments.Count > 0)
            .ToList();
    }

    private static AuthorizationResolutionResult? ValidateAssignment(
        RoleAssignment assignment,
        CurrentAuthorizationState state,
        IReadOnlySet<string> organizationIds,
        IReadOnlySet<(string OrganizationId, string DepartmentId)> departmentScopes,
        IReadOnlyDictionary<string, RoleGroupMapping> mappings)
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
            "bootstrap" => ValidateBootstrapAssignment(assignment, state),
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
        CurrentAuthorizationState state)
    {
        return assignment.Role == "admin"
            && assignment.RoleGroupMappingId is null
            && string.Equals(state.BootstrapObjectId, state.Claims.ObjectId, StringComparison.OrdinalIgnoreCase)
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

    private static AuthorizationResolutionResult Failure(string code, string message, int statusCode)
        => AuthorizationResolutionResult.Failure(code, message, statusCode);
}