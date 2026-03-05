namespace TalentMatch.Domain.Interfaces;

using TalentMatch.Domain.Entities;

public interface IProcessingEventRepository
{
    Task AddAsync(ProcessingEvent evt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcessingEvent>> GetFilteredAsync(
        string? entityType = null,
        string? eventType = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}
