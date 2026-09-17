namespace TalentMatch.Domain.Entities;

public sealed record SequentialScoringWork(
    string ApplicationId,
    string JobId,
    string Owner,
    string PromptVersionId,
    int RunCount,
    string JobDescription,
    string RubricJson,
    double VarianceThreshold,
    double LonglistThreshold);
