namespace TalentMatch.Application.Common.Interfaces;

public sealed record ScoringProfile(string ModelId, string ReasoningLevel)
{
    public bool Matches(string? modelId, string? reasoningLevel)
        => string.Equals(ModelId, modelId, StringComparison.Ordinal)
           && string.Equals(ReasoningLevel, reasoningLevel, StringComparison.Ordinal);
}

public interface IScoringProfileProvider
{
    ScoringProfile Current { get; }
}
