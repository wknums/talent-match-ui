namespace TalentMatch.Domain.Entities;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "recruiter"; // "admin" or "recruiter"
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string AuthenticationProvider { get; set; } = "simple";
    public string? EntraTenantId { get; set; }
    public string? EntraObjectId { get; set; }
    public string? PasswordHash { get; set; } = string.Empty;
    public bool PasswordResetRequired { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLogin { get; set; }

    public ICollection<OrganizationMembership> OrganizationMemberships { get; set; } = new List<OrganizationMembership>();
    public ICollection<DepartmentMembership> DepartmentMemberships { get; set; } = new List<DepartmentMembership>();
    public ICollection<RoleAssignment> RoleAssignments { get; set; } = new List<RoleAssignment>();
}
