namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IFailureQueueRepository
{
    Task<IReadOnlyList<FailureQueueItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<FailureQueueItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(FailureQueueItem item, CancellationToken cancellationToken = default);
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
    Task<HashSet<string>> GetEntityIdsAsync(CancellationToken cancellationToken = default);
}
