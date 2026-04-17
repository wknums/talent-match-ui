using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class FailureQueueRepository : IFailureQueueRepository
{
    private readonly AppDbContext _context;
    public FailureQueueRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<FailureQueueItem>> GetAllAsync(CancellationToken ct = default)
        => await _context.FailureQueueItems.AsNoTracking().ToListAsync(ct);

    public async Task<FailureQueueItem?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.FailureQueueItems.FindAsync(new object[] { id }, ct);

    public async Task AddAsync(FailureQueueItem item, CancellationToken ct = default)
    {
        await _context.FailureQueueItems.AddAsync(item, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        var item = await _context.FailureQueueItems.FindAsync(new object[] { id }, ct);
        if (item != null)
        {
            _context.FailureQueueItems.Remove(item);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task<HashSet<string>> GetEntityIdsAsync(CancellationToken ct = default)
        => (await _context.FailureQueueItems.AsNoTracking().Select(i => i.EntityId).ToListAsync(ct)).ToHashSet();
}
