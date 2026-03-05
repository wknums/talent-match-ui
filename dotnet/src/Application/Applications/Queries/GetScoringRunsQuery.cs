using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Queries;

public record GetScoringRunsQuery(string ApplicationId) : IRequest<IReadOnlyList<ScoringRun>>;

public class GetScoringRunsQueryHandler : IRequestHandler<GetScoringRunsQuery, IReadOnlyList<ScoringRun>>
{
    private readonly IApplicationRepository _applicationRepository;

    public GetScoringRunsQueryHandler(IApplicationRepository applicationRepository)
    {
        _applicationRepository = applicationRepository;
    }

    public async Task<IReadOnlyList<ScoringRun>> Handle(GetScoringRunsQuery request, CancellationToken cancellationToken)
    {
        return await _applicationRepository.GetScoringRunsAsync(request.ApplicationId, cancellationToken);
    }
}
