using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Queries;

public record GetAggregatedResultQuery(string ApplicationId) : IRequest<AggregatedResult?>;

public class GetAggregatedResultQueryHandler : IRequestHandler<GetAggregatedResultQuery, AggregatedResult?>
{
    private readonly IApplicationRepository _applicationRepository;

    public GetAggregatedResultQueryHandler(IApplicationRepository applicationRepository)
    {
        _applicationRepository = applicationRepository;
    }

    public async Task<AggregatedResult?> Handle(GetAggregatedResultQuery request, CancellationToken cancellationToken)
    {
        return await _applicationRepository.GetAggregatedResultAsync(request.ApplicationId, cancellationToken);
    }
}
