using System.Collections.Concurrent;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Commands;

public record ProcessJobCommand(
    string JobId,
    string ProductionPromptId,
    int RunCount
) : IRequest<ProcessJobResult>;

public record ProcessJobResult(
    int Processed,
    int Total,
    List<string> Errors
);

public class ProcessJobCommandHandler : IRequestHandler<ProcessJobCommand, ProcessJobResult>
{
    private readonly IJobRepository _jobRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessJobCommandHandler> _logger;

    private static readonly string? SeqEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
    private static readonly string? PlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");
    private static readonly int MaxParallel = int.TryParse(Environment.GetEnvironmentVariable("AWR_MAX_PARALLEL"), out var p) && p > 0 ? p : 1;

    public static string ResolveScoringMode()
    {
        if (string.IsNullOrEmpty(PlatformEndpoint) || PlatformEndpoint == SeqEndpoint)
            return "sequential";
        return "platform";
    }

    static ProcessJobCommandHandler()
    {
        var mode = ResolveScoringMode();
        Console.WriteLine($"[Pipeline] Scoring mode resolved: {mode} (SEQ={SeqEndpoint ?? "(unset)"}, PLATFORM={PlatformEndpoint ?? "(unset)"})");
    }

    public ProcessJobCommandHandler(
        IJobRepository jobRepo,
        IApplicationRepository applicationRepo,
        IServiceScopeFactory scopeFactory,
        ILogger<ProcessJobCommandHandler> logger)
    {
        _jobRepo = jobRepo;
        _applicationRepo = applicationRepo;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ProcessJobResult> Handle(ProcessJobCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");

        var jobDescriptionText = job.JobDescription ?? job.Title;
        var config = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId)
            ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        var varianceThreshold = config?.VarianceThreshold ?? 15;
        var longlistThreshold = config?.LonglistThreshold ?? 70;

        // Load as no-tracking just to get the list of IDs to process
        var applications = await _applicationRepo.GetByJobIdAsync(request.JobId, ct);
        var toProcessIds = applications
            .Where(a => a.Status is "Queued" or "Scored" or "Scoring" or "ScoringFailed")
            .Select(a => a.Id).ToList();

        int processed = 0;
        var errors = new ConcurrentBag<string>();
        _logger.LogInformation("Processing job {JobId}: {Count} applications in {Mode} mode (parallelism: {MaxParallel})",
            request.JobId, toProcessIds.Count, ResolveScoringMode(), MaxParallel);

        // Process apps in parallel — each in its own DI scope to avoid EF Core tracking conflicts.
        // AWR_MAX_PARALLEL controls concurrency (default 1 = sequential).
        await Parallel.ForEachAsync(toProcessIds,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct },
            async (appId, token) =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();

                // Mark as Scoring
                var app = await appRepo.GetByIdAsync(appId, ct)
                    ?? throw new InvalidOperationException($"Application {appId} not found");
                app.Status = "Scoring";
                await appRepo.UpdateAsync(app, ct);

                // Score via ScoreApplicationCommand (same as test scoring)
                var scoringResult = await mediator.Send(
                    new ScoreApplicationCommand(appId, request.JobId, request.RunCount,
                        request.ProductionPromptId, jobDescriptionText, config?.RubricJson), ct);

                // Compute aggregated scores and set final status
                var scored = await appRepo.GetByIdAsync(appId, ct)
                    ?? throw new InvalidOperationException($"Application {appId} not found after scoring");

                var scores = scoringResult.Runs.Select(r => r.TotalScore).ToList();
                var avgScore = scores.Any() ? scores.Average() : 0;
                var variance = scores.Any()
                    ? Math.Sqrt(scores.Sum(s => Math.Pow(s - avgScore, 2)) / scores.Count) : 0;

                scored.FinalScore = avgScore;
                scored.Variance = variance;

                // Check if any run failed the must-have eligibility gate (like Stack A)
                var anyGateFailed = scoringResult.Runs.Any(r =>
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

                // Decision cascade: gate failure → Excluded (highest priority), then variance, then score
                if (anyGateFailed)
                {
                    scored.Status = "Completed";
                    scored.FinalDecision = "Excluded";
                }
                else if (variance > varianceThreshold)
                {
                    scored.Status = "NeedsManualReview";
                    scored.FinalDecision = "NeedsManualReview";
                }
                else if (avgScore >= longlistThreshold)
                {
                    scored.Status = "Completed";
                    scored.FinalDecision = "Eligible";
                }
                else
                {
                    scored.Status = "Completed";
                    scored.FinalDecision = "Excluded";
                }

                await appRepo.UpdateAsync(scored, ct);

                await appRepo.SetAggregatedResultAsync(new AggregatedResult
                {
                    ApplicationId = appId,
                    FinalScore = avgScore,
                    Variance = variance,
                    Confidence = scores.Count >= request.RunCount
                        ? 1.0 : (double)scores.Count / request.RunCount,
                    Decision = scored.FinalDecision ?? "Excluded",
                    ConsolidatedRationale = anyGateFailed
                        ? $"Excluded: eligibility gate failed. Score: {avgScore:F1} ({scores.Count} run(s), variance: {variance:F1})."
                        : $"Aggregated {scores.Count} scoring run(s). Mean score: {avgScore:F1}, Variance: {variance:F1}"
                }, ct);

                Interlocked.Increment(ref processed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process application {AppId} in job {JobId}",
                    appId, request.JobId);
                errors.Add($"Application {appId}: {ex.Message}");

                try
                {
                    using var errScope = _scopeFactory.CreateScope();
                    var errRepo = errScope.ServiceProvider.GetRequiredService<IApplicationRepository>();
                    var failedApp = await errRepo.GetByIdAsync(appId, ct);
                    if (failedApp != null)
                    {
                        failedApp.Status = "ScoringFailed";
                        failedApp.LastError = ex.Message;
                        await errRepo.UpdateAsync(failedApp, ct);
                    }

                    var dlqRepo = errScope.ServiceProvider.GetRequiredService<IFailureQueueRepository>();
                    await dlqRepo.AddAsync(new FailureQueueItem
                    {
                        EntityType = "Application",
                        EntityId = appId,
                        FailureReason = ex.Message,
                    }, ct);
                }
                catch { /* best effort */ }
            }
        });

        return new ProcessJobResult(processed, toProcessIds.Count, errors.ToList());
    }
}