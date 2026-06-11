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
    bool HumanEdited,
    string? FinalDecision
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

        var app = await _applicationRepository.GetByIdAsync(request.ApplicationId, cancellationToken)
            ?? throw new InvalidOperationException($"Application {request.ApplicationId} not found.");

        var normalizedDecision = NormalizeDecision(request.FinalDecision) ?? app.FinalDecision;
        if (!string.IsNullOrWhiteSpace(normalizedDecision))
        {
            app.FinalDecision = normalizedDecision;
            app.Status = string.Equals(normalizedDecision, "NeedsManualReview", StringComparison.Ordinal)
                ? "NeedsManualReview"
                : "Completed";
        }

        if (request.AdjustedFinalScore.HasValue)
        {
            app.FinalScore = request.AdjustedFinalScore.Value;
        }

        app.UpdatedAt = DateTime.UtcNow;
        await _applicationRepository.UpdateAsync(app, cancellationToken);

        var aggregatedResult = await _applicationRepository.GetAggregatedResultAsync(request.ApplicationId, cancellationToken);
        if (aggregatedResult != null)
        {
            if (!string.IsNullOrWhiteSpace(normalizedDecision))
            {
                aggregatedResult.Decision = normalizedDecision;
            }

            if (request.AdjustedFinalScore.HasValue)
            {
                aggregatedResult.FinalScore = request.AdjustedFinalScore.Value;
            }

            await _applicationRepository.SetAggregatedResultAsync(aggregatedResult, cancellationToken);
        }

        await _applicationRepository.SetManualReviewAsync(review, cancellationToken);
        return review;
    }

    private static string? NormalizeDecision(string? decision)
    {
        if (string.IsNullOrWhiteSpace(decision))
        {
            return null;
        }

        return decision switch
        {
            "Eligible" => "Eligible",
            "Excluded" => "Excluded",
            "NeedsManualReview" => "NeedsManualReview",
            _ => null,
        };
    }
}
