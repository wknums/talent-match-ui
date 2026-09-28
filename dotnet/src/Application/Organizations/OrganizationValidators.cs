using FluentValidation;

namespace TalentMatch.Application.Organizations;

public sealed class CreateOrganizationCommandValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationCommandValidator()
    {
        RuleFor(command => command.Request.Name).NotEmpty().MaximumLength(200).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.InitialDepartmentName).NotEmpty().MaximumLength(100).WithErrorCode("invalid_scope");
    }
}

public sealed class CreateDepartmentCommandValidator : AbstractValidator<CreateDepartmentCommand>
{
    public CreateDepartmentCommandValidator()
    {
        RuleFor(command => command.OrganizationId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.Name).NotEmpty().MaximumLength(100).WithErrorCode("invalid_scope");
    }
}

public sealed class UpdateDepartmentCommandValidator : AbstractValidator<UpdateDepartmentCommand>
{
    public UpdateDepartmentCommandValidator()
    {
        RuleFor(command => command.OrganizationId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.DepartmentId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request)
            .Must(request => request.Name is not null || request.Status is not null || request.OrganizationId is not null)
            .WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.Name)
            .NotEmpty().MaximumLength(100).When(command => command.Request.Name is not null)
            .WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.Status)
            .Must(status => status is null or "active" or "retired")
            .WithErrorCode("invalid_scope");
    }
}

public sealed class RegisterOrganizationMembershipCommandValidator
    : AbstractValidator<RegisterOrganizationMembershipCommand>
{
    public RegisterOrganizationMembershipCommandValidator()
    {
        RuleFor(command => command.OrganizationId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.UserObjectId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.DepartmentIds).NotEmpty().WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.DefaultDepartmentId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
    }
}

public sealed class GrantOrganizationRoleCommandValidator : AbstractValidator<GrantOrganizationRoleCommand>
{
    public GrantOrganizationRoleCommandValidator()
    {
        RuleFor(command => command.OrganizationId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.UserObjectId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.Role).NotEmpty().MaximumLength(30).WithErrorCode("invalid_scope");
        RuleFor(command => command.Request.DepartmentId)
            .Must(value => value is null || OrganizationValidation.BeUuid(value)).WithErrorCode("invalid_scope");
    }
}

public sealed class RevokeOrganizationRoleCommandValidator : AbstractValidator<RevokeOrganizationRoleCommand>
{
    public RevokeOrganizationRoleCommandValidator()
    {
        RuleFor(command => command.OrganizationId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
        RuleFor(command => command.AssignmentId).Must(OrganizationValidation.BeUuid).WithErrorCode("invalid_scope");
    }
}

internal static class OrganizationValidation
{
    public static bool BeUuid(string value) => Guid.TryParse(value, out _);
}