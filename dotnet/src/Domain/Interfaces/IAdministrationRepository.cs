using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public sealed record AdministrationTarget(string EnvironmentKey, string TenantId, string SqlTargetKey);
public sealed record AdministrationLease(string TransitionId, string LeaseOwner, long FencingToken, int ExpectedAuthorityVersion);
public sealed record AdministrationLeaseRequest(
    string TransitionId, string LeaseOwner, long ExpectedFencingToken, DateTime LeaseExpiresAt);
public sealed record AdministrationSupersession(
    string Kind, AdministrationLease Lease, string ExpectedFingerprint,
    int ExpectedCandidateVersion, long AbandonedFencingToken);
public sealed record AdministrationCheckpointRequest(
    string TransitionId, int ExpectedAuthorityVersion, long FencingToken, string LeaseOwner,
    string ExpectedPhase, string Phase, string ExternalOperations, string CorrelationId);
public sealed record IndividualAdministrationProof(
    string TenantId, string ObjectId, bool? AccountEnabled, string? UserType,
    DateTime CollectionStartedAt, DateTime CollectedAt, DateTime ExpiresAt,
    bool DirectAdminRoleBinding, bool AdminClaim, string BootstrapAssignmentId);
public sealed record AdministrationCommitRequest(
    string TransitionId, int ExpectedAuthorityVersion, long FencingToken, string LeaseOwner,
    IReadOnlyList<string> IncludedStacks, IReadOnlyList<string> ReceiptIds, string CorrelationId,
    IReadOnlyDictionary<string, string>? RequiredRuntimes = null,
    string? CapabilityInventoryVersion = null, IndividualAdministrationProof? IndividualProof = null,
    DirectoryAdministrationEvidence? GroupProof = null);
public sealed record AdministrationReceiptRequest(AdministrationLease Lease, VerificationReceipt Receipt,
    int? ExpectedAuthorizationVersion = null);
public sealed record AdministrationProvenanceRequest(AdministrationLease Lease, ManagedGrantProvenance Grant);

public sealed record DirectoryAdministrationEvidence(
    string TenantId, string GroupObjectId, int AuthorityVersion, string ActorObjectId,
    DateTime CollectionStartedAt, DateTime CollectedAt, DateTime ExpiresAt,
    bool Complete, bool CompositionValidated, bool? AccountEnabled, string? UserType,
    IReadOnlyList<string> DirectMemberObjectIds, IReadOnlyList<string> EligibleUserObjectIds);

public sealed record IndividualDirectoryAdministrationEvidence(
    string TenantId, string ObjectId, int AuthorityVersion,
    DateTime CollectionStartedAt, DateTime CollectedAt, DateTime ExpiresAt,
    bool? AccountEnabled, string? UserType, bool DirectAdminRoleBinding);

public interface IAdministrationRepository
{
    Task<SystemAdministrationConfiguration?> GetCommittedAsync(AdministrationTarget target, CancellationToken cancellationToken = default);
    Task<AdministrationTransition?> GetTransitionAsync(string id, CancellationToken cancellationToken = default);
    Task<AdministrationTransition> StageAsync(AdministrationTransition candidate, CancellationToken cancellationToken = default,
        AdministrationSupersession? supersession = null);
    Task<AdministrationTransition?> TryAcquireLeaseAsync(AdministrationLeaseRequest request, CancellationToken cancellationToken = default);
    Task<bool> TryCheckpointAsync(AdministrationCheckpointRequest request, CancellationToken cancellationToken = default);
    /// <summary>Promote only exact live persisted receipts matching the required runtimes and inventory.</summary>
    Task<bool> TryCommitAsync(AdministrationCommitRequest request, CancellationToken cancellationToken = default);
    Task<bool> RecordReceiptAsync(AdministrationReceiptRequest request, CancellationToken cancellationToken = default);
    Task<bool> RecordProvenanceAsync(AdministrationProvenanceRequest request, CancellationToken cancellationToken = default);
}
