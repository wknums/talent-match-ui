using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record ReAggregateJobCommand(string JobId) : IRequest<ReAggregateJobResult>;

public record ReAggregateJobResult(int Updated, int Total);

public class ReAggregateJobCommandHandler : IRequestHandler<ReAggregateJobCommand, ReAggregateJobResult>
{
    private readonly IJobRepository _jobRepo;
    private readonly IApplicationRepository _appRepo;
    private readonly ILogger<ReAggregateJobCommandHandler> _logger;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizationRepository;

    public ReAggregateJobCommandHandler(
        IJobRepository jobRepo,
        IApplicationRepository appRepo,
        ILogger<ReAggregateJobCommandHandler> logger,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizationRepository = null)
    {
        _jobRepo = jobRepo;
        _appRepo = appRepo;
        _logger = logger;
        _currentUser = currentUser;
        _organizationRepository = organizationRepository;
    }

    public async Task<ReAggregateJobResult> Handle(ReAggregateJobCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        await JobAuthorization.EnsureCanMutateAsync(
            job, _currentUser, _organizationRepository, ct);

        var config = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
            ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        var varianceThreshold = config?.VarianceThreshold ?? 15;
        var longlistThreshold = config?.LonglistThreshold ?? 70;

        // Use job.Applications (already loaded via Include) to avoid tracking conflicts
        var scoredApps = job.Applications
            .Where(a => a.Status is "Completed" or "NeedsManualReview" && a.FinalScore.HasValue)
            .ToList();

        int updated = 0;
        foreach (var app in scoredApps)
        {
            var runs = await _appRepo.GetScoringRunsAsync(app.Id, ct);
            if (!runs.Any()) continue;

            var scores = runs.Select(r => r.TotalScore).ToList();
            var avgScore = scores.Average();
            var variance = Math.Sqrt(scores.Sum(s => Math.Pow(s - avgScore, 2)) / scores.Count);

            // Compute per-category averages across all runs
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

            var gatePassVotes = 0;
            var gateFailVotes = 0;
            foreach (var run in runs)
            {
                if (!TryReadGatePassed(run.MustHaveEvaluationJson, out var passed))
                {
                    continue;
                }

                if (passed)
                {
                    gatePassVotes++;
                }
                else
                {
                    gateFailVotes++;
                }
            }

            var gateFailedByAggregation = gateFailVotes > gatePassVotes;
            var hasGateVotes = gatePassVotes + gateFailVotes > 0;

            string newDecision;
            string newStatus;
            if (gateFailedByAggregation)
            {
                newDecision = "Excluded";
                newStatus = "Completed";
            }
            else if (variance > varianceThreshold)
            {
                newDecision = "NeedsManualReview";
                newStatus = "NeedsManualReview";
            }
            else if (avgScore >= longlistThreshold)
            {
                newDecision = "Eligible";
                newStatus = "Completed";
            }
            else
            {
                newDecision = "Excluded";
                newStatus = "Completed";
            }

            var changed = app.FinalDecision != newDecision || app.Status != newStatus;
            if (changed)
            {
                _logger.LogInformation("Re-aggregating {AppId}: {OldDecision} → {NewDecision}",
                    app.Id, app.FinalDecision, newDecision);

                app.FinalDecision = newDecision;
                app.FinalScore = avgScore;
                app.Variance = variance;
                app.Status = newStatus;
                await _appRepo.UpdateAsync(app, ct);

                await _appRepo.SetAggregatedResultAsync(new AggregatedResult
                {
                    ApplicationId = app.Id,
                    FinalScore = avgScore,
                    Variance = variance,
                    Confidence = 1.0,
                    Decision = newDecision,
                    ConsolidatedRationale = gateFailedByAggregation
                        ? $"Excluded: eligibility gate failed by aggregated votes (passed: {gatePassVotes}, failed: {gateFailVotes}). Score: {avgScore:F1} ({scores.Count} run(s), variance: {variance:F1})."
                        : hasGateVotes
                            ? $"Aggregated {scores.Count} scoring run(s). Mean score: {avgScore:F1}, Variance: {variance:F1}. Eligibility votes: passed {gatePassVotes}, failed {gateFailVotes}."
                            : $"Aggregated {scores.Count} scoring run(s). Mean score: {avgScore:F1}, Variance: {variance:F1}",
                    FinalSubScoresJson = finalSubScoresJson,
                }, ct);

                updated++;
            }
        }

        return new ReAggregateJobResult(updated, scoredApps.Count);
    }

    private static bool TryReadGatePassed(string? mustHaveEvaluationJson, out bool passed)
    {
        passed = false;
        if (string.IsNullOrWhiteSpace(mustHaveEvaluationJson) || mustHaveEvaluationJson == "{}")
        {
            return false;
        }

        try
        {
            using var gateDoc = JsonDocument.Parse(mustHaveEvaluationJson);
            if (!gateDoc.RootElement.TryGetProperty("passed", out var gatePassed))
            {
                return false;
            }

            if (gatePassed.ValueKind == JsonValueKind.True)
            {
                passed = true;
                return true;
            }

            if (gatePassed.ValueKind == JsonValueKind.False)
            {
                passed = false;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
