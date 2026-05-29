using System.Text.Json;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Common.Services;

/// <summary>
/// Default finalizer — mirrors the post-scoring aggregation block that used
/// to live inline in ProcessJobCommandHandler.Handle. Persists Application
/// status/decision + AggregatedResult.
/// </summary>
public class ApplicationScoringFinalizer : IApplicationScoringFinalizer
{
    private readonly IApplicationRepository _appRepo;

    public ApplicationScoringFinalizer(IApplicationRepository appRepo) => _appRepo = appRepo;

    public async Task<ApplicationScoringFinalizerResult> FinalizeAsync(
        string applicationId,
        string jobId,
        IReadOnlyList<ScoringRun> runs,
        int runCountTarget,
        double varianceThreshold,
        double longlistThreshold,
        CancellationToken ct = default)
    {
        var app = await _appRepo.GetByIdAsync(applicationId, ct)
            ?? throw new InvalidOperationException($"Application {applicationId} not found");

        var scores = runs.Select(r => r.TotalScore).ToList();
        var avgScore = scores.Any() ? scores.Average() : 0;
        var variance = scores.Any()
            ? Math.Sqrt(scores.Sum(s => Math.Pow(s - avgScore, 2)) / scores.Count) : 0;

        var categoryTotals = new Dictionary<string, List<double>>();
        foreach (var run in runs)
        {
            try
            {
                var cats = JsonSerializer.Deserialize<Dictionary<string, double>>(run.CategoryScoresJson);
                if (cats != null)
                    foreach (var kv in cats)
                    {
                        if (!categoryTotals.ContainsKey(kv.Key)) categoryTotals[kv.Key] = new List<double>();
                        categoryTotals[kv.Key].Add(kv.Value);
                    }
            }
            catch { /* ignore malformed JSON */ }
        }
        var finalSubScores = categoryTotals.ToDictionary(kv => kv.Key, kv => kv.Value.Average());
        var finalSubScoresJson = JsonSerializer.Serialize(finalSubScores);

        app.FinalScore = avgScore;
        app.Variance = variance;

        var anyGateFailed = runs.Any(r =>
        {
            if (string.IsNullOrWhiteSpace(r.MustHaveEvaluationJson) || r.MustHaveEvaluationJson == "{}")
                return false;
            try
            {
                using var gateDoc = JsonDocument.Parse(r.MustHaveEvaluationJson);
                return gateDoc.RootElement.TryGetProperty("passed", out var p)
                    && p.ValueKind == JsonValueKind.False;
            }
            catch { return false; }
        });

        string status, decision;
        if (anyGateFailed)        { status = "Completed";          decision = "Excluded"; }
        else if (variance > varianceThreshold) { status = "NeedsManualReview"; decision = "NeedsManualReview"; }
        else if (avgScore >= longlistThreshold){ status = "Completed";          decision = "Eligible"; }
        else                       { status = "Completed";          decision = "Excluded"; }

        app.Status = status;
        app.FinalDecision = decision;
        await _appRepo.UpdateAsync(app, ct);

        await _appRepo.SetAggregatedResultAsync(new AggregatedResult
        {
            ApplicationId = applicationId,
            FinalScore = avgScore,
            Variance = variance,
            Confidence = scores.Count >= runCountTarget ? 1.0 : (double)scores.Count / Math.Max(1, runCountTarget),
            Decision = decision,
            ConsolidatedRationale = anyGateFailed
                ? $"Excluded: eligibility gate failed. Score: {avgScore:F1} ({scores.Count} run(s), variance: {variance:F1})."
                : $"Aggregated {scores.Count} scoring run(s). Mean score: {avgScore:F1}, Variance: {variance:F1}",
            FinalSubScoresJson = finalSubScoresJson,
        }, ct);

        return new ApplicationScoringFinalizerResult(avgScore, variance, decision, status);
    }
}
