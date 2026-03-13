using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
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
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IJobRepository _jobRepo;
    private readonly IMediator _mediator;
    private readonly ILogger<CreatePromptTestRunCommandHandler> _logger;

    public CreatePromptTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IScoringPromptRepository promptRepo,
        IApplicationRepository applicationRepo,
        IJobRepository jobRepo,
        IMediator mediator,
        ILogger<CreatePromptTestRunCommandHandler> logger)
    {
        _testRunRepo = testRunRepo;
        _promptRepo = promptRepo;
        _applicationRepo = applicationRepo;
        _jobRepo = jobRepo;
        _mediator = mediator;
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

        // Create Application records with TestRunId set (marking them as test cases)
        foreach (var file in request.Files)
        {
            var app = new Domain.Entities.Application
            {
                JobId = request.JobId,
                Status = "Queued",
                TestRunId = testRun.Id
            };
            await _applicationRepo.AddAsync(app, ct);

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

            applicationIds.Add(app.Id);
        }

        testRun.ApplicationIdsJson = JsonSerializer.Serialize(applicationIds);
        await _testRunRepo.AddAsync(testRun, ct);

        // Auto-trigger scoring pipeline (FR-048): fire scoring for each test application
        // using the test prompt ID (bypasses production-approved gate per FR-038)
        _ = Task.Run(async () =>
        {
            try
            {
                // Update status to scoring
                testRun.Status = "scoring";
                await _testRunRepo.UpdateAsync(testRun);

                // Load job to get run count from config
                var job = await _jobRepo.GetByIdAsync(request.JobId);
                var configVersions = await _jobRepo.GetConfigVersionsAsync(request.JobId);
                var config = configVersions
                    .FirstOrDefault(v => v.Id == job?.CurrentConfigVersionId)
                    ?? configVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                var runCount = config?.ScoringRunCount ?? 3;

                // Score each test application
                foreach (var appId in applicationIds)
                {
                    try
                    {
                        await _mediator.Send(new ScoreApplicationCommand(
                            appId, request.JobId, runCount, request.PromptId));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Test-run {TestRunId}: scoring failed for application {ApplicationId}",
                            testRun.Id, appId);
                    }
                }

                // Update status to pending_review
                testRun.Status = "pending_review";
                await _testRunRepo.UpdateAsync(testRun);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test-run {TestRunId} auto-trigger scoring failed", testRun.Id);
            }
        }, CancellationToken.None);

        return testRun;
    }
}
