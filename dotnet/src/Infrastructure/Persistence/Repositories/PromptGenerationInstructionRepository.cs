using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class PromptGenerationInstructionRepository(AppDbContext db)
    : IPromptGenerationInstructionRepository
{
    public async Task<IReadOnlyList<PromptGenerationInstruction>> ListAsync(
        string? jobId, CancellationToken cancellationToken = default)
        => await db.PromptGenerationInstructions
            .Where(item => item.JobId == jobId)
            .OrderByDescending(item => item.VersionNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public Task<PromptGenerationInstruction?> GetByIdAsync(
        string id, CancellationToken cancellationToken = default)
        => db.PromptGenerationInstructions.FirstOrDefaultAsync(
            item => item.Id == id, cancellationToken);

    public Task<PromptGenerationInstruction?> GetActiveAsync(
        string? jobId, CancellationToken cancellationToken = default)
        => db.PromptGenerationInstructions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.JobId == jobId && item.Status == "active",
                cancellationToken);

    public async Task<int> GetNextVersionNumberAsync(
        string? jobId, CancellationToken cancellationToken = default)
        => (await db.PromptGenerationInstructions
            .Where(item => item.JobId == jobId)
            .MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;

    public async Task AddAsync(
        PromptGenerationInstruction instruction, CancellationToken cancellationToken = default)
    {
        await db.PromptGenerationInstructions.AddAsync(instruction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        PromptGenerationInstruction instruction, CancellationToken cancellationToken = default)
    {
        db.PromptGenerationInstructions.Update(instruction);
        await db.SaveChangesAsync(cancellationToken);
    }
}
