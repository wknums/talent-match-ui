using MediatR;

namespace TalentMatch.Application.Organizations;

public sealed record ListOrganizationsQuery(OrganizationActorDto Actor)
    : IRequest<IReadOnlyCollection<OrganizationDto>>;

public sealed record CreateOrganizationCommand(
    OrganizationActorDto Actor,
    CreateOrganizationRequestDto Request) : IRequest<OrganizationDto>;

public sealed record CreateDepartmentCommand(
    OrganizationActorDto Actor,
    string OrganizationId,
    CreateDepartmentRequestDto Request) : IRequest<DepartmentDto>;

public sealed record UpdateDepartmentCommand(
    OrganizationActorDto Actor,
    string OrganizationId,
    string DepartmentId,
    UpdateDepartmentRequestDto Request) : IRequest<DepartmentDto>;

public sealed record RegisterOrganizationMembershipCommand(
    OrganizationActorDto Actor,
    string OrganizationId,
    RegisterMembershipRequestDto Request) : IRequest<OrganizationMembershipDto>;

public sealed record GrantOrganizationRoleCommand(
    OrganizationActorDto Actor,
    string OrganizationId,
    GrantOrganizationRoleRequestDto Request) : IRequest<OrganizationRoleAssignmentDto>;

public sealed record RevokeOrganizationRoleCommand(
    OrganizationActorDto Actor,
    string OrganizationId,
    string AssignmentId) : IRequest;