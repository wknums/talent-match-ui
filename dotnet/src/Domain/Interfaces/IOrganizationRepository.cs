namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public sealed record OrganizationAdministrationActor(
    string TenantId,
    string ObjectId,
    bool GlobalAdmin,
    IReadOnlyCollection<string> OrganizationAdminIds,
    string CorrelationId);

public sealed record OrganizationAdministrationDepartment(
    string Id,
    string OrganizationId,
    string Name,
    string Status);

public sealed record OrganizationAdministrationOrganization(
    string Id,
    string Name,
    string Status,
    IReadOnlyCollection<OrganizationAdministrationDepartment> Departments);

public sealed record OrganizationAdministrationMembership(
    string UserObjectId,
    string OrganizationId,
    IReadOnlyCollection<string> DepartmentIds,
    string DefaultDepartmentId);

public sealed record OrganizationAdministrationRoleAssignment(
    string Id,
    string UserObjectId,
    string Role,
    string OrganizationId,
    string? DepartmentId,
    string Source,
    string Status);

public sealed class OrganizationAdministrationException : Exception
{
    public OrganizationAdministrationException(string code, string message, string auditEventType)
        : base(message)
    {
        Code = code;
        AuditEventType = auditEventType;
    }

    public string Code { get; }
    public string AuditEventType { get; }
}

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<Department?> GetDepartmentAsync(string organizationId, string departmentId, CancellationToken cancellationToken = default);
    Task CreateWithDepartmentAsync(Organization organization, Department department, CancellationToken cancellationToken = default);
    Task ActivateMembershipsAsync(OrganizationMembership organizationMembership, IReadOnlyCollection<DepartmentMembership> departmentMemberships, string defaultDepartmentId, CancellationToken cancellationToken = default);
    Task ReplaceDefaultDepartmentAsync(string userId, string organizationId, string defaultDepartmentId, string updatedBy, CancellationToken cancellationToken = default);
    Task RevokeMembershipsAsync(string userId, string organizationId, string updatedBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationMembership>> GetActiveMembershipsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartmentMembership>> GetActiveDepartmentMembershipsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationAdministrationOrganization>> ListForActorAsync(OrganizationAdministrationActor actor, CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationOrganization> CreateOrganizationAsync(OrganizationAdministrationActor actor, string name, string initialDepartmentName, CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationDepartment> CreateDepartmentAsync(OrganizationAdministrationActor actor, string organizationId, string name, CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationDepartment> UpdateDepartmentAsync(OrganizationAdministrationActor actor, string organizationId, string departmentId, string? name, string? status, CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationMembership> RegisterMembershipAsync(OrganizationAdministrationActor actor, string organizationId, string userObjectId, IReadOnlyCollection<string> departmentIds, string defaultDepartmentId, CancellationToken cancellationToken = default);
    Task<OrganizationAdministrationRoleAssignment> GrantRoleAsync(OrganizationAdministrationActor actor, string organizationId, string userObjectId, string role, string? departmentId, CancellationToken cancellationToken = default);
    Task RevokeRoleAsync(OrganizationAdministrationActor actor, string organizationId, string assignmentId, CancellationToken cancellationToken = default);
    Task RecordFailureAuditAsync(OrganizationAdministrationActor actor, string eventType, string entityType, string entityId, string reason, CancellationToken cancellationToken = default);
}