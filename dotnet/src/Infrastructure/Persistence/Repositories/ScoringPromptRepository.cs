using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class ScoringPromptRepository : IScoringPromptRepository
{
    private readonly AppDbContext _context;
    public ScoringPromptRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<ScoringPrompt>> GetByJobIdAsync(string jobId, CancellationToken ct = default)
        => await _context.ScoringPrompts.Where(p => p.JobId == jobId)
            .OrderByDescending(p => p.VersionNumber).AsNoTracking().ToListAsync(ct);

    public async Task<ScoringPrompt?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.ScoringPrompts.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(ScoringPrompt prompt, CancellationToken ct = default)
    {
        await _context.ScoringPrompts.AddAsync(prompt, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ScoringPrompt prompt, CancellationToken ct = default)
    {
        _context.ScoringPrompts.Update(prompt);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<ScoringPrompt?> GetActiveForJobAsync(string jobId, CancellationToken ct = default)
        => await _context.ScoringPrompts.FirstOrDefaultAsync(p => p.JobId == jobId && p.Status == "active", ct);

    public async Task<ScoringPrompt?> GetProductionApprovedForJobAsync(string jobId, CancellationToken ct = default)
        => await _context.ScoringPrompts.FirstOrDefaultAsync(p => p.JobId == jobId && p.Status == "production-approved", ct);
}
