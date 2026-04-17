using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record EditPromptCommand(
    string PromptId,
    string PromptText,
    string Author
) : IRequest<ScoringPrompt>;

public class EditPromptCommandHandler : IRequestHandler<EditPromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;

    public EditPromptCommandHandler(IScoringPromptRepository promptRepo)
    {
        _promptRepo = promptRepo;
    }

    public async Task<ScoringPrompt> Handle(EditPromptCommand request, CancellationToken ct)
    {
        var existing = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        // Overwrite the existing prompt text in place
        existing.PromptText = request.PromptText;
        existing.Author = request.Author;
        existing.LastModifiedAt = DateTime.UtcNow;

        await _promptRepo.UpdateAsync(existing, ct);
        return existing;
    }
}
