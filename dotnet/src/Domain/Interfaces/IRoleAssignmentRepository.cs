namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IRoleAssignmentRepository
{
    Task<IReadOnlyList<RoleAssignment>> GetActiveForIdentityAsync(string tenantId, string objectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoleAssignment>> GetActiveByRoleAsync(string tenantId, string role, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoleGroupMapping>> GetEnabledMappingsAsync(string tenantId, IReadOnlyCollection<string> groupObjectIds, CancellationToken cancellationToken = default);
    Task UpsertGroupMappingAsync(RoleGroupMapping mapping, CancellationToken cancellationToken = default);
    Task<RoleAssignment> ActivateAsync(RoleAssignment assignment, CancellationToken cancellationToken = default);
    Task RevokeAsync(string assignmentId, string updatedBy, CancellationToken cancellationToken = default);
}