using MediatR;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Organizations;

public sealed class ListOrganizationsQueryHandler(IOrganizationRepository repository)
    : IRequestHandler<ListOrganizationsQuery, IReadOnlyCollection<OrganizationDto>>
{
    public async Task<IReadOnlyCollection<OrganizationDto>> Handle(
        ListOrganizationsQuery request,
        CancellationToken cancellationToken)
    {
        OrganizationPolicies.RequireAdministrator(request.Actor);
        var organizations = await repository.ListForActorAsync(
            OrganizationMappings.ToDomain(request.Actor), cancellationToken);
        return organizations.Select(OrganizationMappings.ToDto).ToArray();
    }
}

public sealed class CreateOrganizationCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<CreateOrganizationCommand, OrganizationDto>
{
    public async Task<OrganizationDto> Handle(CreateOrganizationCommand request, CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireGlobalAdmin(request.Actor);
            var organization = await repository.CreateOrganizationAsync(
                actor,
                request.Request.Name.Trim(),
                request.Request.InitialDepartmentName.Trim(),
                cancellationToken);
            return OrganizationMappings.ToDto(organization);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.organization.changed", "organization", "new", error, cancellationToken);
            throw;
        }
    }
}

public sealed class CreateDepartmentCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<CreateDepartmentCommand, DepartmentDto>
{
    public async Task<DepartmentDto> Handle(CreateDepartmentCommand request, CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireOrganization(request.Actor, request.OrganizationId);
            var department = await repository.CreateDepartmentAsync(
                actor, request.OrganizationId, request.Request.Name.Trim(), cancellationToken);
            return OrganizationMappings.ToDto(department);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.department.changed", "organization", request.OrganizationId, error, cancellationToken);
            throw;
        }
    }
}

public sealed class UpdateDepartmentCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<UpdateDepartmentCommand, DepartmentDto>
{
    public async Task<DepartmentDto> Handle(UpdateDepartmentCommand request, CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireOrganization(request.Actor, request.OrganizationId);
            if (request.Request.OrganizationId is not null
                && !string.Equals(request.Request.OrganizationId, request.OrganizationId, StringComparison.OrdinalIgnoreCase))
            {
                throw OrganizationPolicies.InvalidScope(
                    "A department cannot be moved to another organization.",
                    "auth.department.changed");
            }

            var department = await repository.UpdateDepartmentAsync(
                actor,
                request.OrganizationId,
                request.DepartmentId,
                request.Request.Name?.Trim(),
                request.Request.Status,
                cancellationToken);
            return OrganizationMappings.ToDto(department);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.department.changed", "department", request.DepartmentId, error, cancellationToken);
            throw;
        }
    }
}

public sealed class RegisterOrganizationMembershipCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<RegisterOrganizationMembershipCommand, OrganizationMembershipDto>
{
    public async Task<OrganizationMembershipDto> Handle(
        RegisterOrganizationMembershipCommand request,
        CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireOrganization(request.Actor, request.OrganizationId);
            if (request.Request.DepartmentIds.Count == 0
                || request.Request.DepartmentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Request.DepartmentIds.Count
                || !request.Request.DepartmentIds.Contains(request.Request.DefaultDepartmentId, StringComparer.OrdinalIgnoreCase))
            {
                throw OrganizationPolicies.InvalidScope(
                    "Membership departments must be unique and include the explicit default.",
                    "auth.membership.activated");
            }

            var membership = await repository.RegisterMembershipAsync(
                actor,
                request.OrganizationId,
                request.Request.UserObjectId,
                request.Request.DepartmentIds,
                request.Request.DefaultDepartmentId,
                cancellationToken);
            return OrganizationMappings.ToDto(membership);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.membership.activated", "user", request.Request.UserObjectId, error, cancellationToken);
            throw;
        }
    }
}

public sealed class GrantOrganizationRoleCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<GrantOrganizationRoleCommand, OrganizationRoleAssignmentDto>
{
    public async Task<OrganizationRoleAssignmentDto> Handle(
        GrantOrganizationRoleCommand request,
        CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireOrganization(request.Actor, request.OrganizationId);
            OrganizationPolicies.RequireDelegatedRole(request.Actor, request.Request.Role, request.Request.DepartmentId);
            var assignment = await repository.GrantRoleAsync(
                actor,
                request.OrganizationId,
                request.Request.UserObjectId,
                request.Request.Role,
                request.Request.DepartmentId,
                cancellationToken);
            return OrganizationMappings.ToDto(assignment);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.assignment.activated", "user", request.Request.UserObjectId, error, cancellationToken);
            throw;
        }
    }
}

public sealed class RevokeOrganizationRoleCommandHandler(IOrganizationRepository repository)
    : IRequestHandler<RevokeOrganizationRoleCommand>
{
    public async Task Handle(RevokeOrganizationRoleCommand request, CancellationToken cancellationToken)
    {
        var actor = OrganizationMappings.ToDomain(request.Actor);
        try
        {
            OrganizationPolicies.RequireOrganization(request.Actor, request.OrganizationId);
            await repository.RevokeRoleAsync(
                actor, request.OrganizationId, request.AssignmentId, cancellationToken);
        }
        catch (Exception error)
        {
            await OrganizationPolicies.RecordFailureAsync(
                repository, actor, "auth.assignment.revoked", "role_assignment", request.AssignmentId, error, cancellationToken);
            throw;
        }
    }
}

internal static class OrganizationPolicies
{
    public static void RequireAdministrator(OrganizationActorDto actor)
    {
        if (!actor.GlobalAdmin && actor.OrganizationAdminIds.Count == 0)
            throw Forbidden("Administrative access is required.");
    }

    public static void RequireGlobalAdmin(OrganizationActorDto actor)
    {
        if (!actor.GlobalAdmin)
            throw Forbidden("Application Admin access is required.");
    }

    public static void RequireOrganization(OrganizationActorDto actor, string organizationId)
    {
        if (!actor.GlobalAdmin
            && !actor.OrganizationAdminIds.Contains(organizationId, StringComparer.OrdinalIgnoreCase))
        {
            throw Forbidden("This organization is outside the actor scope.");
        }
    }

    public static void RequireDelegatedRole(OrganizationActorDto actor, string role, string? departmentId)
    {
        if (role == "admin")
            throw InvalidScope("Application Admin cannot be delegated within an organization.", "auth.scope.denied");
        if (role is not ("organization_admin" or "recruiter" or "business_panel"))
            throw InvalidScope("The delegated role is not supported.", "auth.scope.denied");
        if (role == "organization_admin" && departmentId is not null)
            throw InvalidScope("Organization Admin cannot have department scope.", "auth.scope.denied");
        if (role == "recruiter" && departmentId is null)
            throw InvalidScope("Recruiter requires department scope.", "auth.scope.denied");
        if (!actor.GlobalAdmin && role == "organization_admin")
            throw Forbidden("Organization Admin access cannot be delegated by this actor.");
    }

    public static OrganizationAdministrationException InvalidScope(string message, string auditEventType) =>
        new("invalid_scope", message, auditEventType);

    private static OrganizationAdministrationException Forbidden(string message) =>
        new("forbidden", message, "auth.scope.denied");

    public static async Task RecordFailureAsync(
        IOrganizationRepository repository,
        OrganizationAdministrationActor actor,
        string eventType,
        string entityType,
        string entityId,
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            var administrationError = error as OrganizationAdministrationException;
            await repository.RecordFailureAuditAsync(
                actor,
                administrationError?.AuditEventType ?? eventType,
                entityType,
                entityId,
                administrationError?.Code ?? "internal_error",
                cancellationToken);
        }
        catch
        {
        }
    }
}

internal static class OrganizationMappings
{
    public static OrganizationAdministrationActor ToDomain(OrganizationActorDto actor) =>
        new(actor.TenantId, actor.ObjectId, actor.GlobalAdmin, actor.OrganizationAdminIds, actor.CorrelationId);

    public static OrganizationDto ToDto(OrganizationAdministrationOrganization organization) =>
        new(organization.Id, organization.Name, organization.Status, organization.Departments.Select(ToDto).ToArray());

    public static DepartmentDto ToDto(OrganizationAdministrationDepartment department) =>
        new(department.Id, department.OrganizationId, department.Name, department.Status);

    public static OrganizationMembershipDto ToDto(OrganizationAdministrationMembership membership) =>
        new(membership.UserObjectId, membership.OrganizationId, membership.DepartmentIds, membership.DefaultDepartmentId);

    public static OrganizationRoleAssignmentDto ToDto(OrganizationAdministrationRoleAssignment assignment) =>
        new(assignment.Id, assignment.UserObjectId, assignment.Role, assignment.OrganizationId, assignment.DepartmentId, assignment.Source, assignment.Status);
}