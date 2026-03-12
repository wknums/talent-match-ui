namespace TalentMatch.Domain.Entities;

public record DepartmentAnalytics(
    string Department,
    int TotalRecruiters,
    int ApplicationsInQueue,
    int ManualReviewsPerformed,
    int ShortlistRecommendations,
    int ActiveJobs,
    IReadOnlyList<RecruiterAnalytics> Recruiters);
