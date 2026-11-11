using System.Text.Json;
using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record ActivatePromptCommand(
    string PromptId,
    string Actor
) : IRequest<ScoringPrompt>;

public class ActivatePromptCommandHandler : IRequestHandler<ActivatePromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;
    private readonly IProcessingEventRepository _eventRepo;

    public ActivatePromptCommandHandler(
        IScoringPromptRepository promptRepo,
        IProcessingEventRepository eventRepo)
    {
        _promptRepo = promptRepo;
        _eventRepo = eventRepo;
    }

    public async Task<ScoringPrompt> Handle(ActivatePromptCommand request, CancellationToken ct)
    {
        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        // FR-036: Deactivate the currently active prompt for this job
        var currentActive = await _promptRepo.GetActiveForJobAsync(prompt.JobId, ct);
        if (currentActive != null && currentActive.Id != prompt.Id)
        {
            currentActive.Status = "inactive";
            currentActive.LastModifiedAt = DateTime.UtcNow;
            await _promptRepo.UpdateAsync(currentActive, ct);
        }

        prompt.Status = "active";
        prompt.LastModifiedAt = DateTime.UtcNow;
        await _promptRepo.UpdateAsync(prompt, ct);

        // Record audit event
        await _eventRepo.AddAsync(new ProcessingEvent
        {
            Actor = request.Actor,
            EventType = "prompt_activated",
            EntityType = "ScoringPrompt",
            EntityId = prompt.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                prompt.JobId,
                prompt.VersionNumber,
                PreviousActivePromptId = currentActive?.Id
            })
        }, ct);

        return prompt;
    }
}
