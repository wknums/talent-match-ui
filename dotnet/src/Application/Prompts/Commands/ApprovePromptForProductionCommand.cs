using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Prompts.Services;

namespace TalentMatch.Application.Prompts.Commands;

public record ApprovePromptForProductionCommand(
    string PromptId,
    string Actor
) : IRequest<ScoringPrompt>;

public class ApprovePromptForProductionCommandHandler : IRequestHandler<ApprovePromptForProductionCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IPromptTestRunRepository _testRunRepo;
    private readonly IProcessingEventRepository _eventRepo;
    private readonly IScoringProfileProvider? _profileProvider;

    public ApprovePromptForProductionCommandHandler(
        IScoringPromptRepository promptRepo,
        IPromptTestRunRepository testRunRepo,
        IProcessingEventRepository eventRepo,
        IScoringProfileProvider? profileProvider = null)
    {
        _promptRepo = promptRepo;
        _testRunRepo = testRunRepo;
        _eventRepo = eventRepo;
        _profileProvider = profileProvider;
    }

    public async Task<ScoringPrompt> Handle(ApprovePromptForProductionCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        var profile = new ScoringProfile(prompt.ModelId, prompt.ReasoningLevel);
        if (string.IsNullOrWhiteSpace(profile.ModelId)
            || string.IsNullOrWhiteSpace(profile.ReasoningLevel))
            throw new ScoringProfileMismatchException(
                $"Prompt v{prompt.VersionNumber} does not have a model and reasoning effort. "
                + "Create a new prompt version and retest it.");

        var testRuns = await _testRunRepo.GetByPromptIdAsync(prompt.Id, ct);
        var approvedRun = testRuns
            .Where(run => run.Status == "approved"
                          && profile.Matches(run.ModelId, run.ReasoningLevel)
                          && profile.Matches(run.ApprovedModelId, run.ApprovedReasoningLevel))
            .OrderByDescending(run => run.CompletedAt)
            .FirstOrDefault();

        if (approvedRun is null)
            throw new ScoringProfileMismatchException(
                $"Cannot approve for production: no approved test run exists for the prompt profile "
                + $"'{profile.ModelId}' / '{profile.ReasoningLevel}'. Retest the prompt.");

        // Demote any existing production-approved prompts for this job to inactive
        var allPrompts = await _promptRepo.GetByJobIdAsync(prompt.JobId, ct);
        foreach (var other in allPrompts.Where(p => p.Status == "production-approved" && p.Id != prompt.Id))
        {
            other.Status = "inactive";
            other.LastModifiedAt = DateTime.UtcNow;
            await _promptRepo.UpdateAsync(other, ct);
        }

        prompt.Status = "production-approved";
        prompt.ApprovedTestRunId = approvedRun.Id;
        prompt.ApprovedModelId = profile.ModelId;
        prompt.ApprovedReasoningLevel = profile.ReasoningLevel;
        prompt.LastModifiedAt = DateTime.UtcNow;
        await _promptRepo.UpdateAsync(prompt, ct);

        await _eventRepo.AddAsync(new ProcessingEvent
        {
            Actor = request.Actor,
            EventType = "prompt_production_approved",
            EntityType = "ScoringPrompt",
            EntityId = prompt.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                prompt.JobId,
                prompt.VersionNumber,
                ApprovedTestRunId = approvedRun.Id,
                profile.ModelId,
                profile.ReasoningLevel
            })
        }, ct);

        return prompt;
    }
}
