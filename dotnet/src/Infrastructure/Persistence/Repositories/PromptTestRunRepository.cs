using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class PromptTestRunRepository : IPromptTestRunRepository
{
    private readonly AppDbContext _context;
    public PromptTestRunRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<PromptTestRun>> GetByPromptIdAsync(string promptId, CancellationToken ct = default)
        => await _context.PromptTestRuns.Where(tr => tr.PromptId == promptId)
            .AsNoTracking().ToListAsync(ct);

    public async Task<PromptTestRun?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.PromptTestRuns.FirstOrDefaultAsync(tr => tr.Id == id, ct);

    public async Task AddAsync(PromptTestRun testRun, CancellationToken ct = default)
    {
        await _context.PromptTestRuns.AddAsync(testRun, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PromptTestRun testRun, CancellationToken ct = default)
    {
        _context.PromptTestRuns.Update(testRun);
        await _context.SaveChangesAsync(ct);
    }
}
