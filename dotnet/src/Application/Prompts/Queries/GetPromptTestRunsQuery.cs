using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Prompts.Queries;

public record GetPromptTestRunsQuery(string PromptId) : IRequest<IReadOnlyList<PromptTestRun>>;

public class GetPromptTestRunsQueryHandler : IRequestHandler<GetPromptTestRunsQuery, IReadOnlyList<PromptTestRun>>
{
    private readonly IPromptTestRunRepository _testRunRepo;

    public GetPromptTestRunsQueryHandler(IPromptTestRunRepository testRunRepo)
    {
        _testRunRepo = testRunRepo;
    }

    public async Task<IReadOnlyList<PromptTestRun>> Handle(GetPromptTestRunsQuery request, CancellationToken ct)
    {
        return await _testRunRepo.GetByPromptIdAsync(request.PromptId, ct);
    }
}
