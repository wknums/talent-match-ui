using MediatR;
using Microsoft.Extensions.Logging;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Applications.Queries;

public record GetApplicationsQuery(
    string JobId,
    string? List,
    string? ApplicantName,
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
    private readonly ILogger<GetApplicationsQueryHandler> _logger;

    public GetApplicationsQueryHandler(
        IApplicationRepository applicationRepository,
        IFailureQueueRepository failureQueueRepository,
        ILogger<GetApplicationsQueryHandler> logger)
    {
        _applicationRepository = applicationRepository;
        _failureQueueRepository = failureQueueRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Domain.Entities.Application>> Handle(GetApplicationsQuery request, CancellationToken cancellationToken)
    {
        var apps = await _applicationRepository.GetByJobIdAsync(request.JobId, cancellationToken);

        // FR-038: Filter out test run applications from production results unless explicitly requested
        if (!request.IncludeTestCases)
        {
            apps = apps.Where(a => a.TestRunId == null).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.ApplicantName))
        {
            var searchTerm = request.ApplicantName.Trim();
            apps = apps.Where(a =>
                    (!string.IsNullOrWhiteSpace(a.CandidateName)
                     && a.CandidateName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(a.CandidateRef)
                        && a.CandidateRef.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                .ToList();
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
                    try
                    {
                        await _applicationRepository.DeleteAsync(fa.Id, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // Read operations must remain stable even if best-effort cleanup fails.
                        _logger.LogWarning(ex,
                            "Failed to delete orphaned failed application {ApplicationId} during list query for job {JobId}",
                            fa.Id,
                            request.JobId);
                    }
                }
            }
            if (orphanIds.Count > 0)
                apps = apps.Where(a => !orphanIds.Contains(a.Id)).ToList();
        }

        return apps;
    }
}
