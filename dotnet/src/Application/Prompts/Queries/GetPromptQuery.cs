using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public record GetPromptQuery(string PromptId) : IRequest<ScoringPrompt?>;

public class GetPromptQueryHandler : IRequestHandler<GetPromptQuery, ScoringPrompt?>
{
    private readonly IScoringPromptRepository _promptRepo;

    public GetPromptQueryHandler(IScoringPromptRepository promptRepo)
    {
        _promptRepo = promptRepo;
    }

    public async Task<ScoringPrompt?> Handle(GetPromptQuery request, CancellationToken ct)
    {
        return await _promptRepo.GetByIdAsync(request.PromptId, ct);
    }
}
