using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Common.Interfaces;

public interface ICurrentUserService
{
    string? UserId { get; }
    string? Username { get; }
    string? Role { get; }
    string? Department { get; }
    bool IsAdmin { get; }
    string? CorrelationId => null;
    Task<CurrentAuthorizationState?> GetAuthorizationStateAsync(CancellationToken cancellationToken = default);
    Task<CurrentAuthorizationState?> GetCandidateAuthorizationStateAsync(
        SystemAdministrationConfiguration candidate, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Candidate evaluation requires an explicit candidate-aware identity reader.");
}

public sealed record CurrentEntraClaims(
    string TenantId,
    string ObjectId,
    string Username,
    string FullName,
    string Email,
    DateTimeOffset TokenIssuedAt,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Groups,
    bool GroupsOverage = false,
    DateTimeOffset? TokenExpiresAt = null);

public sealed record CurrentAuthorizationState(
    CurrentEntraClaims Claims,
    User? User,
    IReadOnlyList<OrganizationMembership> OrganizationMemberships,
    IReadOnlyList<DepartmentMembership> DepartmentMemberships,
    IReadOnlyList<RoleAssignment> Assignments,
    IReadOnlyList<RoleGroupMapping> GroupMappings,
    string ExpectedTenantId,
    string? BootstrapObjectId,
    SystemAdministrationConfiguration? AdministrationConfiguration = null,
    TalentMatch.Domain.Interfaces.DirectoryAdministrationEvidence? AdministrationEvidence = null,
    TalentMatch.Domain.Interfaces.IndividualDirectoryAdministrationEvidence? AdministrationIndividualEvidence = null,
    DirectoryUserEligibilityEvidence? DirectoryActorEvidence = null);
