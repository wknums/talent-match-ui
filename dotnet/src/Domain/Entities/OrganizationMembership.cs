using System.ComponentModel.DataAnnotations;

namespace TalentMatch.Domain.Entities;

public class OrganizationMembership : IValidatableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string? DefaultDepartmentMembershipId { get; set; }
    public string Status { get; set; } = "active";
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
    public DepartmentMembership? DefaultDepartmentMembership { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Status != "active")
        {
            yield break;
        }

        if (DefaultDepartmentMembershipId is null || DefaultDepartmentMembership is null)
        {
            yield return new ValidationResult(
                "An active organization membership requires an explicit default department membership.",
                [nameof(DefaultDepartmentMembershipId)]);
            yield break;
        }

        if (DefaultDepartmentMembership.Id != DefaultDepartmentMembershipId
            || DefaultDepartmentMembership.UserId != UserId
            || DefaultDepartmentMembership.OrganizationId != OrganizationId
            || DefaultDepartmentMembership.Status != "active"
            || DefaultDepartmentMembership.Department?.Status != "active")
        {
            yield return new ValidationResult(
                "The default department must be an active membership for the same user and organization.",
                [nameof(DefaultDepartmentMembershipId)]);
        }
    }
}