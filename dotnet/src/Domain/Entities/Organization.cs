using System.ComponentModel.DataAnnotations;

namespace TalentMatch.Domain.Entities;

public class Organization : IValidatableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;

    public ICollection<Department> Departments { get; set; } = new List<Department>();
    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status == "active" && !Departments.Any(department => department.Status == "active"))
        {
            yield return new ValidationResult(
                "An active organization must contain at least one active department.",
                [nameof(Departments)]);
        }
    }
}