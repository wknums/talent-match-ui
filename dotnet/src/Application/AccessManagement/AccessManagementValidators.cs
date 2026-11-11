using FluentValidation;
using FluentValidation.Results;

namespace TalentMatch.Application.AccessManagement;

public sealed class ListEntraAccessUsersQueryValidator : AbstractValidator<ListEntraAccessUsersQuery>
{
    public ListEntraAccessUsersQueryValidator()
    {
        RuleFor(request => request.Limit).InclusiveBetween(1, 100).WithErrorCode("invalid_scope");
        RuleFor(request => request.Search).MaximumLength(200).WithErrorCode("invalid_scope");
        RuleFor(request => request.OrganizationId).Must(BeNullOrUuid).WithErrorCode("invalid_scope");
        RuleFor(request => request.Status)
            .Must(status => status is null or "pending" or "active" or "disabled")
            .WithErrorCode("invalid_scope");
    }

    private static bool BeNullOrUuid(string? value) => value is null || Guid.TryParse(value, out _);
}

public sealed class GetEntraAccessUserQueryValidator : AbstractValidator<GetEntraAccessUserQuery>
{
    public GetEntraAccessUserQueryValidator() =>
        RuleFor(request => request.ObjectId).Must(BeUuid).WithErrorCode("invalid_scope");

    private static bool BeUuid(string value) => Guid.TryParse(value, out _);
}

public sealed class PutEntraOrganizationAccessCommandValidator : AbstractValidator<PutEntraOrganizationAccessCommand>
{
    private static readonly string[] Roles = ["organization_admin", "recruiter", "business_panel"];

    public PutEntraOrganizationAccessCommandValidator()
    {
        RuleFor(request => request.ObjectId).Must(BeUuid).WithErrorCode("invalid_scope");
        RuleFor(request => request.OrganizationId).Must(BeUuid).WithErrorCode("invalid_scope");
        RuleFor(request => request.Request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("invalid_scope");
        RuleFor(request => request.Request.Profile).SetValidator(new EntraAccessProfileValidator());
        RuleFor(request => request.Request).Custom(ValidateDesiredState);
    }

    private static void ValidateDesiredState(
        PutEntraOrganizationAccessRequestDto request,
        ValidationContext<PutEntraOrganizationAccessCommand> context)
    {
        var membership = request.Membership;
        var uniqueDepartments = membership.DepartmentIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (uniqueDepartments != membership.DepartmentIds.Count
            || membership.DepartmentIds.Any(departmentId => !Guid.TryParse(departmentId, out _)))
        {
            context.AddFailure(Failure("Request.Membership.DepartmentIds", "Department IDs must be unique UUIDs."));
        }

        if (membership.Status == "active")
        {
            if (membership.DepartmentIds.Count == 0
                || membership.DefaultDepartmentId is null
                || !membership.DepartmentIds.Contains(membership.DefaultDepartmentId, StringComparer.OrdinalIgnoreCase))
            {
                context.AddFailure(Failure("Request.Membership.DefaultDepartmentId", "The default must be an active department membership."));
            }
        }
        else if (membership.Status == "revoked")
        {
            if (membership.DepartmentIds.Count > 0 || membership.DefaultDepartmentId is not null || request.RoleAssignments.Count > 0)
                context.AddFailure(Failure("Request.Membership", "Revoked access cannot retain departments, a default, or delegated roles."));
        }
        else
        {
            context.AddFailure(Failure("Request.Membership.Status", "Membership status must be active or revoked."));
        }

        var roleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in request.RoleAssignments)
        {
            if (!Roles.Contains(assignment.Role, StringComparer.Ordinal))
                context.AddFailure(Failure("Request.RoleAssignments", "The delegated role is not supported."));
            if (assignment.Role == "recruiter" && assignment.DepartmentId is null)
                context.AddFailure(Failure("Request.RoleAssignments", "Recruiter access requires a department."));
            if (assignment.Role == "organization_admin" && assignment.DepartmentId is not null)
                context.AddFailure(Failure("Request.RoleAssignments", "Organization Admin cannot have a department."));
            if (assignment.DepartmentId is not null
                && !membership.DepartmentIds.Contains(assignment.DepartmentId, StringComparer.OrdinalIgnoreCase))
            {
                context.AddFailure(Failure("Request.RoleAssignments", "Role departments must be active memberships."));
            }
            if (!roleKeys.Add($"{assignment.Role}:{assignment.DepartmentId}"))
                context.AddFailure(Failure("Request.RoleAssignments", "Delegated roles must be unique."));
        }
    }

    private static ValidationFailure Failure(string propertyName, string message) =>
        new(propertyName, message) { ErrorCode = "invalid_scope" };

    private static bool BeUuid(string value) => Guid.TryParse(value, out _);
}

public sealed class UpdateEntraAccessUserCommandValidator : AbstractValidator<UpdateEntraAccessUserCommand>
{
    public UpdateEntraAccessUserCommandValidator()
    {
        RuleFor(request => request.ObjectId).Must(value => Guid.TryParse(value, out _)).WithErrorCode("invalid_scope");
        RuleFor(request => request.Request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("invalid_scope");
        RuleFor(request => request.Request)
            .Must(request => request.Profile is not null || request.IsActive.HasValue)
            .WithErrorCode("invalid_scope");
        When(request => request.Request.Profile is not null, () =>
            RuleFor(request => request.Request.Profile!).SetValidator(new EntraAccessProfileValidator()));
    }
}

public sealed class RevokeEntraOrganizationRoleCommandValidator : AbstractValidator<RevokeEntraOrganizationRoleCommand>
{
    public RevokeEntraOrganizationRoleCommandValidator()
    {
        RuleFor(request => request.ObjectId).Must(value => Guid.TryParse(value, out _)).WithErrorCode("invalid_scope");
        RuleFor(request => request.OrganizationId).Must(value => Guid.TryParse(value, out _)).WithErrorCode("invalid_scope");
        RuleFor(request => request.AssignmentId).Must(value => Guid.TryParse(value, out _)).WithErrorCode("invalid_scope");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("invalid_scope");
    }
}

internal sealed class EntraAccessProfileValidator : AbstractValidator<EntraAccessProfileDto>
{
    public EntraAccessProfileValidator()
    {
        RuleFor(profile => profile.Username).NotEmpty().MaximumLength(100).WithErrorCode("invalid_scope");
        RuleFor(profile => profile.FullName).NotEmpty().MaximumLength(200).WithErrorCode("invalid_scope");
        RuleFor(profile => profile.Email)
            .MaximumLength(320).EmailAddress().When(profile => !string.IsNullOrWhiteSpace(profile.Email))
            .WithErrorCode("invalid_scope");
    }
}