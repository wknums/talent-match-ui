namespace TalentMatch.Domain.Entities;

/// <summary>Server/operator-established proof; never accepted as caller-supplied SQL booleans.</summary>
public sealed class VerificationReceipt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TransitionId { get; set; } = "";
    public int CandidateVersion { get; set; }
    public string Mode { get; set; } = "";
    public string PrincipalObjectId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string MemberObjectId { get; set; } = "";
    public DateTime CollectionStartedAt { get; set; }
    public DateTime CollectedAt { get; set; }
    public DateTime EvidenceExpiresAt { get; set; }
    public bool Complete { get; set; }
    public bool CompositionValidated { get; set; }
    public bool AccountEnabled { get; set; }
    public string UserType { get; set; } = "";
    public bool IsActive { get; set; }
    public string Stack { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string CapabilityInventoryVersion { get; set; } = "";
    public bool CapabilityInventoryPassed { get; set; }
    public string SqlTargetKey { get; set; } = "";
    public string SqlAdministratorObjectId { get; set; } = "";
    public bool SqlBootstrapPassed { get; set; }
    public DateTime VerifiedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string AuditEventId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
}
