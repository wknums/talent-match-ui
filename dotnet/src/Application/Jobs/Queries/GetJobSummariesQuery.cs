using MediatR;
using TalentMatch.Application.Common.Interfaces;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Jobs.Queries;

public record JobSummaryDto(
    string Id,
    string JobCode,
    string Title,
    string Department,
    string Organisation,
    DateTime PostingDate,
    string Status,
    string? CurrentConfigVersionId,
    string? JobDescription,
    string? CreatedBy,
    DateTime CreatedAt,
    string CreatedByName,
    int TotalApplications,
    int CompletedApplications);

public record GetJobSummariesQuery : IRequest<IReadOnlyList<JobSummaryDto>>;

public class GetJobSummariesQueryHandler : IRequestHandler<GetJobSummariesQuery, IReadOnlyList<JobSummaryDto>>
{
    private readonly IJobRepository _jobRepository;
    private readonly IUserRepository _userRepository;
    private readonly ICurrentUserService _currentUser;

    public GetJobSummariesQueryHandler(
        IJobRepository jobRepository,
        IUserRepository userRepository,
        ICurrentUserService currentUser)
    {
        _jobRepository = jobRepository;
        _userRepository = userRepository;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<JobSummaryDto>> Handle(GetJobSummariesQuery request, CancellationToken cancellationToken)
    {
        // Replicate RBAC filtering from GetJobsQueryHandler
        var jobs = (_currentUser.IsAdmin || string.Equals(_currentUser.Department, "all", StringComparison.OrdinalIgnoreCase))
            ? await _jobRepository.GetAllAsync(cancellationToken)
            : await _jobRepository.GetByDepartmentsOrCreatorAsync(
                (_currentUser.Department ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _currentUser.UserId ?? "",
                cancellationToken);

        // Batch-load all users for creator name resolution
        var allUsers = await _userRepository.GetAllAsync(cancellationToken);
        var userLookup = allUsers.ToDictionary(u => u.Id, u => u.FullName);

        // For application counts, we need to load jobs with Applications included.
        // GetAllAsync/GetByDepartmentsOrCreatorAsync don't include Applications,
        // so we fetch each job by ID (which includes Applications) for count data.
        // Optimization: load all job IDs and query applications in bulk.
        var jobIds = jobs.Select(j => j.Id).ToHashSet();
        var jobsWithApps = new Dictionary<string, (int Total, int Completed)>();

        foreach (var job in jobs)
        {
            // GetByIdAsync includes Applications
            var fullJob = await _jobRepository.GetByIdAsync(job.Id, cancellationToken);
            if (fullJob != null)
            {
                var total = fullJob.Applications.Count;
                var completed = fullJob.Applications.Count(a => a.Status == "Completed");
                jobsWithApps[job.Id] = (total, completed);
            }
        }

        return jobs.Select(j =>
        {
            var createdByName = j.CreatedBy != null && userLookup.TryGetValue(j.CreatedBy, out var name)
                ? name
                : "Unknown User";

            var (total, completed) = jobsWithApps.TryGetValue(j.Id, out var counts)
                ? counts
                : (0, 0);

            return new JobSummaryDto(
                j.Id, j.JobCode, j.Title, j.Department, j.Organisation,
                j.PostingDate, j.Status, j.CurrentConfigVersionId, j.JobDescription,
                j.CreatedBy, j.CreatedAt, createdByName, total, completed);
        }).ToList();
    }
}
