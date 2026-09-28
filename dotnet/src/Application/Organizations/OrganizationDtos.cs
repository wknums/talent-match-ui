namespace TalentMatch.Application.Organizations;

public sealed record OrganizationActorDto(
    string TenantId,
    string ObjectId,
    bool GlobalAdmin,
    IReadOnlyCollection<string> OrganizationAdminIds,
    string CorrelationId);

public sealed record DepartmentDto(string Id, string OrganizationId, string Name, string Status);

public sealed record OrganizationDto(
    string Id,
    string Name,
    string Status,
    IReadOnlyCollection<DepartmentDto> Departments);

public sealed record OrganizationMembershipDto(
    string UserObjectId,
    string OrganizationId,
    IReadOnlyCollection<string> DepartmentIds,
    string DefaultDepartmentId);

public sealed record OrganizationRoleAssignmentDto(
    string Id,
    string UserObjectId,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);

public sealed record CreateOrganizationRequestDto(string Name, string InitialDepartmentName);
public sealed record CreateDepartmentRequestDto(string Name);
public sealed record UpdateDepartmentRequestDto(string? Name, string? Status, string? OrganizationId = null);
public sealed record RegisterMembershipRequestDto(
    string UserObjectId,
    IReadOnlyCollection<string> DepartmentIds,
    string DefaultDepartmentId);
public sealed record GrantOrganizationRoleRequestDto(
    string UserObjectId,
    string Role,
    string? DepartmentId);