namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IPromptTestRunRepository
{
    Task<IReadOnlyList<PromptTestRun>> GetByPromptIdAsync(string promptId, CancellationToken cancellationToken = default);
    Task<PromptTestRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(PromptTestRun testRun, CancellationToken cancellationToken = default);
    Task UpdateAsync(PromptTestRun testRun, CancellationToken cancellationToken = default);
}
