using FluentValidation;

namespace TalentMatch.Application.Jobs.Commands;

public class CreateJobValidator : AbstractValidator<CreateJobCommand>
{
    public CreateJobValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Department).NotEmpty();
        RuleFor(x => x.Organisation).NotEmpty();
    }
}
