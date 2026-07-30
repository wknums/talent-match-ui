namespace TalentMatch.Domain.Entities;

public class DepartmentMembership
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string DepartmentId { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
    public Department Department { get; set; } = null!;
}