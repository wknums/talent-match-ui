namespace TalentMatch.Domain.Entities;

public class RoleAssignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserObjectId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? DepartmentId { get; set; }
    public string? RoleGroupMappingId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public RoleGroupMapping? RoleGroupMapping { get; set; }
}