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

        // Filter to recruiters/admins with departments, scoped by caller role
        var users = allUsers
            .Where(u => (u.Role == "recruiter" || u.Role == "admin")
                && !string.IsNullOrEmpty(u.Department))
            .ToList();

        if (request.CallerRole != "admin" && !string.IsNullOrEmpty(request.CallerDepartment))
        {
            users = users.Where(u => u.Department == request.CallerDepartment).ToList();
        }

        var results = new List<RecruiterAnalytics>();

        foreach (var user in users)
        {
            var userJobs = allJobs.Where(j => j.CreatedBy == user.Id).ToList();

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
}
