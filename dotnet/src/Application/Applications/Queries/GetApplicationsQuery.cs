using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;
using TalentMatch.Application.Common.Services;

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
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizations;

    public GetApplicationsQueryHandler(
        IApplicationRepository applicationRepository,
        IJobRepository jobRepository,
        ICurrentUserService? currentUser = null,
        IOrganizationRepository? organizations = null)
    {
        _applicationRepository = applicationRepository;
        _jobRepository = jobRepository;
        _currentUser = currentUser;
        _organizations = organizations;
    }

    public async Task<IReadOnlyList<Domain.Entities.Application>> Handle(GetApplicationsQuery request, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");
        await JobAuthorization.EnsureCanReadAsync(job, _currentUser, _organizations, cancellationToken);

        var apps = await _applicationRepository.GetByJobIdAsync(request.JobId, cancellationToken);
        apps = apps.Where(application => application.Status != "Uploading").ToList();

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

        foreach (var app in apps)
        {
            app.FinalScore = ScorePrecision.Round(app.FinalScore);
            app.Variance = ScorePrecision.Round(app.Variance);
        }

        if (string.Equals(request.SortField, "score", StringComparison.OrdinalIgnoreCase))
        {
            apps = string.Equals(request.SortOrder, "asc", StringComparison.OrdinalIgnoreCase)
                ? apps.OrderBy(app => Math.Round(app.FinalScore ?? 0, ScorePrecision.DecimalPlaces)).ToList()
                : apps.OrderByDescending(app => Math.Round(app.FinalScore ?? 0, ScorePrecision.DecimalPlaces)).ToList();
        }

        return apps;
    }
}
