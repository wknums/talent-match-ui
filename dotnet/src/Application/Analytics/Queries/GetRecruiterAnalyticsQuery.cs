using MediatR;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Application.Analytics.Queries;

public record GetRecruiterAnalyticsQuery(
    string CallerRole,
    string? CallerDepartment,
    string? CallerUserId = null,
    string? TenantId = null) : IRequest<List<RecruiterAnalytics>>;

public class GetRecruiterAnalyticsQueryHandler : IRequestHandler<GetRecruiterAnalyticsQuery, List<RecruiterAnalytics>>
{
    private readonly IUserRepository _userRepository;
    private readonly IJobRepository _jobRepository;
    private readonly IApplicationRepository _applicationRepository;
    private readonly IRoleAssignmentRepository? _roleAssignmentRepository;
    private readonly IOrganizationRepository? _organizationRepository;

    public GetRecruiterAnalyticsQueryHandler(
        IUserRepository userRepository,
        IJobRepository jobRepository,
        IApplicationRepository applicationRepository,
        IRoleAssignmentRepository? roleAssignmentRepository = null,
        IOrganizationRepository? organizationRepository = null)
    {
        _userRepository = userRepository;
        _jobRepository = jobRepository;
        _applicationRepository = applicationRepository;
        _roleAssignmentRepository = roleAssignmentRepository;
        _organizationRepository = organizationRepository;
    }

    public async Task<List<RecruiterAnalytics>> Handle(GetRecruiterAnalyticsQuery request, CancellationToken cancellationToken)
    {
        var allUsers = await _userRepository.GetAllAsync(cancellationToken);
        var allJobs = await _jobRepository.GetAllAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.CallerUserId)
            && !string.IsNullOrWhiteSpace(request.TenantId)
            && _roleAssignmentRepository is not null
            && _organizationRepository is not null)
        {
            return await BuildEntraAnalyticsAsync(request, allUsers, allJobs, cancellationToken);
        }

        var users = allUsers
            .Where(u => (u.Role == "recruiter" || u.Role == "admin")
                && !string.IsNullOrEmpty(u.Department)
                && !string.IsNullOrWhiteSpace(u.Id))
            .ToList();

        if (request.CallerRole != "admin" && !string.IsNullOrEmpty(request.CallerDepartment))
        {
            users = users.Where(u => u.Department == request.CallerDepartment).ToList();
        }

        var usersById = users
            .GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var usersByUsername = users
            .Where(u => !string.IsNullOrWhiteSpace(u.Username))
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var departmentCandidates = users
            .GroupBy(u => u.Department, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var jobsByRecruiterId = usersById.Keys.ToDictionary(
            recruiterId => recruiterId,
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
                string.Equals(j.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Status, "Processing", StringComparison.OrdinalIgnoreCase));

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

    private async Task<List<RecruiterAnalytics>> BuildEntraAnalyticsAsync(
        GetRecruiterAnalyticsQuery request,
        IReadOnlyList<User> allUsers,
        IReadOnlyList<Job> allJobs,
        CancellationToken cancellationToken)
    {
        var assignments = await _roleAssignmentRepository!.GetActiveByRoleAsync(
            request.TenantId!, "recruiter", cancellationToken);
        if (request.CallerRole != "admin")
        {
            assignments = assignments
                .Where(assignment => string.Equals(
                    assignment.UserId, request.CallerUserId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var usersById = allUsers
            .Where(user => user.IsActive)
            .ToDictionary(user => user.Id, StringComparer.OrdinalIgnoreCase);
        var results = new List<RecruiterAnalytics>();
        foreach (var assignment in assignments
            .Where(assignment => assignment.OrganizationId is not null && assignment.DepartmentId is not null)
            .GroupBy(assignment => (assignment.UserId, assignment.OrganizationId, assignment.DepartmentId))
            .Select(group => group.First()))
        {
            if (!usersById.TryGetValue(assignment.UserId, out var user))
                continue;

            var department = await _organizationRepository!.GetDepartmentAsync(
                assignment.OrganizationId!, assignment.DepartmentId!, cancellationToken);
            if (department?.Status != "active")
                continue;

            var userJobs = allJobs.Where(job =>
                (string.Equals(job.CreatedBy, user.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job.CreatedBy, user.Username, StringComparison.OrdinalIgnoreCase))
                && string.Equals(job.OrganizationId, assignment.OrganizationId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(job.DepartmentId, assignment.DepartmentId, StringComparison.OrdinalIgnoreCase));
            results.Add(await CalculateAsync(user, department.Name, userJobs, cancellationToken));
        }

        return results;
    }

    private async Task<RecruiterAnalytics> CalculateAsync(
        User user,
        string department,
        IEnumerable<Job> jobs,
        CancellationToken cancellationToken)
    {
        var userJobs = jobs.ToArray();
        var activeJobs = userJobs.Count(job =>
            string.Equals(job.Status, "Active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, "Processing", StringComparison.OrdinalIgnoreCase));
        var applicationsInQueue = 0;
        var manualReviewsPerformed = 0;
        var shortlistRecommendations = 0;
        var totalProcessingHours = 0d;
        var completedCount = 0;

        foreach (var job in userJobs)
        {
            var applications = await _applicationRepository.GetByJobIdAsync(job.Id, cancellationToken);
            foreach (var application in applications.Where(item => item.TestRunId is null))
            {
                if (application.Status == "Queued") applicationsInQueue++;
                if (application.Status == "NeedsManualReview") manualReviewsPerformed++;
                if (application.FinalDecision == "Eligible") shortlistRecommendations++;
                var result = await _applicationRepository.GetAggregatedResultAsync(application.Id, cancellationToken);
                if (result is not null && result.CreatedAt > application.CreatedAt)
                {
                    totalProcessingHours += (result.CreatedAt - application.CreatedAt).TotalHours;
                    completedCount++;
                }
            }
        }

        return new RecruiterAnalytics(
            user.Id,
            user.FullName,
            department,
            applicationsInQueue,
            manualReviewsPerformed,
            shortlistRecommendations,
            completedCount == 0 ? null : Math.Round(totalProcessingHours / completedCount, 2),
            activeJobs);
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
