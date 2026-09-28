namespace TalentMatch.Domain.Entities;

public class Department
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OrganizationId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;

    public Organization Organization { get; set; } = null!;
    public ICollection<DepartmentMembership> Memberships { get; set; } = new List<DepartmentMembership>();
}