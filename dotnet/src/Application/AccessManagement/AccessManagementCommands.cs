using MediatR;

namespace TalentMatch.Application.AccessManagement;

public sealed record ListEntraAccessUsersQuery(
    AccessManagementActorDto Actor,
    string? Search,
    string? OrganizationId,
    string? Status,
    string? Cursor,
    int Limit = 25) : IRequest<EntraAccessUserPageDto>;

public sealed record GetEntraAccessUserQuery(
    AccessManagementActorDto Actor,
    string ObjectId) : IRequest<EntraAccessUserDto>;

public sealed record PutEntraOrganizationAccessCommand(
    AccessManagementActorDto Actor,
    string ObjectId,
    string OrganizationId,
    PutEntraOrganizationAccessRequestDto Request) : IRequest<EntraAccessUserDto>;

public sealed record UpdateEntraAccessUserCommand(
    AccessManagementActorDto Actor,
    string ObjectId,
    UpdateEntraAccessUserRequestDto Request) : IRequest<EntraAccessUserDto>;

public sealed record RevokeEntraOrganizationRoleCommand(
    AccessManagementActorDto Actor,
    string ObjectId,
    string OrganizationId,
    string AssignmentId,
    int ExpectedVersion) : IRequest<EntraAccessUserDto>;