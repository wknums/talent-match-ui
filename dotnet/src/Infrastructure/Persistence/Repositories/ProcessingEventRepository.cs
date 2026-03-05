using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class ProcessingEventRepository : IProcessingEventRepository
{
    private readonly AppDbContext _context;
    public ProcessingEventRepository(AppDbContext context) => _context = context;

    public async Task AddAsync(ProcessingEvent evt, CancellationToken ct = default)
    {
        await _context.ProcessingEvents.AddAsync(evt, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ProcessingEvent>> GetFilteredAsync(
        string? entityType = null,
        string? eventType = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _context.ProcessingEvents.AsQueryable();

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(e => e.EntityType == entityType);

        if (!string.IsNullOrEmpty(eventType))
            query = query.Where(e => e.EventType == eventType);

        if (startDate.HasValue)
            query = query.Where(e => e.Timestamp >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.Timestamp <= endDate.Value);

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
