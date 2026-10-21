namespace TalentMatch.Domain.Entities;

public sealed record ApplicationScoringFinalization(
    IReadOnlyList<ScoringRun> Runs,
    AggregatedResult Aggregate,
    string Status,
    string? CandidateName = null);
