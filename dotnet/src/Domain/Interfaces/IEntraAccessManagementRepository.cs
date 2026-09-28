namespace TalentMatch.Domain.Interfaces;

public sealed record AccessManagementActor(
    string TenantId,
    string ObjectId,
    bool GlobalAdmin,
    IReadOnlyList<string> OrganizationAdminIds,
    string CorrelationId);

public sealed record EntraAccessSearch(
    string? Search,
    string? OrganizationId,
    string? Status,
    string? Cursor,
    int Limit);

public sealed record EntraAccessProfile(string Username, string FullName, string? Email);

public sealed record EntraAccessMembership(
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId);

public sealed record EntraDesiredRole(string Role, string? DepartmentId);

public sealed record PutEntraOrganizationAccess(
    int ExpectedVersion,
    EntraAccessProfile Profile,
    EntraAccessMembership Membership,
    IReadOnlyList<EntraDesiredRole> RoleAssignments);

public sealed record UpdateEntraAccessUser(
    int ExpectedVersion,
    EntraAccessProfile? Profile,
    bool? IsActive);

public sealed record EntraAccessRoleAssignment(
    string Id,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);

public sealed record EntraOrganizationAccess(
    string OrganizationId,
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId,
    IReadOnlyList<EntraAccessRoleAssignment> RoleAssignments);

public sealed record EntraAccessAggregate(
    string ObjectId,
    string Username,
    string FullName,
    string? Email,
    bool IsActive,
    int AuthorizationVersion,
    IReadOnlyList<EntraOrganizationAccess> Organizations);

public sealed record EntraAccessPage(
    IReadOnlyList<EntraAccessAggregate> Items,
    string? NextCursor);

public sealed class EntraAccessManagementException : Exception
{
    public EntraAccessManagementException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IEntraAccessManagementRepository
{
    Task<EntraAccessPage> ListAsync(
        AccessManagementActor actor,
        EntraAccessSearch search,
        CancellationToken cancellationToken = default);

    Task<EntraAccessAggregate?> GetAsync(
        AccessManagementActor actor,
        string objectId,
        CancellationToken cancellationToken = default);

    Task<EntraAccessAggregate> UpdateUserAsync(
        AccessManagementActor actor,
        string objectId,
        UpdateEntraAccessUser request,
        CancellationToken cancellationToken = default);

    Task<EntraAccessAggregate> PutOrganizationAccessAsync(
        AccessManagementActor actor,
        string objectId,
        string organizationId,
        PutEntraOrganizationAccess request,
        CancellationToken cancellationToken = default);

    Task<EntraAccessAggregate> RevokeRoleAsync(
        AccessManagementActor actor,
        string objectId,
        string organizationId,
        string assignmentId,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    Task RecordFailureAuditAsync(
        AccessManagementActor actor,
        string objectId,
        string operation,
        string code,
        CancellationToken cancellationToken = default);
}