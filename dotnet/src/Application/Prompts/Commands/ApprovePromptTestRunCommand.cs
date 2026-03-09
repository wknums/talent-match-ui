using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

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

    public ApprovePromptTestRunCommandHandler(
        IPromptTestRunRepository testRunRepo,
        IApplicationRepository applicationRepo,
        IProcessingEventRepository eventRepo)
    {
        _testRunRepo = testRunRepo;
        _applicationRepo = applicationRepo;
        _eventRepo = eventRepo;
    }

    public async Task<PromptTestRun> Handle(ApprovePromptTestRunCommand request, CancellationToken ct)
    {
        var testRun = await _testRunRepo.GetByIdAsync(request.TestRunId, ct)
            ?? throw new InvalidOperationException("Test run not found");

        // FR-039: Verify all test applications have been reviewed (completed or have results)
        var applicationIds = JsonSerializer.Deserialize<List<string>>(testRun.ApplicationIdsJson) ?? [];

        foreach (var appId in applicationIds)
        {
            var app = await _applicationRepo.GetByIdAsync(appId, ct);
            if (app == null || app.Status == "Queued" || app.Status == "Extracting" || app.Status == "Scoring" || app.Status == "Aggregating")
                throw new InvalidOperationException($"Not all test applications have been reviewed. Application '{appId}' has status '{app?.Status ?? "not found"}'");
        }

        testRun.Status = "approved";
        testRun.CompletedAt = DateTime.UtcNow;
        testRun.ReviewedBy = request.ReviewedBy;
        testRun.ReviewNotes = request.ReviewNotes;

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
}
