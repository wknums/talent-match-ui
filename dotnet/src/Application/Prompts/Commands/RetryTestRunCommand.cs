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

        // Find applications that need re-scoring (failed or still queued)
        var failedAppIds = new List<string>();
        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app != null && app.Status is "ScoringFailed" or "Queued")
            {
                app.Status = "Queued";
                await _applicationRepo.UpdateAsync(app, ct);
                failedAppIds.Add(appId);
            }
        }

        if (!failedAppIds.Any())
            throw new InvalidOperationException("No failed applications to retry");

        // Reset test run status
        testRun.Status = "scoring";
        await _testRunRepo.UpdateAsync(testRun, ct);

        var testRunId = testRun.Id;
        var jobId = testRun.JobId;
        var promptId = testRun.PromptId;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var testRunRepo = scope.ServiceProvider.GetRequiredService<IPromptTestRunRepository>();
            var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var appRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<RetryTestRunCommandHandler>>();

            try
            {
                var job = await jobRepo.GetByIdAsync(jobId);
                var configVersions = await jobRepo.GetConfigVersionsAsync(jobId);
                var config = configVersions
                    .FirstOrDefault(v => v.Id == job?.CurrentConfigVersionId)
                    ?? configVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                var runCount = config?.ScoringRunCount ?? 3;

                var failedCount = 0;
                foreach (var appId in failedAppIds)
                {
                    try
                    {
                        await mediator.Send(new ScoreApplicationCommand(
                            appId, jobId, runCount, promptId, job?.JobDescription ?? job?.Title ?? "", config?.RubricJson));

                        var app = await appRepo.GetByIdAsync(appId);
                        if (app != null)
                        {
                            app.Status = "Completed";
                            await appRepo.UpdateAsync(app);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        logger.LogError(ex, "Retry test-run {TestRunId}: scoring failed for application {ApplicationId}",
                            testRunId, appId);

                        var failedApp = await appRepo.GetByIdAsync(appId);
                        if (failedApp != null)
                        {
                            failedApp.Status = "ScoringFailed";
                            failedApp.LastError = ex.Message;
                            await appRepo.UpdateAsync(failedApp);
                        }
                    }
                }

                var updatedRun = await testRunRepo.GetByIdAsync(testRunId);
                if (updatedRun != null)
                {
                    updatedRun.Status = failedCount > 0 ? "scoring_failed" : "pending_review";
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
                    await testRunRepo.UpdateAsync(updatedRun);
                }
            }
        }, CancellationToken.None);

        return testRun;
    }
}
