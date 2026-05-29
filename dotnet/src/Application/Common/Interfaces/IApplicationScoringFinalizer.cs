using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Common.Interfaces;

/// <summary>
/// Aggregates a set of ScoringRun results for an application into an
/// AggregatedResult + final decision + application status update. Shared
/// between sequential mode (ProcessJobCommandHandler) and platform mode
/// (PlatformScoringReconciler) so the decision cascade lives in one place.
/// </summary>
public interface IApplicationScoringFinalizer
{
    Task<ApplicationScoringFinalizerResult> FinalizeAsync(
        string applicationId,
        string jobId,
        IReadOnlyList<ScoringRun> runs,
        int runCountTarget,
        double varianceThreshold,
        double longlistThreshold,
        CancellationToken ct = default);
}

public record ApplicationScoringFinalizerResult(
    double FinalScore,
    double Variance,
    string FinalDecision,
    string Status
);
