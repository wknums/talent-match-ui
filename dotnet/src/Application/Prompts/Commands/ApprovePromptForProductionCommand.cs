using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

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

    public ApprovePromptForProductionCommandHandler(
        IScoringPromptRepository promptRepo,
        IPromptTestRunRepository testRunRepo,
        IProcessingEventRepository eventRepo)
    {
        _promptRepo = promptRepo;
        _testRunRepo = testRunRepo;
        _eventRepo = eventRepo;
    }

    public async Task<ScoringPrompt> Handle(ApprovePromptForProductionCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        // FR-040: Verify at least one approved test run exists
        var testRuns = await _testRunRepo.GetByPromptIdAsync(prompt.Id, ct);
        var hasApprovedRun = testRuns.Any(tr => tr.Status == "approved");

        if (!hasApprovedRun)
            throw new InvalidOperationException("Cannot approve for production: no approved test run exists for this prompt");

        prompt.Status = "production-approved";
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
                ApprovedTestRunId = testRuns.First(tr => tr.Status == "approved").Id
            })
        }, ct);

        return prompt;
    }
}
