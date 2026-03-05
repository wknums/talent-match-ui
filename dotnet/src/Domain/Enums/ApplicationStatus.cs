namespace TalentMatch.Domain.Enums;

public enum ApplicationStatus
{
    Queued,
    Extracting,
    Scoring,
    Aggregating,
    Completed,
    NeedsManualReview,
    Failed
}
