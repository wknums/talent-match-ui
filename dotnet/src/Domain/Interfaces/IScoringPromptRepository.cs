namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IScoringPromptRepository
{
    Task<IReadOnlyList<ScoringPrompt>> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ScoringPrompt?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(ScoringPrompt prompt, CancellationToken cancellationToken = default);
    Task UpdateAsync(ScoringPrompt prompt, CancellationToken cancellationToken = default);
    Task<ScoringPrompt?> GetActiveForJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ScoringPrompt?> GetProductionApprovedForJobAsync(string jobId, CancellationToken cancellationToken = default);
}
