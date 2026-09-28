namespace TalentMatch.Domain.Interfaces;

public interface IScoringThroughputRepository
{
    Task<IReadOnlyDictionary<int, int>> GetHourlyCountsAsync(
        IReadOnlyCollection<string> jobIds, DateTime asOfUtc, CancellationToken ct = default);
}
