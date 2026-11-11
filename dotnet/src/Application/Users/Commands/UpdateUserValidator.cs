using FluentValidation;

namespace TalentMatch.Application.Users.Commands;

public class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.UserId).NotEmpty().WithMessage("User ID is required");
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Full name is required")
            .MaximumLength(200).WithMessage("Full name must not exceed 200 characters");
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Email must be a valid email address");
        RuleFor(x => x.Role).NotEmpty()
            .Must(r => r == "admin" || r == "recruiter" || r == "business_panel")
            .WithMessage("Role must be 'admin', 'recruiter', or 'business_panel'");
        RuleFor(x => x.Department).Custom((department, context) =>
        {
            if (string.IsNullOrEmpty(department)) return;
            var parts = department.Split(',');
            foreach (var part in parts)
            {
                if (part.Trim().Length > 100)
                {
                    context.AddFailure("Department", "Department names must not exceed 100 characters");
                    return;
                }
            }
        });
    }
}
