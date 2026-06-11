using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Scoring.Commands;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record TestRunFile(string FileName, string FileType, long FileSize, string ContentBase64, string Fingerprint);

public record CreatePromptTestRunCommand(
    string JobId,
    string PromptId,
    List<TestRunFile> Files
) : IRequest<PromptTestRun>;

public class CreatePromptTestRunCommandHandler : IRequestHandler<CreatePromptTestRunCommand, PromptTestRun>
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
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IJobRepository _jobRepo;
    private readonly ILlmProxyService _llmService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CreatePromptTestRunCommandHandler> _logger;

    public CreatePromptTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IScoringPromptRepository promptRepo,
        IApplicationRepository applicationRepo,
        IJobRepository jobRepo,
        ILlmProxyService llmService,
        IServiceScopeFactory scopeFactory,
        ILogger<CreatePromptTestRunCommandHandler> logger)
    {
        _testRunRepo = testRunRepo;
        _promptRepo = promptRepo;
        _applicationRepo = applicationRepo;
        _jobRepo = jobRepo;
        _llmService = llmService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PromptTestRun> Handle(CreatePromptTestRunCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        if (prompt.JobId != request.JobId)
            throw new InvalidOperationException("Prompt does not belong to the specified job");

        var testRun = new PromptTestRun
        {
            JobId = request.JobId,
            PromptId = request.PromptId,
            Status = "pending_scoring"
        };

        var applicationIds = new List<string>();
        var createdApplicationIds = new List<string>();

        try
        {
            // Create Application records with TestRunId set (marking them as test cases)
            foreach (var file in request.Files)
            {
                var app = new Domain.Entities.Application
                {
                    JobId = request.JobId,
                    Status = "Queued",
                    TestRunId = testRun.Id
                };
                try
                {
                    await _applicationRepo.AddAsync(app, ct);
                    createdApplicationIds.Add(app.Id);

                    var doc = new ApplicationDocument
                    {
                        ApplicationId = app.Id,
                        FileName = file.FileName,
                        FileType = file.FileType,
                        FileSize = file.FileSize,
                        ContentBase64 = file.ContentBase64,
                        Fingerprint = file.Fingerprint
                    };
                    await _applicationRepo.AddDocumentAsync(doc, ct);
                }
                catch (Exception ex)
                {
                    await SafeDeleteApplicationAsync(app.Id, ct);
                    createdApplicationIds.Remove(app.Id);
                    throw new InvalidOperationException(
                        $"Failed to persist prompt test document '{file.FileName}' for application {app.Id}. Upload was rolled back.",
                        ex);
                }

                // Extract text via AWR passthrough API (sends binary PDF for proper extraction)
                var bytes = Convert.FromBase64String(file.ContentBase64);
                var mimeType = file.FileType;
                if (string.IsNullOrEmpty(mimeType))
                    mimeType = Path.GetExtension(file.FileName).ToLowerInvariant() switch
                    {
                        ".pdf" => "application/pdf",
                        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                        ".txt" or ".md" or ".csv" => "text/plain",
                        _ => "application/octet-stream"
                    };

                string extractedText;
                double confidence;
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext is ".txt" or ".md" or ".csv")
                {
                    // Plain text files don't need API extraction
                    extractedText = Encoding.UTF8.GetString(bytes);
                    confidence = 1.0;
                }
                else
                {
                    try
                    {
                        extractedText = await _llmService.ExtractAsync(bytes, file.FileName, mimeType, ct);
                        confidence = 0.90;
                        _logger.LogInformation("Extracted {Length} chars from {FileName} via AWR API", extractedText.Length, file.FileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AWR extraction failed for {FileName}, using fallback", file.FileName);
                        extractedText = $"[Extraction failed for {file.FileName}: {ex.Message}]";
                        confidence = 0.0;
                    }
                }

                var extraction = new ExtractionArtifact
                {
                    ApplicationId = app.Id,
                    NormalisedText = extractedText,
                    ConfidenceScore = confidence,
                    Status = "completed"
                };
                await _applicationRepo.SetExtractionAsync(extraction, ct);

                applicationIds.Add(app.Id);
            }
        }
        catch
        {
            foreach (var applicationId in createdApplicationIds)
            {
                await SafeDeleteApplicationAsync(applicationId, ct);
            }

            throw;
        }

        testRun.ApplicationIdsJson = JsonSerializer.Serialize(applicationIds);
        await _testRunRepo.AddAsync(testRun, ct);

        // Capture values needed by background task
        var testRunId = testRun.Id;
        var jobId = request.JobId;
        var promptId = request.PromptId;

        // Auto-trigger scoring pipeline (FR-048): fire scoring for each test application
        // using the test prompt ID (bypasses production-approved gate per FR-038)
        // Uses IServiceScopeFactory to create a new DI scope since the HTTP request scope
        // will be disposed before the background work completes.
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var testRunRepo = scope.ServiceProvider.GetRequiredService<IPromptTestRunRepository>();
            var runAppRepo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();
            var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<CreatePromptTestRunCommandHandler>>();

            try
            {
                // Update status to scoring
                var run = await testRunRepo.GetByIdAsync(testRunId);
                if (run == null) return;
                run.Status = "scoring";
                run.CompletedAt = null;
                await testRunRepo.UpdateAsync(run);

                // Load job to get run count from config
                var job = await jobRepo.GetByIdAsync(jobId);
                var configVersions = await jobRepo.GetConfigVersionsAsync(jobId);
                var config = configVersions
                    .FirstOrDefault(v => v.Id == job?.CurrentConfigVersionId)
                    ?? configVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                var runCount = PromptTestRunCount;
                logger.LogInformation(
                    "Test-run {TestRunId}: scoring {ApplicationCount} application(s) with runCount={RunCount}, parallelism={Parallelism}",
                    testRunId,
                    applicationIds.Count,
                    runCount,
                    PromptTestParallelism);

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = PromptTestParallelism,
                    CancellationToken = CancellationToken.None
                };

                await Parallel.ForEachAsync(applicationIds, parallelOptions, async (appId, token) =>
                {
                    using var appScope = _scopeFactory.CreateScope();
                    var appMediator = appScope.ServiceProvider.GetRequiredService<IMediator>();
                    var appRepo = appScope.ServiceProvider.GetRequiredService<IApplicationRepository>();

                    try
                    {
                        var app = await appRepo.GetByIdAsync(appId, token);
                        if (app != null)
                        {
                            app.Status = "Scoring";
                            await appRepo.UpdateAsync(app, token);
                        }

                        await appMediator.Send(new ScoreApplicationCommand(
                            appId, jobId, runCount, promptId, job?.JobDescription ?? job?.Title ?? "", config?.RubricJson), token);

                        // Update application status so approve validation passes
                        app = await appRepo.GetByIdAsync(appId, token);
                        if (app != null)
                        {
                            app.Status = "Completed";
                            await appRepo.UpdateAsync(app, token);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Test-run {TestRunId}: scoring failed for application {ApplicationId}",
                            testRunId, appId);

                        // Mark the application as failed so the UI can show the error
                        var failedApp = await appRepo.GetByIdAsync(appId, token);
                        if (failedApp != null)
                        {
                            failedApp.Status = "ScoringFailed";
                            failedApp.LastError = ex.Message;
                            await appRepo.UpdateAsync(failedApp, token);
                        }
                    }
                });

                // Update test run status based on final persisted application state.
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
                logger.LogError(ex, "Test-run {TestRunId} auto-trigger scoring failed", testRunId);
                try
                {
                    var failedRun = await testRunRepo.GetByIdAsync(testRunId);
                    if (failedRun != null)
                    {
                        failedRun.Status = "scoring_failed";
                        failedRun.CompletedAt = DateTime.UtcNow;
                        await testRunRepo.UpdateAsync(failedRun);
                    }
                }
                catch (Exception statusEx)
                {
                    logger.LogError(statusEx, "Test-run {TestRunId} failed to update status to scoring_failed", testRunId);
                }
            }
        }, CancellationToken.None);

        return testRun;

        async Task SafeDeleteApplicationAsync(string applicationId, CancellationToken cancellationToken)
        {
            try
            {
                await _applicationRepo.DeleteAsync(applicationId, cancellationToken);
            }
            catch
            {
                // Best-effort cleanup: preserve the original exception path.
            }
        }
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
