using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public record GetPromptsQuery(string JobId) : IRequest<IReadOnlyList<ScoringPrompt>>;

public class GetPromptsQueryHandler : IRequestHandler<GetPromptsQuery, IReadOnlyList<ScoringPrompt>>
{
    private readonly IScoringPromptRepository _promptRepo;

    public GetPromptsQueryHandler(IScoringPromptRepository promptRepo)
    {
        _promptRepo = promptRepo;
    }

    public async Task<IReadOnlyList<ScoringPrompt>> Handle(GetPromptsQuery request, CancellationToken ct)
    {
        var prompts = await _promptRepo.GetByJobIdAsync(request.JobId, ct);
        return prompts.OrderByDescending(p => p.VersionNumber).ToList();
    }
}
