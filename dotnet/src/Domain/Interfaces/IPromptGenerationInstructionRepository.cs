using TalentMatch.Domain.Entities;

namespace TalentMatch.Domain.Interfaces;

public interface IPromptGenerationInstructionRepository
{
    Task<IReadOnlyList<PromptGenerationInstruction>> ListAsync(
        string? jobId, CancellationToken cancellationToken = default);
    Task<PromptGenerationInstruction?> GetByIdAsync(
        string id, CancellationToken cancellationToken = default);
    Task<PromptGenerationInstruction?> GetActiveAsync(
        string? jobId, CancellationToken cancellationToken = default);
    Task<int> GetNextVersionNumberAsync(
        string? jobId, CancellationToken cancellationToken = default);
    Task AddAsync(
        PromptGenerationInstruction instruction, CancellationToken cancellationToken = default);
    Task UpdateAsync(
        PromptGenerationInstruction instruction, CancellationToken cancellationToken = default);
}
