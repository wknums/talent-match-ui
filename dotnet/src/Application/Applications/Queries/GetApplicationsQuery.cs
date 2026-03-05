using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Queries;

public record GetApplicationsQuery(
    string JobId,
    string? List,
    string? SortField,
    string? SortOrder,
    double? VarianceMin,
    int Page,
    int PageSize
) : IRequest<IReadOnlyList<Domain.Entities.Application>>;

public class GetApplicationsQueryHandler : IRequestHandler<GetApplicationsQuery, IReadOnlyList<Domain.Entities.Application>>
{
    private readonly IApplicationRepository _applicationRepository;

    public GetApplicationsQueryHandler(IApplicationRepository applicationRepository)
    {
        _applicationRepository = applicationRepository;
    }

    public async Task<IReadOnlyList<Domain.Entities.Application>> Handle(GetApplicationsQuery request, CancellationToken cancellationToken)
    {
        var apps = await _applicationRepository.GetByJobIdAsync(request.JobId, cancellationToken);
        return apps;
    }
}
