using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class RoleAssignmentRepository(AppDbContext context) : IRoleAssignmentRepository
{
    public async Task<IReadOnlyList<RoleAssignment>> GetActiveForIdentityAsync(string tenantId, string objectId, CancellationToken ct = default)
        => await context.RoleAssignments.Where(a => a.TenantId == tenantId && a.UserObjectId == objectId && a.Status == "active").AsNoTracking().ToListAsync(ct);

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

    public async Task ActivateAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        context.RoleAssignments.Add(assignment);
        await context.SaveChangesAsync(ct);
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
}