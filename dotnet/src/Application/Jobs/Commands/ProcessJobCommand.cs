using MediatR;
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
    private readonly ISender _mediator;
    private readonly IJobRepository _jobRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly ILogger<ProcessJobCommandHandler> _logger;

    private static readonly string? SeqEndpoint = Environment.GetEnvironmentVariable("AWR_SEQ_API_ENDPOINT");
    private static readonly string? PlatformEndpoint = Environment.GetEnvironmentVariable("AWR_PLATFORM_API_ENDPOINT");

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
        ISender mediator,
        IJobRepository jobRepo,
        IApplicationRepository applicationRepo,
        ILogger<ProcessJobCommandHandler> logger)
    {
        _mediator = mediator;
        _jobRepo = jobRepo;
        _applicationRepo = applicationRepo;
        _logger = logger;
    }

    public async Task<ProcessJobResult> Handle(ProcessJobCommand request, CancellationToken ct)
    {
        var job = await _jobRepo.GetByIdAsync(request.JobId, ct)
            ?? throw new InvalidOperationException($"Job {request.JobId} not found");
        var applications = await _applicationRepo.GetByJobIdAsync(request.JobId, ct);
        var toProcess = applications.Where(a => a.Status == "Queued" || a.Status == "Scored").ToList();
        int processed = 0;
        var errors = new List<string>();
        var scoringMode = ResolveScoringMode();
        _logger.LogInformation("Processing job {JobId}: {Count} applications in {Mode} mode", request.JobId, toProcess.Count, scoringMode);
        foreach (var app in toProcess)
        {
            try
            {
                app.Status = "Scoring";
                await _applicationRepo.UpdateAsync(app, ct);
                var scoreCommand = new ScoreApplicationCommand(app.Id, request.JobId, request.RunCount, request.ProductionPromptId);
                var scoringResult = await _mediator.Send(scoreCommand, ct);
                var scores = scoringResult.Runs.Select(r => r.TotalScore).ToList();
                var avgScore = scores.Any() ? scores.Average() : 0;
                var variance = scores.Any() ? Math.Sqrt(scores.Sum(s => Math.Pow(s - avgScore, 2)) / scores.Count) : 0;
                var config = job.ConfigVersions.FirstOrDefault(v => v.Id == job.CurrentConfigVersionId) ?? job.ConfigVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                var varianceThreshold = config?.VarianceThreshold ?? 15;
                var longlistThreshold = config?.LonglistThreshold ?? 70;
                app.FinalScore = avgScore;
                app.Variance = variance;
                if (variance > varianceThreshold) { app.Status = "NeedsManualReview"; app.FinalDecision = "NeedsManualReview"; }
                else if (avgScore >= longlistThreshold) { app.Status = "Completed"; app.FinalDecision = "Eligible"; }
                else { app.Status = "Completed"; app.FinalDecision = "Excluded"; }
                await _applicationRepo.UpdateAsync(app, ct);
                var aggregatedResult = new AggregatedResult { ApplicationId = app.Id, FinalScore = avgScore, Variance = variance, Confidence = scores.Count >= request.RunCount ? 1.0 : (double)scores.Count / request.RunCount, Decision = app.FinalDecision ?? "Excluded", ConsolidatedRationale = $"Averaged {scores.Count} scoring run(s). Mean score: {avgScore:F1}, Variance: {variance:F1}" };
                await _applicationRepo.SetAggregatedResultAsync(aggregatedResult, ct);
                processed++;
            }
            catch (Exception ex)
            {
                app.Status = "ScoringFailed";
                await _applicationRepo.UpdateAsync(app, ct);
                errors.Add($"Application {app.Id}: {ex.Message}");
                _logger.LogError(ex, "Failed to process application {AppId} in job {JobId}", app.Id, request.JobId);
            }
        }
        return new ProcessJobResult(processed, toProcess.Count, errors);
    }
}