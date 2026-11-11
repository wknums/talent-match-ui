using TalentMatch.Domain.Entities;

namespace TalentMatch.Application.Common.Interfaces;

public interface ISequentialApplicationScorer
{
    Task ScoreAsync(SequentialScoringWork work, CancellationToken ct);
}
