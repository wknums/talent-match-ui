using System.ComponentModel.DataAnnotations;

namespace TalentMatch.Domain.Entities;

public class DepartmentMembership : IValidatableObject
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
    public ICollection<OrganizationMembership> DefaultForOrganizationMemberships { get; set; } = new List<OrganizationMembership>();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Department is not null && Department.OrganizationId != OrganizationId)
        {
            yield return new ValidationResult(
                "A department membership cannot cross organization boundaries.",
                [nameof(DepartmentId), nameof(OrganizationId)]);
        }

        if (User is not null && User.Id != UserId)
        {
            yield return new ValidationResult(
                "A department membership must belong to its referenced user.",
                [nameof(UserId)]);
        }
    }
}