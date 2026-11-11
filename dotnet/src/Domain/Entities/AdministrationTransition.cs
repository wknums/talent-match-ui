namespace TalentMatch.Domain.Entities;

public sealed class AdministrationTransition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TargetKey { get; set; } = "";
    public string DesiredFingerprint { get; set; } = "";
    public string? PriorConfiguration { get; set; }
    public string CandidateConfiguration { get; set; } = "{}";
    public string Phase { get; set; } = "preflight";
    public int CommittedVersion { get; set; }
    public int CandidateVersion { get; set; }
    public string LeaseOwner { get; set; } = "";
    public DateTime LeaseExpiresAt { get; set; }
    public long FencingToken { get; set; }
    public string ExternalOperations { get; set; } = "[]";
    public string? PriorSqlAdministrator { get; set; }
    public string GrantInventory { get; set; } = "[]";
    public DateTime StartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string ActorId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string? FailureCode { get; set; }
    public string? SupersedesTransitionId { get; set; }
    public string? SupersededByTransitionId { get; set; }
    public DateTime? SupersededAt { get; set; }
}
