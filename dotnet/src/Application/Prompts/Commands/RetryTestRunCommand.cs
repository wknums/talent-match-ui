using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record RetryTestRunCommand(string TestRunId) : IRequest<PromptTestRun>;

public class RetryTestRunCommandHandler : IRequestHandler<RetryTestRunCommand, PromptTestRun>
{
    private static readonly int PromptTestRunCount =
        int.TryParse(Environment.GetEnvironmentVariable("PROMPT_TESTRUN_RUN_COUNT"), out var configuredRunCount) && configuredRunCount > 0
            ? configuredRunCount
            : 1;
    private static readonly int PromptTestParallelism =
        int.TryParse(Environment.GetEnvironmentVariable("PROMPT_TESTRUN_MAX_PARALLEL"), out var configuredParallelism) && configuredParallelism > 0
            ? configuredParallelism
            : (int.TryParse(Environment.GetEnvironmentVariable("AWR_MAX_PARALLEL"), out var awrParallelism) && awrParallelism > 0
                ? awrParallelism
                : 4);

    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IJobRepository _jobRepo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetryTestRunCommandHandler> _logger;

    public RetryTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IApplicationRepository applicationRepo,
        IJobRepository jobRepo,
        IServiceScopeFactory scopeFactory,
        ILogger<RetryTestRunCommandHandler> logger)
    {
        _testRunRepo = testRunRepo;
        _applicationRepo = applicationRepo;
        _jobRepo = jobRepo;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PromptTestRun> Handle(RetryTestRunCommand request, CancellationToken ct)
    {
        var testRun = await _testRunRepo.GetByIdAsync(request.TestRunId, ct)
            ?? throw new InvalidOperationException("Test run not found");

        if (testRun.Status is not ("scoring_failed" or "pending_scoring" or "scoring" or "pending_review"))
            throw new InvalidOperationException($"Test run must be in scoring_failed, pending_scoring, scoring, or pending_review status to retry (current: {testRun.Status})");

        var applicationIds = JsonSerializer.Deserialize<List<string>>(testRun.ApplicationIdsJson) ?? new();

        // Find applications that need re-scoring (failed or still queued).
        // If a run is marked scoring_failed but no app is flagged failed, allow a full retry.
        var failedAppIds = new List<string>();
        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app == null)
                continue;

            if (app.Status is "ScoringFailed" or "ExtractionFailed" or "Queued")
            {
                app.Status = "Queued";
                await _applicationRepo.UpdateAsync(app, ct);
                failedAppIds.Add(appId);
            }
        }

        if (!failedAppIds.Any() && string.Equals(testRun.Status, "scoring_failed", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var appId in applicationIds)
            {
                var app = await _applicationRepo.GetByIdAsync(appId, ct);
                if (app == null)
                    continue;

                app.Status = "Queued";
                await _applicationRepo.UpdateAsync(app, ct);
                failedAppIds.Add(appId);
            }
        }

        if (!failedAppIds.Any())
            throw new InvalidOperationException("No failed applications to retry");

        // Reset test run status
        testRun.Status = "scoring";
        testRun.CompletedAt = null;
        await _testRunRepo.UpdateAsync(testRun, ct);

        var testRunId = testRun.Id;
        var jobId = testRun.JobId;
        var promptId = testRun.PromptId;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var testRunRepo = scope.ServiceProvider.GetRequiredService<IPromptTestRunRepository>();
            var runAppRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<RetryTestRunCommandHandler>>();

            try
            {
                var job = await jobRepo.GetByIdAsync(jobId);
                var configVersions = await jobRepo.GetConfigVersionsAsync(jobId);
                var config = configVersions
                    .FirstOrDefault(v => v.Id == job?.CurrentConfigVersionId)
                    ?? configVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                var runCount = PromptTestRunCount;
                logger.LogInformation(
                    "Retry test-run {TestRunId}: scoring {ApplicationCount} application(s) with runCount={RunCount}, parallelism={Parallelism}",
                    testRunId,
                    failedAppIds.Count,
                    runCount,
                    PromptTestParallelism);

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = PromptTestParallelism,
                    CancellationToken = CancellationToken.None
                };

                await Parallel.ForEachAsync(failedAppIds, parallelOptions, async (appId, token) =>
                {
                    using var appScope = _scopeFactory.CreateScope();
                    var appMediator = appScope.ServiceProvider.GetRequiredService<IMediator>();
                    var scopedAppRepo = appScope.ServiceProvider.GetRequiredService<IApplicationRepository>();

                    try
                    {
                        var app = await scopedAppRepo.GetByIdAsync(appId, token);
                        if (app != null)
                        {
                            app.Status = "Scoring";
                            await scopedAppRepo.UpdateAsync(app, token);
                        }

                        await appMediator.Send(new ScoreApplicationCommand(
                            appId, jobId, runCount, promptId, job?.JobDescription ?? job?.Title ?? "", config?.RubricJson), token);

                        app = await scopedAppRepo.GetByIdAsync(appId, token);
                        if (app != null)
                        {
                            app.Status = "Completed";
                            await scopedAppRepo.UpdateAsync(app, token);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Retry test-run {TestRunId}: scoring failed for application {ApplicationId}",
                            testRunId, appId);

                        var failedApp = await scopedAppRepo.GetByIdAsync(appId, token);
                        if (failedApp != null)
                        {
                            failedApp.Status = "ScoringFailed";
                            failedApp.LastError = ex.Message;
                            await scopedAppRepo.UpdateAsync(failedApp, token);
                        }
                    }
                });

                var updatedRun = await testRunRepo.GetByIdAsync(testRunId);
                if (updatedRun != null)
                {
                    var (hasNonTerminal, hasFailed) = await EvaluateRunStateAsync(runAppRepo, applicationIds, CancellationToken.None);
                    updatedRun.Status = hasNonTerminal
                        ? "scoring"
                        : (hasFailed ? "scoring_failed" : "pending_review");
                    updatedRun.CompletedAt = string.Equals(updatedRun.Status, "scoring", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : DateTime.UtcNow;
                    await testRunRepo.UpdateAsync(updatedRun);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retry test-run {TestRunId} scoring failed", testRunId);

                var updatedRun = await testRunRepo.GetByIdAsync(testRunId);
                if (updatedRun != null)
                {
                    updatedRun.Status = "scoring_failed";
                    updatedRun.CompletedAt = DateTime.UtcNow;
                    await testRunRepo.UpdateAsync(updatedRun);
                }
            }
        }, CancellationToken.None);

        return testRun;
    }

    private static async Task<(bool HasNonTerminal, bool HasFailed)> EvaluateRunStateAsync(
        IApplicationRepository applicationRepo,
        IReadOnlyList<string> applicationIds,
        CancellationToken ct)
    {
        var hasFailed = false;

        foreach (var appId in applicationIds)
        {
            var app = await applicationRepo.GetByIdAsync(appId, ct);
            if (app == null)
            {
                hasFailed = true;
                continue;
            }

            if (app.Status is "Queued" or "Extracting" or "Aggregating")
                return (true, hasFailed);

            if (app.Status is "ScoringFailed" or "ExtractionFailed" or "Failed")
            {
                hasFailed = true;
                continue;
            }

            if (app.Status == "Scoring")
            {
                var hasTerminalEvidence = app.FinalScore.HasValue
                    || !string.IsNullOrWhiteSpace(app.FinalDecision);

                if (!hasTerminalEvidence)
                {
                    var appRuns = await applicationRepo.GetScoringRunsAsync(appId, ct);
                    hasTerminalEvidence = appRuns.Count > 0;
                }

                if (!hasTerminalEvidence)
                    return (true, hasFailed);
            }
        }

        return (false, hasFailed);
    }
}
