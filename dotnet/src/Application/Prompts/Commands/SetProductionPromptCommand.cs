using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record SetProductionPromptCommand(
    string PromptId,
    string Actor
) : IRequest<ScoringPrompt>;

public class SetProductionPromptCommandHandler : IRequestHandler<SetProductionPromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IProcessingEventRepository _eventRepo;

    public SetProductionPromptCommandHandler(
        IScoringPromptRepository promptRepo,
        IProcessingEventRepository eventRepo)
    {
        _promptRepo = promptRepo;
        _eventRepo = eventRepo;
    }

    public async Task<ScoringPrompt> Handle(SetProductionPromptCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        // Only allow re-promoting prompts that were previously production-approved (now inactive)
        if (prompt.Status != "inactive" && prompt.Status != "production-approved")
            throw new InvalidOperationException($"Cannot set as production: prompt status is '{prompt.Status}'. Only inactive or production-approved prompts can be set.");

        // Demote any existing production-approved prompts for this job
        var allPrompts = await _promptRepo.GetByJobIdAsync(prompt.JobId, ct);
        foreach (var other in allPrompts.Where(p => p.Status == "production-approved" && p.Id != prompt.Id))
        {
            other.Status = "inactive";
            other.LastModifiedAt = DateTime.UtcNow;
            await _promptRepo.UpdateAsync(other, ct);
        }

        prompt.Status = "production-approved";
        prompt.LastModifiedAt = DateTime.UtcNow;
        await _promptRepo.UpdateAsync(prompt, ct);

        await _eventRepo.AddAsync(new ProcessingEvent
        {
            Actor = request.Actor,
            EventType = "prompt_set_production",
            EntityType = "ScoringPrompt",
            EntityId = prompt.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                prompt.JobId,
                prompt.VersionNumber
            })
        }, ct);

        return prompt;
    }
}
