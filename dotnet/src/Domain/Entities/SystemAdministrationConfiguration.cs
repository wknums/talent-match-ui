namespace TalentMatch.Domain.Entities;

public sealed class SystemAdministrationConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EnvironmentKey { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SqlTargetKey { get; set; } = "";
    public string Mode { get; set; } = "individual";
    public string? GroupObjectId { get; set; }
    public string? LegacyIndividualObjectId { get; set; }
    public string? SystemRoleGroupMappingId { get; set; }
    public string InitialOrganizationId { get; set; } = "";
    public string InitialDepartmentId { get; set; } = "";
    public int AuthorityVersion { get; set; }
    public string? CommittedTransitionId { get; set; }
    public DateTime CommittedAt { get; set; }
    public string CommittedBy { get; set; } = "";
}
