using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IExtractionInstructionRepository
{
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
    Task<int> GetNextVersionNumberAsync(CancellationToken cancellationToken = default);
    Task<ExtractionInstructionVersion?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<ExtractionInstructionVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExtractionInstructionVersion>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(ExtractionInstructionVersion version, CancellationToken cancellationToken = default);
    Task UpdateAsync(ExtractionInstructionVersion version, CancellationToken cancellationToken = default);
    Task ActivateAsync(string id, string actor, DateTime activatedAtUtc, CancellationToken cancellationToken = default);
}
