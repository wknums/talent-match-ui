using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class OrganizationRepository(AppDbContext context) : IOrganizationRepository
{
    public Task<Organization?> GetByIdAsync(string id, CancellationToken ct = default)
        => context.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Department?> GetDepartmentAsync(string organizationId, string departmentId, CancellationToken ct = default)
        => context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId && d.OrganizationId == organizationId, ct);

    public async Task CreateWithDepartmentAsync(Organization organization, Department department, CancellationToken ct = default)
    {
        if (department.OrganizationId != organization.Id) throw new InvalidOperationException("Department must belong to the organization being created.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        context.Organizations.Add(organization);
        context.Departments.Add(department);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ActivateMembershipsAsync(OrganizationMembership organizationMembership, DepartmentMembership departmentMembership, CancellationToken ct = default)
    {
        if (organizationMembership.UserId != departmentMembership.UserId || organizationMembership.OrganizationId != departmentMembership.OrganizationId)
            throw new InvalidOperationException("Membership scopes must agree.");
        var validDepartment = await context.Departments.AnyAsync(d => d.Id == departmentMembership.DepartmentId && d.OrganizationId == departmentMembership.OrganizationId && d.Status == "active", ct);
        if (!validDepartment) throw new InvalidOperationException("Department does not belong to the active organization.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        context.OrganizationMemberships.Add(organizationMembership);
        context.DepartmentMemberships.Add(departmentMembership);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<OrganizationMembership>> GetActiveMembershipsAsync(string userId, CancellationToken ct = default)
        => await context.OrganizationMemberships
            .Where(m => m.UserId == userId && m.Status == "active" && m.Organization.Status == "active")
            .Include(m => m.Organization)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DepartmentMembership>> GetActiveDepartmentMembershipsAsync(string userId, CancellationToken ct = default)
        => await context.DepartmentMemberships
            .Where(m =>
                m.UserId == userId
                && m.Status == "active"
                && m.Organization.Status == "active"
                && m.Department.Status == "active")
            .Include(m => m.Organization)
            .Include(m => m.Department)
            .AsNoTracking()
            .ToListAsync(ct);
}