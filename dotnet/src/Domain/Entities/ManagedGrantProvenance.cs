namespace TalentMatch.Domain.Entities;

public sealed class ManagedGrantProvenance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PrincipalObjectId { get; set; } = "";
    public string? PrincipalSid { get; set; }
    public string Resource { get; set; } = "";
    public string Permission { get; set; } = "";
    public string ManagementSource { get; set; } = "independent";
    public string? OriginatingTransitionId { get; set; }
    public string? AdoptingTransitionId { get; set; }
    public string? RetiringTransitionId { get; set; }
    public string BeforeState { get; set; } = "{}";
    public string AfterState { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
