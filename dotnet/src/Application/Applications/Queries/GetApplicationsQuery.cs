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

public sealed record GetApplicationCountsQuery(
    string JobId,
    bool IncludeTestCases = false) : IRequest<ApplicationCounts>;

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
        var job = await _jobRepository.GetByIdWithoutApplicationsAsync(
                request.JobId, cancellationToken)
            ?? await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");
        await JobAuthorization.EnsureCanReadAsync(job, _currentUser, _organizations, cancellationToken);

        var config = job.CurrentConfigVersionId is null
            ? null
            : job.ConfigVersions.FirstOrDefault(version =>
                version.Id == job.CurrentConfigVersionId);
        var apps = await _applicationRepository.GetByJobIdAsync(
            request.JobId,
            request.List,
            config?.LonglistThreshold ?? 70,
            config?.ShortlistThreshold ?? 85,
            request.IncludeTestCases,
            cancellationToken)
            ?? await _applicationRepository.GetByJobIdAsync(
                request.JobId, cancellationToken);

        apps = apps.Where(application => application.Status != "Uploading").ToList();
        if (!request.IncludeTestCases)
            apps = apps.Where(application => application.TestRunId == null).ToList();

        apps = request.List?.ToLowerInvariant() switch
        {
            "shortlist" => apps.Where(application =>
                application.FinalScore != null
                && application.FinalScore >= (config?.ShortlistThreshold ?? 85)).ToList(),
            "longlist" => apps.Where(application =>
                application.FinalScore != null
                && application.FinalScore >= (config?.LonglistThreshold ?? 70)).ToList(),
            "excluded" => apps.Where(application =>
                application.FinalDecision == "Excluded").ToList(),
            "review" => apps.Where(application =>
                application.Status == "NeedsManualReview"
                || application.FinalDecision == "NeedsManualReview").ToList(),
            "failed" => apps.Where(application =>
                application.Status == "Failed"
                || application.Status == "ScoringFailed"
                || application.Status == "ExtractionFailed").ToList(),
            _ => apps,
        };

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

public sealed class GetApplicationCountsQueryHandler
    : IRequestHandler<GetApplicationCountsQuery, ApplicationCounts>
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService? _currentUser;
    private readonly IOrganizationRepository? _organizations;

    public GetApplicationCountsQueryHandler(
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

    public async Task<ApplicationCounts> Handle(
        GetApplicationCountsQuery request,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdWithoutApplicationsAsync(
                request.JobId, cancellationToken)
            ?? await _jobRepository.GetByIdAsync(request.JobId, cancellationToken)
            ?? throw new InvalidOperationException($"Job '{request.JobId}' not found.");
        await JobAuthorization.EnsureCanReadAsync(
            job, _currentUser, _organizations, cancellationToken);
        var config = job.CurrentConfigVersionId is null
            ? null
            : job.ConfigVersions.FirstOrDefault(version =>
                version.Id == job.CurrentConfigVersionId);
        return await _applicationRepository.GetCountsByJobIdAsync(
            request.JobId,
            config?.LonglistThreshold ?? 70,
            config?.ShortlistThreshold ?? 85,
            request.IncludeTestCases,
            cancellationToken);
    }
}
