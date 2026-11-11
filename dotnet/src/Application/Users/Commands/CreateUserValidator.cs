using FluentValidation;

namespace TalentMatch.Application.Users.Commands;

public class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).NotEmpty().Must(r => r == "admin" || r == "recruiter");
        RuleFor(x => x.Department).NotEmpty();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
    }
}
