namespace TalentMatch.Application.Authorization;

public static class AuthorizationErrorCodes
{
    public const string AuthRequired = "auth_required";
    public const string InvalidToken = "invalid_token";
    public const string WrongTenant = "wrong_tenant";
    public const string InvalidAudience = "invalid_audience";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string TokenStale = "token_stale";
    public const string RoleMissing = "role_missing";
    public const string RoleConflict = "role_conflict";
    public const string AssignmentMissing = "assignment_missing";
    public const string AssignmentRevoked = "assignment_revoked";
    public const string ScopeUnmapped = "scope_unmapped";
    public const string MembershipMissing = "membership_missing";
    public const string IdentityDisabled = "identity_disabled";
    public const string InvalidScope = "invalid_scope";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string VersionConflict = "version_conflict";
    public const string InvalidJobScope = "invalid_job_scope";
    public const string Conflict = "conflict";
    public const string ServiceUnavailable = "service_unavailable";
    public const string PersistenceUnavailable = "persistence_unavailable";
    public const string AuditUnavailable = "audit_unavailable";
    public const string InternalError = "internal_error";

    public static AuthorizationErrorDefinition Resolve(string code) => code switch
    {
        AuthRequired => new(code, 401, "Sign in is required to access this application."),
        InvalidToken => new(code, 401, "Your session could not be validated. Sign in again."),
        WrongTenant => new(code, 401, "Sign in with an account from the configured organization."),
        InvalidAudience => new(code, 401, "This session is not valid for the TalentMatch API."),
        UnauthorizedClient => new(code, 401, "This application is not authorized to call the TalentMatch API."),
        TokenStale => new(code, 401, "Your session must be refreshed before continuing."),
        RoleMissing => new(code, 403, "No supported application role is assigned to this identity."),
        RoleConflict => new(code, 403, "Your assigned access could not be verified."),
        AssignmentMissing => new(code, 403, "No active application access is assigned to this identity."),
        AssignmentRevoked => new(code, 403, "The application access assigned to this identity is no longer active."),
        ScopeUnmapped => new(code, 403, "The assigned application scope could not be verified."),
        MembershipMissing => new(code, 403, "An active organization and department membership is required."),
        IdentityDisabled => new(code, 403, "This application identity is disabled."),
        InvalidScope => new(code, 400, "The request has an invalid scope."),
        Forbidden => new(code, 403, "You do not have permission to perform this operation."),
        NotFound => new(code, 404, "The requested resource was not found."),
        VersionConflict => new(code, 409, "The resource changed after it was loaded. Reload and try again."),
        InvalidJobScope => new(code, 400, "The job organization and department do not form a valid pair."),
        Conflict => new(code, 409, "The requested change conflicts with the current state."),
        ServiceUnavailable or PersistenceUnavailable or AuditUnavailable =>
            new(code, 503, "The requested operation is temporarily unavailable."),
        InternalError => new(code, 500, "The request could not be completed."),
        _ => new(InternalError, 500, "The request could not be completed."),
    };
}

public sealed record AuthorizationErrorDefinition(string Code, int StatusCode, string Message);