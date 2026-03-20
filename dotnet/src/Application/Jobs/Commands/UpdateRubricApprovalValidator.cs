using FluentValidation;

namespace TalentMatch.Application.Jobs.Commands;

public class UpdateRubricApprovalValidator : AbstractValidator<UpdateRubricApprovalCommand>
{
    public UpdateRubricApprovalValidator()
    {
        RuleFor(x => x.JobId).NotEmpty();
        RuleFor(x => x.Status).NotEmpty().Must(s => s == "approved").WithMessage("Status must be 'approved'.");
    }
}
