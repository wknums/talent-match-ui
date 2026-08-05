using System.Text.Json;
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
        await context.ExecuteInTransactionAsync(async token =>
        {
            context.Organizations.Add(organization);
            context.Departments.Add(department);
            await context.SaveChangesAsync(token);
        }, ct);
    }

    public async Task ActivateMembershipsAsync(
        OrganizationMembership organizationMembership,
        IReadOnlyCollection<DepartmentMembership> departmentMemberships,
        string defaultDepartmentId,
        CancellationToken ct = default)
    {
        if (organizationMembership.Status != "active" || organizationMembership.RevokedAt is not null)
            throw new InvalidOperationException("Organization membership must be active.");
        if (departmentMemberships.Count == 0)
            throw new InvalidOperationException("At least one active department membership is required.");
        if (departmentMemberships.Any(membership =>
                membership.UserId != organizationMembership.UserId
                || membership.OrganizationId != organizationMembership.OrganizationId
                || membership.Status != "active"
                || membership.RevokedAt is not null))
            throw new InvalidOperationException("Membership scopes and statuses must agree.");
        if (departmentMemberships.Select(membership => membership.Id).Distinct().Count() != departmentMemberships.Count
            || departmentMemberships.Select(membership => membership.DepartmentId).Distinct().Count() != departmentMemberships.Count)
            throw new InvalidOperationException("Department memberships must be unique.");

        var defaultMembership = departmentMemberships.SingleOrDefault(membership => membership.DepartmentId == defaultDepartmentId);
        if (defaultMembership is null)
            throw new InvalidOperationException("Default department must be part of the active membership transition.");

        var departmentIds = departmentMemberships.Select(membership => membership.DepartmentId).ToArray();
        var validDepartmentCount = await context.Departments.CountAsync(department =>
            departmentIds.Contains(department.Id)
            && department.OrganizationId == organizationMembership.OrganizationId
            && department.Status == "active", ct);
        if (validDepartmentCount != departmentMemberships.Count)
            throw new InvalidOperationException("All departments must belong to the active organization.");

        organizationMembership.DefaultDepartmentMembershipId = defaultMembership.Id;
        await context.ExecuteInTransactionAsync(async token =>
        {
            context.DepartmentMemberships.AddRange(departmentMemberships);
            await context.SaveChangesAsync(token);
            context.OrganizationMemberships.Add(organizationMembership);
            await context.SaveChangesAsync(token);
        }, ct);
    }

    public async Task ReplaceDefaultDepartmentAsync(
        string userId,
        string organizationId,
        string defaultDepartmentId,
        string updatedBy,
        CancellationToken ct = default)
    {
        await context.ExecuteInTransactionAsync(async token =>
        {
            var organizationMembership = await context.OrganizationMemberships.SingleOrDefaultAsync(membership =>
                membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.Status == "active", token)
                ?? throw new InvalidOperationException("Active organization membership was not found.");
            var defaultMembership = await context.DepartmentMemberships.SingleOrDefaultAsync(membership =>
                membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.DepartmentId == defaultDepartmentId
                && membership.Status == "active"
                && membership.Department.Status == "active", token)
                ?? throw new InvalidOperationException("Default department must be an active membership in the organization.");

            organizationMembership.DefaultDepartmentMembershipId = defaultMembership.Id;
            organizationMembership.UpdatedBy = updatedBy;
            await context.SaveChangesAsync(token);
        }, ct);
    }

    public async Task RevokeMembershipsAsync(
        string userId,
        string organizationId,
        string updatedBy,
        CancellationToken ct = default)
    {
        await context.ExecuteInTransactionAsync(async token =>
        {
            var organizationMembership = await context.OrganizationMemberships.SingleOrDefaultAsync(membership =>
                membership.UserId == userId
                && membership.OrganizationId == organizationId
                && membership.Status == "active", token)
                ?? throw new InvalidOperationException("Active organization membership was not found.");
            var departmentMemberships = await context.DepartmentMemberships
                .Where(membership =>
                    membership.UserId == userId
                    && membership.OrganizationId == organizationId
                    && membership.Status == "active")
                .ToListAsync(token);
            var revokedAt = DateTime.UtcNow;

            organizationMembership.Status = "revoked";
            organizationMembership.RevokedAt = revokedAt;
            organizationMembership.UpdatedBy = updatedBy;
            await context.SaveChangesAsync(token);

            foreach (var departmentMembership in departmentMemberships)
            {
                departmentMembership.Status = "revoked";
                departmentMembership.RevokedAt = revokedAt;
                departmentMembership.UpdatedBy = updatedBy;
            }
            await context.SaveChangesAsync(token);
        }, ct);
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

    public async Task<IReadOnlyList<OrganizationAdministrationOrganization>> ListForActorAsync(
        OrganizationAdministrationActor actor,
        CancellationToken ct = default)
    {
        var query = context.Organizations
            .Where(organization => organization.Status == "active")
            .Include(organization => organization.Departments)
            .AsNoTracking();
        if (!actor.GlobalAdmin)
            query = query.Where(organization => actor.OrganizationAdminIds.Contains(organization.Id));

        return (await query.OrderBy(organization => organization.Name).ToListAsync(ct))
            .Select(ToAdministrationOrganization)
            .ToArray();
    }

    public async Task<OrganizationAdministrationOrganization> CreateOrganizationAsync(
        OrganizationAdministrationActor actor,
        string name,
        string initialDepartmentName,
        CancellationToken ct = default)
    {
        try
        {
            return await context.ExecuteInTransactionAsync(async token =>
            {
                if (await context.Organizations.AnyAsync(
                        organization => organization.Status == "active" && organization.Name == name, token))
                    throw Conflict("An active organization with this name already exists.", "auth.organization.changed");

                var organization = new Organization { Name = name, UpdatedBy = actor.ObjectId };
                var department = new Department
                {
                    OrganizationId = organization.Id,
                    Name = initialDepartmentName,
                    UpdatedBy = actor.ObjectId,
                };
                organization.Departments.Add(department);
                context.Organizations.Add(organization);
                AppendAudit(actor, "auth.organization.changed", "organization", organization.Id, "succeeded");
                await context.SaveChangesAsync(token);
                return ToAdministrationOrganization(organization);
            }, ct);
        }
        catch (DbUpdateException)
        {
            throw new OrganizationAdministrationException(
                "conflict", "The organization could not be created because its name or scope already exists.", "auth.organization.changed");
        }
    }

    public async Task<OrganizationAdministrationDepartment> CreateDepartmentAsync(
        OrganizationAdministrationActor actor,
        string organizationId,
        string name,
        CancellationToken ct = default)
    {
        try
        {
            return await context.ExecuteInTransactionAsync(async token =>
            {
                var organizationExists = await context.Organizations.AnyAsync(
                    organization => organization.Id == organizationId && organization.Status == "active", token);
                if (!organizationExists)
                    throw NotFound("The active organization was not found.", "auth.department.changed");
                if (await context.Departments.AnyAsync(department =>
                        department.OrganizationId == organizationId
                        && department.Name == name
                        && department.Status == "active", token))
                    throw Conflict("An active department with this name already exists.", "auth.department.changed");

                var department = new Department
                {
                    OrganizationId = organizationId,
                    Name = name,
                    UpdatedBy = actor.ObjectId,
                };
                context.Departments.Add(department);
                AppendAudit(actor, "auth.department.changed", "department", department.Id, "succeeded");
                await context.SaveChangesAsync(token);
                return ToAdministrationDepartment(department);
            }, ct);
        }
        catch (DbUpdateException)
        {
            throw Conflict("The department change conflicts with the current organization state.", "auth.department.changed");
        }
    }

    public async Task<OrganizationAdministrationDepartment> UpdateDepartmentAsync(
        OrganizationAdministrationActor actor,
        string organizationId,
        string departmentId,
        string? name,
        string? status,
        CancellationToken ct = default)
    {
        try
        {
            return await context.ExecuteInTransactionAsync(async token =>
            {
                var department = await context.Departments.SingleOrDefaultAsync(item =>
                    item.Id == departmentId && item.OrganizationId == organizationId, token)
                    ?? throw NotFound("The department was not found in this organization.", "auth.department.changed");

                if (status == "retired" && department.Status == "active")
                {
                    var activeCount = await context.Departments.CountAsync(item =>
                        item.OrganizationId == organizationId && item.Status == "active", token);
                    var isDefault = await context.OrganizationMemberships.AnyAsync(membership =>
                        membership.Status == "active"
                        && membership.DefaultDepartmentMembership != null
                        && membership.DefaultDepartmentMembership.DepartmentId == departmentId, token);
                    var hasJobs = await context.Jobs.AnyAsync(job =>
                        job.OrganizationId == organizationId && job.DepartmentId == departmentId, token);
                    if (activeCount <= 1 || isDefault || hasJobs)
                    {
                        throw Conflict(
                            "The department cannot be retired while it is the last active department or remains referenced.",
                            "auth.department.changed");
                    }
                }

                if (name is not null)
                    department.Name = name;
                if (status is not null)
                    department.Status = status;
                department.UpdatedAt = DateTime.UtcNow;
                department.UpdatedBy = actor.ObjectId;
                AppendAudit(actor, "auth.department.changed", "department", department.Id, "succeeded");
                await context.SaveChangesAsync(token);
                return ToAdministrationDepartment(department);
            }, ct);
        }
        catch (DbUpdateException)
        {
            throw Conflict("The department change conflicts with the current organization state.", "auth.department.changed");
        }
    }

    public async Task<OrganizationAdministrationMembership> RegisterMembershipAsync(
        OrganizationAdministrationActor actor,
        string organizationId,
        string userObjectId,
        IReadOnlyCollection<string> departmentIds,
        string defaultDepartmentId,
        CancellationToken ct = default)
    {
        try
        {
            return await context.ExecuteInTransactionAsync(async token =>
            {
                var user = await context.Users.SingleOrDefaultAsync(item =>
                    item.AuthenticationProvider == "entra"
                    && item.EntraTenantId == actor.TenantId
                    && item.EntraObjectId == userObjectId, token)
                    ?? throw NotFound("The tenant-verified user was not found.", "auth.membership.activated");
                var departments = await context.Departments.Where(department =>
                    departmentIds.Contains(department.Id)
                    && department.OrganizationId == organizationId
                    && department.Status == "active").ToListAsync(token);
                if (departments.Count != departmentIds.Count
                    || departments.All(department => department.Id != defaultDepartmentId))
                {
                    throw InvalidScope(
                        "All membership departments and the default must be active in the requested organization.",
                        "auth.membership.activated");
                }

                var existingDepartmentMemberships = await context.DepartmentMemberships.Where(membership =>
                    membership.UserId == user.Id && membership.OrganizationId == organizationId).ToListAsync(token);
                var activeMemberships = new List<DepartmentMembership>();
                foreach (var department in departments)
                {
                    var membership = existingDepartmentMemberships.FirstOrDefault(item =>
                        item.DepartmentId == department.Id && item.Status == "active")
                        ?? existingDepartmentMemberships.FirstOrDefault(item => item.DepartmentId == department.Id)
                        ?? new DepartmentMembership
                        {
                            UserId = user.Id,
                            OrganizationId = organizationId,
                            DepartmentId = department.Id,
                        };
                    membership.Status = "active";
                    membership.RevokedAt = null;
                    membership.UpdatedBy = actor.ObjectId;
                    if (context.Entry(membership).State == EntityState.Detached)
                        context.DepartmentMemberships.Add(membership);
                    activeMemberships.Add(membership);
                }
                await context.SaveChangesAsync(token);

                var defaultMembership = activeMemberships.Single(item => item.DepartmentId == defaultDepartmentId);
                var organizationMembership = await context.OrganizationMemberships.FirstOrDefaultAsync(membership =>
                    membership.UserId == user.Id
                    && membership.OrganizationId == organizationId
                    && membership.Status == "active", token)
                    ?? await context.OrganizationMemberships.FirstOrDefaultAsync(membership =>
                        membership.UserId == user.Id && membership.OrganizationId == organizationId, token)
                    ?? new OrganizationMembership { UserId = user.Id, OrganizationId = organizationId };
                organizationMembership.Status = "active";
                organizationMembership.RevokedAt = null;
                organizationMembership.DefaultDepartmentMembershipId = defaultMembership.Id;
                organizationMembership.UpdatedBy = actor.ObjectId;
                if (context.Entry(organizationMembership).State == EntityState.Detached)
                    context.OrganizationMemberships.Add(organizationMembership);
                await context.SaveChangesAsync(token);

                var revokedAt = DateTime.UtcNow;
                foreach (var membership in existingDepartmentMemberships.Where(item =>
                             item.Status == "active" && !departmentIds.Contains(item.DepartmentId)))
                {
                    membership.Status = "revoked";
                    membership.RevokedAt = revokedAt;
                    membership.UpdatedBy = actor.ObjectId;
                }
                AppendAudit(actor, "auth.membership.activated", "user", userObjectId, "succeeded");
                await context.SaveChangesAsync(token);
                return new OrganizationAdministrationMembership(
                    userObjectId, organizationId, departmentIds.ToArray(), defaultDepartmentId);
            }, ct);
        }
        catch (DbUpdateException)
        {
            throw Conflict("The membership change conflicts with the current state.", "auth.membership.activated");
        }
    }

    public async Task<OrganizationAdministrationRoleAssignment> GrantRoleAsync(
        OrganizationAdministrationActor actor,
        string organizationId,
        string userObjectId,
        string role,
        string? departmentId,
        CancellationToken ct = default)
    {
        try
        {
            return await context.ExecuteInTransactionAsync(async token =>
            {
                var user = await context.Users.SingleOrDefaultAsync(item =>
                    item.AuthenticationProvider == "entra"
                    && item.EntraTenantId == actor.TenantId
                    && item.EntraObjectId == userObjectId, token)
                    ?? throw NotFound("The tenant-verified user was not found.", "auth.assignment.activated");
                var hasOrganizationMembership = await context.OrganizationMemberships.AnyAsync(membership =>
                    membership.UserId == user.Id
                    && membership.OrganizationId == organizationId
                    && membership.Status == "active", token);
                var hasDepartmentMembership = departmentId is null || await context.DepartmentMemberships.AnyAsync(membership =>
                    membership.UserId == user.Id
                    && membership.OrganizationId == organizationId
                    && membership.DepartmentId == departmentId
                    && membership.Status == "active"
                    && membership.Department.Status == "active", token);
                if (!hasOrganizationMembership || !hasDepartmentMembership)
                    throw InvalidScope("The delegated role requires matching active memberships.", "auth.assignment.activated");

                var assignment = await context.RoleAssignments.FirstOrDefaultAsync(item =>
                    item.TenantId == actor.TenantId
                    && item.UserObjectId == userObjectId
                    && item.Source == "delegated"
                    && item.Role == role
                    && item.OrganizationId == organizationId
                    && item.DepartmentId == departmentId, token);
                if (assignment is null)
                {
                    assignment = new RoleAssignment
                    {
                        UserId = user.Id,
                        TenantId = actor.TenantId,
                        UserObjectId = userObjectId,
                        Role = role,
                        OrganizationId = organizationId,
                        DepartmentId = departmentId,
                        Source = "delegated",
                        UpdatedBy = actor.ObjectId,
                    };
                    context.RoleAssignments.Add(assignment);
                }
                else
                {
                    assignment.Status = "active";
                    assignment.RevokedAt = null;
                    assignment.UpdatedAt = DateTime.UtcNow;
                    assignment.UpdatedBy = actor.ObjectId;
                }

                AppendAudit(actor, "auth.assignment.activated", "role_assignment", assignment.Id, "succeeded");
                await context.SaveChangesAsync(token);
                return ToAdministrationRoleAssignment(assignment);
            }, ct);
        }
        catch (DbUpdateException)
        {
            throw Conflict("The delegated role conflicts with an existing assignment.", "auth.assignment.activated");
        }
    }

    public async Task RevokeRoleAsync(
        OrganizationAdministrationActor actor,
        string organizationId,
        string assignmentId,
        CancellationToken ct = default)
    {
        await context.ExecuteInTransactionAsync(async token =>
        {
            var assignment = await context.RoleAssignments.SingleOrDefaultAsync(item =>
                item.Id == assignmentId
                && item.OrganizationId == organizationId
                && item.Source == "delegated"
                && item.Status == "active", token)
                ?? throw NotFound("The active delegated assignment was not found in this organization.", "auth.assignment.revoked");
            assignment.Status = "revoked";
            assignment.RevokedAt = DateTime.UtcNow;
            assignment.UpdatedAt = DateTime.UtcNow;
            assignment.UpdatedBy = actor.ObjectId;
            AppendAudit(actor, "auth.assignment.revoked", "role_assignment", assignment.Id, "succeeded");
            await context.SaveChangesAsync(token);
        }, ct);
    }

    public async Task RecordFailureAuditAsync(
        OrganizationAdministrationActor actor,
        string eventType,
        string entityType,
        string entityId,
        string reason,
        CancellationToken ct = default)
    {
        AppendAudit(actor, eventType, entityType, entityId, "failed", reason);
        await context.SaveChangesAsync(ct);
    }

    private void AppendAudit(
        OrganizationAdministrationActor actor,
        string eventType,
        string entityType,
        string entityId,
        string result,
        string? reason = null)
    {
        context.ProcessingEvents.Add(new ProcessingEvent
        {
            Actor = actor.ObjectId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            CorrelationId = actor.CorrelationId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                result,
                reason,
                actorTenantId = actor.TenantId,
                actorObjectId = actor.ObjectId,
            }),
        });
    }

    private static OrganizationAdministrationOrganization ToAdministrationOrganization(Organization organization) =>
        new(
            organization.Id,
            organization.Name,
            organization.Status,
            organization.Departments
                .OrderBy(department => department.Name)
                .Select(ToAdministrationDepartment)
                .ToArray());

    private static OrganizationAdministrationDepartment ToAdministrationDepartment(Department department) =>
        new(department.Id, department.OrganizationId, department.Name, department.Status);

    private static OrganizationAdministrationRoleAssignment ToAdministrationRoleAssignment(RoleAssignment assignment) =>
        new(
            assignment.Id,
            assignment.UserObjectId,
            assignment.Role,
            assignment.OrganizationId!,
            assignment.DepartmentId,
            assignment.Source,
            assignment.Status);

    private static OrganizationAdministrationException InvalidScope(string message, string auditEventType) =>
        new("invalid_scope", message, auditEventType);

    private static OrganizationAdministrationException NotFound(string message, string auditEventType) =>
        new("not_found", message, auditEventType);

    private static OrganizationAdministrationException Conflict(string message, string auditEventType) =>
        new("conflict", message, auditEventType);
}