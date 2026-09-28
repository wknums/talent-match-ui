namespace TalentMatch.Application.AccessManagement;

public sealed record AccessManagementActorDto(
    string TenantId,
    string ObjectId,
    bool GlobalAdmin,
    IReadOnlyList<string> OrganizationAdminIds,
    string CorrelationId);

public sealed record EntraAccessProfileDto(
    string Username,
    string FullName,
    string? Email);

public sealed record EntraAccessMembershipDto(
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId);

public sealed record EntraDesiredRoleDto(
    string Role,
    string? DepartmentId);

public sealed record PutEntraOrganizationAccessRequestDto(
    int ExpectedVersion,
    EntraAccessProfileDto Profile,
    EntraAccessMembershipDto Membership,
    IReadOnlyList<EntraDesiredRoleDto> RoleAssignments);

public sealed record UpdateEntraAccessUserRequestDto(
    int ExpectedVersion,
    EntraAccessProfileDto? Profile,
    bool? IsActive);

public sealed record EntraAccessUserPageDto(
    IReadOnlyList<EntraAccessUserDto> Items,
    string? NextCursor);

public sealed record EntraAccessUserDto(
    string ObjectId,
    string Username,
    string FullName,
    string? Email,
    bool IsActive,
    int AuthorizationVersion,
    IReadOnlyList<EntraOrganizationAccessDto> Organizations);

public sealed record EntraOrganizationAccessDto(
    string OrganizationId,
    string Status,
    IReadOnlyList<string> DepartmentIds,
    string? DefaultDepartmentId,
    IReadOnlyList<EntraAccessRoleAssignmentDto> RoleAssignments);

public sealed record EntraAccessRoleAssignmentDto(
    string Id,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);