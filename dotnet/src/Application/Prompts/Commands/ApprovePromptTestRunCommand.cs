using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Prompts.Services;

namespace TalentMatch.Application.Prompts.Commands;

public record ApprovePromptTestRunCommand(
    string TestRunId,
    string ReviewedBy,
    string? ReviewNotes
) : IRequest<PromptTestRun>;

public class ApprovePromptTestRunCommandHandler : IRequestHandler<ApprovePromptTestRunCommand, PromptTestRun>
{
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IApplicationRepository _applicationRepo;
    private readonly IProcessingEventRepository _eventRepo;
    private readonly IScoringProfileProvider? _profileProvider;

    public ApprovePromptTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IApplicationRepository applicationRepo,
        IProcessingEventRepository eventRepo,
        IScoringProfileProvider? profileProvider = null)
    {
        _testRunRepo = testRunRepo;
        _applicationRepo = applicationRepo;
        _eventRepo = eventRepo;
        _profileProvider = profileProvider;
    }

    public async Task<PromptTestRun> Handle(ApprovePromptTestRunCommand request, CancellationToken ct)
    {
        var testRun = await _testRunRepo.GetByIdAsync(request.TestRunId, ct)
            ?? throw new InvalidOperationException("Test run not found");
        var profile = new ScoringProfile(testRun.ModelId, testRun.ReasoningLevel);
        if (string.IsNullOrWhiteSpace(profile.ModelId)
            || string.IsNullOrWhiteSpace(profile.ReasoningLevel))
            throw new ScoringProfileMismatchException(
                "This test run does not have a model and reasoning effort. Create a new test run.");

        // FR-039: Verify all test applications have been reviewed (completed or have results)
        var applicationIds = JsonSerializer.Deserialize<List<string>>(testRun.ApplicationIdsJson) ?? [];

        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app == null)
                throw new InvalidOperationException($"Not all test applications have been reviewed. Application '{appId}' has status 'not found'");

            if (IsStatus(app.Status, "Queued")
                || IsStatus(app.Status, "Extracting")
                || IsStatus(app.Status, "Aggregating")
                || IsStatus(app.Status, "Scoring"))
            {
                var hasTerminalEvidence = app.FinalScore.HasValue
                    || !string.IsNullOrWhiteSpace(app.FinalDecision);

                if (!hasTerminalEvidence)
                {
                    var runs = await _applicationRepo.GetScoringRunsAsync(appId, ct);
                    hasTerminalEvidence = runs.Count > 0;
                }

                if (!hasTerminalEvidence)
                    throw new InvalidOperationException($"Not all test applications have been reviewed. Application '{appId}' has status '{app.Status}'");

                // Heal stale app status so future approvals do not fail on legacy scoring rows.
                app.Status = "Completed";
                await _applicationRepo.UpdateAsync(app, ct);
            }
        }

        testRun.Status = "approved";
        testRun.CompletedAt = DateTime.UtcNow;
        testRun.ReviewedBy = request.ReviewedBy;
        testRun.ReviewNotes = request.ReviewNotes;
        testRun.ApprovedModelId = profile.ModelId;
        testRun.ApprovedReasoningLevel = profile.ReasoningLevel;

        await _testRunRepo.UpdateAsync(testRun, ct);

        await _eventRepo.AddAsync(new ProcessingEvent
        {
            Actor = request.ReviewedBy,
            EventType = "test_run_approved",
            EntityType = "PromptTestRun",
            EntityId = testRun.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                testRun.PromptId,
                testRun.JobId,
                ApplicationCount = applicationIds.Count
            })
        }, ct);

        return testRun;
    }

    private static bool IsStatus(string? currentStatus, string expectedStatus)
        => string.Equals(currentStatus, expectedStatus, StringComparison.OrdinalIgnoreCase);
}
