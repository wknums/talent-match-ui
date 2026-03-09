using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Commands;

public record RatePromptCommand(
    string PromptId,
    int Rating,
    string? Comments
) : IRequest<ScoringPrompt>;

public class RatePromptCommandHandler : IRequestHandler<RatePromptCommand, ScoringPrompt>
{
    private readonly IScoringPromptRepository _promptRepo;

    public RatePromptCommandHandler(IScoringPromptRepository promptRepo)
    {
        _promptRepo = promptRepo;
    }

    public async Task<ScoringPrompt> Handle(RatePromptCommand request, CancellationToken ct)
    {
        // FR-037: Validate rating 0-5
        if (request.Rating < 0 || request.Rating > 5)
            throw new InvalidOperationException("Rating must be between 0 and 5");

        var prompt = await _promptRepo.GetByIdAsync(request.PromptId, ct)
            ?? throw new InvalidOperationException("Prompt not found");

        prompt.Rating = request.Rating;
        prompt.Comments = request.Comments;
        prompt.LastModifiedAt = DateTime.UtcNow;

        await _promptRepo.UpdateAsync(prompt, ct);
        return prompt;
    }
}
