namespace TalentMatch.Domain.Entities;

public class RoleGroupMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;
    public string GroupObjectId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string? DepartmentId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;
}