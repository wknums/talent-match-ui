namespace TalentMatch.Domain.Entities;

public record RecruiterAnalytics(
    string RecruiterId,
    string RecruiterName,
    string Department,
    int ApplicationsInQueue,
    int ManualReviewsPerformed,
    int ShortlistRecommendations,
    double? AverageProcessingTime,
    int ActiveJobs);
