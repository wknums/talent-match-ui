using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Common.Interfaces;

public interface ICurrentUserService
{
    string? UserId { get; }
    string? Username { get; }
    string? Role { get; }
    string? Department { get; }
    bool IsAdmin { get; }
    Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default);
}

public sealed record CurrentEntraClaims(
    string TenantId,
    string ObjectId,
    DateTimeOffset TokenIssuedAt,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Groups);

public sealed record CurrentAuthorizationState(
    CurrentEntraClaims Claims,
    User? User,
    IReadOnlyList<OrganizationMembership> OrganizationMemberships,
    IReadOnlyList<DepartmentMembership> DepartmentMemberships,
    IReadOnlyList<RoleAssignment> Assignments,
    IReadOnlyList<RoleGroupMapping> GroupMappings,
    string ExpectedTenantId,
    string? BootstrapObjectId);
