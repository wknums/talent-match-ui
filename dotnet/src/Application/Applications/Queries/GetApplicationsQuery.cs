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
    int PageSize,
    bool IncludeTestCases = false
) : IRequest<IReadOnlyList<Domain.Entities.Application>>;

public class GetApplicationsQueryHandler : IRequestHandler<GetApplicationsQuery, IReadOnlyList<Domain.Entities.Application>>
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly IFailureQueueRepository _failureQueueRepository;

    public GetApplicationsQueryHandler(IApplicationRepository applicationRepository, IFailureQueueRepository failureQueueRepository)
    {
        _applicationRepository = applicationRepository;
        _failureQueueRepository = failureQueueRepository;
    }

    public async Task<IReadOnlyList<Domain.Entities.Application>> Handle(GetApplicationsQuery request, CancellationToken cancellationToken)
    {
        var apps = await _applicationRepository.GetByJobIdAsync(request.JobId, cancellationToken);

        // FR-038: Filter out test run applications from production results unless explicitly requested
        if (!request.IncludeTestCases)
        {
            apps = apps.Where(a => a.TestRunId == null).ToList();
        }

        // Remove orphaned failed apps (ScoringFailed/ExtractionFailed with no DLQ entry)
        var failedApps = apps.Where(a => a.Status is "ScoringFailed" or "ExtractionFailed").ToList();
        if (failedApps.Any())
        {
            var dlqEntityIds = await _failureQueueRepository.GetEntityIdsAsync(cancellationToken);
            var orphanIds = new HashSet<string>();
            foreach (var fa in failedApps)
            {
                if (!dlqEntityIds.Contains(fa.Id))
                {
                    orphanIds.Add(fa.Id);
                    await _applicationRepository.DeleteAsync(fa.Id, cancellationToken);
                }
            }
            if (orphanIds.Count > 0)
                apps = apps.Where(a => !orphanIds.Contains(a.Id)).ToList();
        }

        return apps;
    }
}
