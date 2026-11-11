using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Common.Interfaces;

public sealed record DirectoryAdministrationEvidenceRequest(
    string TenantId, string GroupObjectId, int AuthorityVersion, string ActorObjectId,
    int AuthorizationVersion = 0, bool ForceRefresh = false);

public sealed record IndividualDirectoryAdministrationEvidenceRequest(
    string TenantId, string ObjectId, int AuthorityVersion);

public sealed class DirectoryAdministrationEvidenceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed record DirectoryUserEligibilityEvidence(
    string TenantId, string ObjectId, int AuthorityVersion, DateTime CollectionStartedAt,
    DateTime CollectedAt, DateTime ExpiresAt, bool AccountEnabled, string UserType);

public interface IDirectoryAdministrationEvidenceProvider
{
    Task<DirectoryUserEligibilityEvidence> GetUserEvidenceAsync(
        IndividualDirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Current user eligibility must be provided by the selected runtime reader.");
    /// <summary>
    /// Collect complete direct-member and composition-veto evidence for the selected group.
    /// Null eligibility is not affirmative proof. Expiry is bounded by first collection plus
    /// five minutes; never slide it, retain stale proof on errors, or collect in a SQL transaction.
    /// </summary>
    Task<DirectoryAdministrationEvidence> GetGroupEvidenceAsync(
        DirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Verify only the explicitly selected workforce individual and its direct Admin role binding.</summary>
    Task<IndividualDirectoryAdministrationEvidence> GetIndividualEvidenceAsync(
        IndividualDirectoryAdministrationEvidenceRequest request, CancellationToken cancellationToken = default);
}
