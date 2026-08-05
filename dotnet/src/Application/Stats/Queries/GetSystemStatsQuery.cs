using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Application.Jobs;
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
        var authorizationState = await _currentUser.GetAuthorizationStateAsync(cancellationToken);
        IReadOnlyList<Domain.Entities.Job> jobs;
        if (authorizationState is null)
        {
            jobs = (_currentUser.IsAdmin || string.Equals(_currentUser.Department, "all", StringComparison.OrdinalIgnoreCase))
                ? await _jobRepository.GetAllAsync(cancellationToken)
                : await _jobRepository.GetByDepartmentsOrCreatorAsync(
                    (_currentUser.Department ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    _currentUser.UserId ?? "",
                    cancellationToken);
        }
        else
        {
            jobs = (await _jobRepository.GetAllAsync(cancellationToken))
                .Where(job => JobAuthorization.CanRead(authorizationState, job))
                .ToArray();
        }

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

        return new SystemStatsDto(
            Count("Queued"),
            Count("Extracting"),
            Count("Scoring"),
            Count("Aggregating"),
            Count("Completed"),
            Count("NeedsManualReview"),
            Count("Failed"),
            jobs.Count,
            totalApplications);
    }
}
