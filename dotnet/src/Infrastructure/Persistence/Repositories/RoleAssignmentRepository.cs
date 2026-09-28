using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class RoleAssignmentRepository(AppDbContext context) : IRoleAssignmentRepository
{
    public async Task<IReadOnlyList<RoleAssignment>> GetActiveForIdentityAsync(string tenantId, string objectId, CancellationToken ct = default)
        => await context.RoleAssignments.Where(a => a.TenantId == tenantId && a.UserObjectId == objectId && a.Status == "active").AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetActiveByRoleAsync(string tenantId, string role, CancellationToken ct = default)
        => await context.RoleAssignments
            .Where(assignment =>
                assignment.TenantId == tenantId
                && assignment.Role == role
                && assignment.Status == "active")
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleGroupMapping>> GetEnabledMappingsAsync(string tenantId, IReadOnlyCollection<string> groupObjectIds, CancellationToken ct = default)
        => await context.RoleGroupMappings.Where(m => m.TenantId == tenantId && m.Enabled && groupObjectIds.Contains(m.GroupObjectId)).AsNoTracking().ToListAsync(ct);

    public async Task UpsertGroupMappingAsync(RoleGroupMapping mapping, CancellationToken ct = default)
    {
        var existing = await context.RoleGroupMappings.FirstOrDefaultAsync(m => m.TenantId == mapping.TenantId && m.GroupObjectId == mapping.GroupObjectId, ct);
        if (existing is null) context.RoleGroupMappings.Add(mapping);
        else
        {
            existing.Role = mapping.Role;
            existing.OrganizationId = mapping.OrganizationId;
            existing.DepartmentId = mapping.DepartmentId;
            existing.Enabled = mapping.Enabled;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = mapping.UpdatedBy;
        }
        await context.SaveChangesAsync(ct);
    }

    public async Task<RoleAssignment> ActivateAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        var existing = await FindEquivalentActiveAsync(assignment, ct);
        if (existing is not null) return existing;

        context.RoleAssignments.Add(assignment);
        await context.SaveChangesAsync(ct);
        return assignment;
    }

    public async Task RevokeAsync(string assignmentId, string updatedBy, CancellationToken ct = default)
    {
        var assignment = await context.RoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.Status == "active", ct);
        if (assignment is null) return;
        assignment.Status = "revoked";
        assignment.RevokedAt = DateTime.UtcNow;
        assignment.UpdatedAt = DateTime.UtcNow;
        assignment.UpdatedBy = updatedBy;
        await context.SaveChangesAsync(ct);
    }

    private Task<RoleAssignment?> FindEquivalentActiveAsync(RoleAssignment assignment, CancellationToken ct)
        => context.RoleAssignments.FirstOrDefaultAsync(existing =>
            existing.TenantId == assignment.TenantId
            && existing.UserObjectId == assignment.UserObjectId
            && existing.Source == assignment.Source
            && existing.Role == assignment.Role
            && existing.OrganizationId == assignment.OrganizationId
            && existing.DepartmentId == assignment.DepartmentId
            && existing.RoleGroupMappingId == assignment.RoleGroupMappingId
            && existing.Status == "active", ct);
}