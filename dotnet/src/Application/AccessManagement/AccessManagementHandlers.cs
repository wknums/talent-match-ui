using MediatR;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.AccessManagement;

public sealed class ListEntraAccessUsersQueryHandler
    : IRequestHandler<ListEntraAccessUsersQuery, EntraAccessUserPageDto>
{
    private readonly IEntraAccessManagementRepository _repository;

    public ListEntraAccessUsersQueryHandler(IEntraAccessManagementRepository repository) =>
        _repository = repository;

    public async Task<EntraAccessUserPageDto> Handle(ListEntraAccessUsersQuery request, CancellationToken cancellationToken)
    {
        AccessManagementHandlers.RequireAdministrator(request.Actor);
        if (request.OrganizationId is not null)
            AccessManagementHandlers.RequireOrganization(request.Actor, request.OrganizationId);

        var page = await _repository.ListAsync(
            AccessManagementMappings.ToDomain(request.Actor),
            new EntraAccessSearch(request.Search, request.OrganizationId, request.Status, request.Cursor, request.Limit),
            cancellationToken);
        return AccessManagementMappings.ToDto(page);
    }
}

public sealed class GetEntraAccessUserQueryHandler
    : IRequestHandler<GetEntraAccessUserQuery, EntraAccessUserDto>
{
    private readonly IEntraAccessManagementRepository _repository;

    public GetEntraAccessUserQueryHandler(IEntraAccessManagementRepository repository) =>
        _repository = repository;

    public async Task<EntraAccessUserDto> Handle(GetEntraAccessUserQuery request, CancellationToken cancellationToken)
    {
        AccessManagementHandlers.RequireAdministrator(request.Actor);
        var aggregate = await _repository.GetAsync(
            AccessManagementMappings.ToDomain(request.Actor), request.ObjectId, cancellationToken);
        if (aggregate is null)
            throw new EntraAccessManagementException("not_found", "The Entra access profile was not found.");
        return AccessManagementMappings.ToDto(aggregate);
    }
}

public sealed class PutEntraOrganizationAccessCommandHandler
    : IRequestHandler<PutEntraOrganizationAccessCommand, EntraAccessUserDto>
{
    private readonly IEntraAccessManagementRepository _repository;

    public PutEntraOrganizationAccessCommandHandler(IEntraAccessManagementRepository repository) =>
        _repository = repository;

    public async Task<EntraAccessUserDto> Handle(PutEntraOrganizationAccessCommand request, CancellationToken cancellationToken)
    {
        var actor = AccessManagementMappings.ToDomain(request.Actor);
        try
        {
            AccessManagementHandlers.RequireOrganization(request.Actor, request.OrganizationId);
            if (!request.Actor.GlobalAdmin
                && request.Request.RoleAssignments.Any(assignment => assignment.Role == "organization_admin"))
            {
                throw new EntraAccessManagementException(
                    "forbidden", "Organization Admin access cannot be delegated by this actor.");
            }

            var result = await _repository.PutOrganizationAccessAsync(
                actor,
                request.ObjectId,
                request.OrganizationId,
                AccessManagementMappings.ToDomain(request.Request),
                cancellationToken);
            return AccessManagementMappings.ToDto(result);
        }
        catch (Exception error)
        {
            await AccessManagementHandlers.RecordFailureAsync(
                _repository, actor, request.ObjectId, "put_organization_access", error, cancellationToken);
            throw;
        }
    }
}

public sealed class UpdateEntraAccessUserCommandHandler
    : IRequestHandler<UpdateEntraAccessUserCommand, EntraAccessUserDto>
{
    private readonly IEntraAccessManagementRepository _repository;

    public UpdateEntraAccessUserCommandHandler(IEntraAccessManagementRepository repository) =>
        _repository = repository;

    public async Task<EntraAccessUserDto> Handle(UpdateEntraAccessUserCommand request, CancellationToken cancellationToken)
    {
        var actor = AccessManagementMappings.ToDomain(request.Actor);
        try
        {
            if (!request.Actor.GlobalAdmin)
                throw new EntraAccessManagementException("forbidden", "Application Admin access is required.");
            var result = await _repository.UpdateUserAsync(
                actor,
                request.ObjectId,
                new UpdateEntraAccessUser(
                    request.Request.ExpectedVersion,
                    request.Request.Profile is null
                        ? null
                        : new EntraAccessProfile(
                            request.Request.Profile.Username,
                            request.Request.Profile.FullName,
                            request.Request.Profile.Email),
                    request.Request.IsActive),
                cancellationToken);
            return AccessManagementMappings.ToDto(result);
        }
        catch (Exception error)
        {
            await AccessManagementHandlers.RecordFailureAsync(
                _repository, actor, request.ObjectId, "update_user", error, cancellationToken);
            throw;
        }
    }
}

public sealed class RevokeEntraOrganizationRoleCommandHandler
    : IRequestHandler<RevokeEntraOrganizationRoleCommand, EntraAccessUserDto>
{
    private readonly IEntraAccessManagementRepository _repository;

    public RevokeEntraOrganizationRoleCommandHandler(IEntraAccessManagementRepository repository) =>
        _repository = repository;

    public async Task<EntraAccessUserDto> Handle(RevokeEntraOrganizationRoleCommand request, CancellationToken cancellationToken)
    {
        var actor = AccessManagementMappings.ToDomain(request.Actor);
        try
        {
            AccessManagementHandlers.RequireOrganization(request.Actor, request.OrganizationId);
            var result = await _repository.RevokeRoleAsync(
                actor,
                request.ObjectId,
                request.OrganizationId,
                request.AssignmentId,
                request.ExpectedVersion,
                cancellationToken);
            return AccessManagementMappings.ToDto(result);
        }
        catch (Exception error)
        {
            await AccessManagementHandlers.RecordFailureAsync(
                _repository, actor, request.ObjectId, "revoke_role", error, cancellationToken);
            throw;
        }
    }
}

internal static class AccessManagementHandlers
{
    public static void RequireAdministrator(AccessManagementActorDto actor)
    {
        if (!actor.GlobalAdmin && actor.OrganizationAdminIds.Count == 0)
            throw new EntraAccessManagementException("forbidden", "Administrative access is required.");
    }

    public static void RequireOrganization(AccessManagementActorDto actor, string organizationId)
    {
        if (!actor.GlobalAdmin
            && !actor.OrganizationAdminIds.Contains(organizationId, StringComparer.OrdinalIgnoreCase))
        {
            throw new EntraAccessManagementException("forbidden", "This organization is outside the actor scope.");
        }
    }

    public static async Task RecordFailureAsync(
        IEntraAccessManagementRepository repository,
        AccessManagementActor actor,
        string objectId,
        string operation,
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.RecordFailureAuditAsync(
                actor,
                objectId,
                operation,
                error is EntraAccessManagementException accessError ? accessError.Code : "internal_error",
                cancellationToken);
        }
        catch
        {
            // The operation error remains authoritative if failure auditing is unavailable.
        }
    }
}

internal static class AccessManagementMappings
{
    public static AccessManagementActor ToDomain(AccessManagementActorDto actor) =>
        new(actor.TenantId, actor.ObjectId, actor.GlobalAdmin, actor.OrganizationAdminIds, actor.CorrelationId);

    public static PutEntraOrganizationAccess ToDomain(PutEntraOrganizationAccessRequestDto request) =>
        new(
            request.ExpectedVersion,
            new EntraAccessProfile(request.Profile.Username, request.Profile.FullName, request.Profile.Email),
            new EntraAccessMembership(
                request.Membership.Status,
                request.Membership.DepartmentIds,
                request.Membership.DefaultDepartmentId),
            request.RoleAssignments
                .Select(assignment => new EntraDesiredRole(assignment.Role, assignment.DepartmentId))
                .ToArray());

    public static EntraAccessUserPageDto ToDto(EntraAccessPage page) =>
        new(page.Items.Select(ToDto).ToArray(), page.NextCursor);

    public static EntraAccessUserDto ToDto(EntraAccessAggregate aggregate) =>
        new(
            aggregate.ObjectId,
            aggregate.Username,
            aggregate.FullName,
            aggregate.Email,
            aggregate.IsActive,
            aggregate.AuthorizationVersion,
            aggregate.Organizations.Select(organization => new EntraOrganizationAccessDto(
                organization.OrganizationId,
                organization.Status,
                organization.DepartmentIds,
                organization.DefaultDepartmentId,
                organization.RoleAssignments.Select(assignment => new EntraAccessRoleAssignmentDto(
                    assignment.Id,
                    assignment.Role,
                    assignment.OrganizationId,
                    assignment.DepartmentId,
                    assignment.Source,
                    assignment.Status)).ToArray())).ToArray());
}