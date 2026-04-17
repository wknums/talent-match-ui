using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Analytics.Queries;

public record GetRecruiterAnalyticsQuery(string CallerRole, string? CallerDepartment) : IRequest<List<RecruiterAnalytics>>;

public class GetRecruiterAnalyticsQueryHandler : IRequestHandler<GetRecruiterAnalyticsQuery, List<RecruiterAnalytics>>
{
    private readonly IUserRepository _userRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IApplicationRepository _applicationRepository;

    public GetRecruiterAnalyticsQueryHandler(
        IUserRepository userRepository,
        IJobRepository jobRepository,
        IApplicationRepository applicationRepository)
    {
        _userRepository = userRepository;
        _jobRepository = jobRepository;
        _applicationRepository = applicationRepository;
    }

    public async Task<List<RecruiterAnalytics>> Handle(GetRecruiterAnalyticsQuery request, CancellationToken cancellationToken)
    {
        var allUsers = await _userRepository.GetAllAsync(cancellationToken);
        var allJobs = await _jobRepository.GetAllAsync(cancellationToken);

        var users = allUsers
            .Where(u => (u.Role == "recruiter" || u.Role == "admin")
                && !string.IsNullOrEmpty(u.Department))
            .ToList();

        if (request.CallerRole != "admin" && !string.IsNullOrEmpty(request.CallerDepartment))
        {
            users = users.Where(u => u.Department == request.CallerDepartment).ToList();
        }

        var usersById = users.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var usersByUsername = users
            .Where(u => !string.IsNullOrWhiteSpace(u.Username))
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var departmentCandidates = users
            .GroupBy(u => u.Department, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var jobsByRecruiterId = users.ToDictionary(
            user => user.Id,
            _ => new List<Job>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var job in allJobs)
        {
            var recruiterId = ResolveRecruiterId(job, usersById, usersByUsername, departmentCandidates);
            if (recruiterId is null) continue;

            if (jobsByRecruiterId.TryGetValue(recruiterId, out var recruiterJobs))
            {
                recruiterJobs.Add(job);
            }
        }

        var results = new List<RecruiterAnalytics>();

        foreach (var user in users)
        {
            var userJobs = jobsByRecruiterId[user.Id];

            var activeJobs = userJobs.Count(j =>
                j.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                j.Status.Equals("Processing", StringComparison.OrdinalIgnoreCase));

            int applicationsInQueue = 0;
            int manualReviewsPerformed = 0;
            int shortlistRecommendations = 0;
            double totalProcessingHours = 0;
            int completedCount = 0;

            foreach (var job in userJobs)
            {
                var apps = await _applicationRepository.GetByJobIdAsync(job.Id, cancellationToken);
                foreach (var app in apps.Where(a => a.TestRunId == null))
                {
                    if (app.Status == "Queued") applicationsInQueue++;
                    if (app.Status == "NeedsManualReview") manualReviewsPerformed++;
                    if (app.FinalDecision == "Eligible") shortlistRecommendations++;

                    var result = await _applicationRepository.GetAggregatedResultAsync(app.Id, cancellationToken);
                    if (result != null && result.CreatedAt > app.CreatedAt)
                    {
                        totalProcessingHours += (result.CreatedAt - app.CreatedAt).TotalHours;
                        completedCount++;
                    }
                }
            }

            double? averageProcessingTime = completedCount > 0
                ? Math.Round(totalProcessingHours / completedCount, 2)
                : null;

            results.Add(new RecruiterAnalytics(
                user.Id,
                user.FullName,
                user.Department,
                applicationsInQueue,
                manualReviewsPerformed,
                shortlistRecommendations,
                averageProcessingTime,
                activeJobs));
        }

        return results;
    }

    private static string? ResolveRecruiterId(
        Job job,
        IReadOnlyDictionary<string, User> usersById,
        IReadOnlyDictionary<string, User> usersByUsername,
        IReadOnlyDictionary<string, List<User>> departmentCandidates)
    {
        if (!string.IsNullOrWhiteSpace(job.CreatedBy))
        {
            if (usersById.ContainsKey(job.CreatedBy))
            {
                return job.CreatedBy;
            }

            if (usersByUsername.TryGetValue(job.CreatedBy, out var userByUsername))
            {
                return userByUsername.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(job.Department))
        {
            return null;
        }

        if (!departmentCandidates.TryGetValue(job.Department, out var candidates) || candidates.Count != 1)
        {
            return null;
        }

        return candidates[0].Id;
    }
}
