namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<Department?> GetDepartmentAsync(string organizationId, string departmentId, CancellationToken cancellationToken = default);
    Task CreateWithDepartmentAsync(Organization organization, Department department, CancellationToken cancellationToken = default);
    Task ActivateMembershipsAsync(OrganizationMembership organizationMembership, DepartmentMembership departmentMembership, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrganizationMembership>> GetActiveMembershipsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DepartmentMembership>> GetActiveDepartmentMembershipsAsync(string userId, CancellationToken cancellationToken = default);
}