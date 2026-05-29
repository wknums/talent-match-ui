using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Commands;

public record SaveManualReviewCommand(
    string ApplicationId,
    string RubricScoresJson,
    string OverallComment,
    double? AdjustedFinalScore,
    string AuditTrailJson,
    bool HumanEdited
) : IRequest<ManualReviewData>;

public class SaveManualReviewCommandHandler : IRequestHandler<SaveManualReviewCommand, ManualReviewData>
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly ICurrentUserService _currentUser;

    public SaveManualReviewCommandHandler(IApplicationRepository applicationRepository, ICurrentUserService currentUser)
    {
        _applicationRepository = applicationRepository;
        _currentUser = currentUser;
    }

    public async Task<ManualReviewData> Handle(SaveManualReviewCommand request, CancellationToken cancellationToken)
    {
        var existing = await _applicationRepository.GetManualReviewAsync(request.ApplicationId, cancellationToken);

        var review = existing ?? new ManualReviewData { ApplicationId = request.ApplicationId };
        review.RubricScoresJson = request.RubricScoresJson;
        review.OverallComment = request.OverallComment;
        review.AdjustedFinalScore = request.AdjustedFinalScore;
        review.AuditTrailJson = request.AuditTrailJson;
        review.HumanEdited = review.HumanEdited || request.HumanEdited;
        review.UpdatedAt = DateTime.UtcNow;

        await _applicationRepository.SetManualReviewAsync(review, cancellationToken);
        return review;
    }
}
