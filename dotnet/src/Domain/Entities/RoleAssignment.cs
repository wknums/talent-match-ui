using System.ComponentModel.DataAnnotations;

namespace TalentMatch.Domain.Entities;

public class RoleAssignment : IValidatableObject
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Source != "delegated")
        {
            yield break;
        }

        if (Role == "admin" || OrganizationId is null)
        {
            yield return InvalidScope();
            yield break;
        }

        if ((Role == "organization_admin" && DepartmentId is not null)
            || (Role == "recruiter" && DepartmentId is null)
            || Role is not ("organization_admin" or "recruiter" or "business_panel"))
        {
            yield return InvalidScope();
            yield break;
        }

        var hasOrganizationMembership = User?.OrganizationMemberships.Any(membership =>
            membership.OrganizationId == OrganizationId && membership.Status == "active") == true;
        if (!hasOrganizationMembership)
        {
            yield return new ValidationResult(
                "A delegated assignment requires an active organization membership.",
                [nameof(OrganizationId)]);
        }

        if (DepartmentId is not null)
        {
            var hasDepartmentMembership = User?.DepartmentMemberships.Any(membership =>
                membership.OrganizationId == OrganizationId
                && membership.DepartmentId == DepartmentId
                && membership.Status == "active"
                && membership.Department?.OrganizationId == OrganizationId) == true;
            if (!hasDepartmentMembership)
            {
                yield return new ValidationResult(
                    "A delegated department assignment requires a matching active department membership.",
                    [nameof(DepartmentId)]);
            }
        }
    }

    private static ValidationResult InvalidScope() => new(
        "The delegated role scope is invalid.",
        [nameof(Role), nameof(OrganizationId), nameof(DepartmentId)]);
}