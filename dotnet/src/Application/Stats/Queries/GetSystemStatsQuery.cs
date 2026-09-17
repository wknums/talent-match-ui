using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Stats.Queries;

public record SystemStatsDto(
    int Queued,
    int Extracting,
    int Scoring,
    int Aggregating,
    int Completed,
    int NeedsManualReview,
    int Failed,
    int TotalJobs,
    int TotalApplications);

public record GetSystemStatsQuery : IRequest<SystemStatsDto>;

public class GetSystemStatsQueryHandler : IRequestHandler<GetSystemStatsQuery, SystemStatsDto>
{
    private readonly IJobRepository _jobRepository;
    private readonly ICurrentUserService _currentUser;

    public GetSystemStatsQueryHandler(IJobRepository jobRepository, ICurrentUserService currentUser)
    {
        _jobRepository = jobRepository;
        _currentUser = currentUser;
    }

    public async Task<SystemStatsDto> Handle(GetSystemStatsQuery request, CancellationToken cancellationToken)
    {
        var jobs = await DashboardJobScope.GetVisibleJobsAsync(_jobRepository, _currentUser, cancellationToken);

        var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalApplications = 0;

        foreach (var job in jobs)
        {
            // GetByIdAsync is the only repository call that includes Applications.
            var fullJob = await _jobRepository.GetByIdAsync(job.Id, cancellationToken);
            if (fullJob is null)
                continue;

            foreach (var application in fullJob.Applications.Where(a => a.TestRunId == null))
            {
                totalApplications++;
                statusCounts[application.Status] = statusCounts.GetValueOrDefault(application.Status) + 1;
            }
        }

        int Count(string status) => statusCounts.GetValueOrDefault(status);
        var failed = Count("Failed") + Count("ScoringFailed") + Count("ExtractionFailed");

        return new SystemStatsDto(
            Count("Queued"),
            Count("Extracting"),
            Count("Scoring"),
            Count("Aggregating"),
            Count("Completed"),
            Count("NeedsManualReview"),
            failed,
            jobs.Count,
            totalApplications);
    }
}
