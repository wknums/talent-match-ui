using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class JobRepository : IJobRepository
{
    private readonly AppDbContext _context;
    public JobRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default)
        => await _context.Jobs.Include(j => j.ConfigVersions).AsNoTracking().ToListAsync(ct);

    public async Task<IReadOnlyList<Job>> GetByDepartmentAsync(string department, CancellationToken ct = default)
        => await _context.Jobs.Include(j => j.ConfigVersions)
            .Where(j => j.Department == department).AsNoTracking().ToListAsync(ct);

    public async Task<Job?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.Jobs.Include(j => j.ConfigVersions).Include(j => j.Applications)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task AddAsync(Job job, CancellationToken ct = default)
    {
        await _context.Jobs.AddAsync(job, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Job job, CancellationToken ct = default)
    {
        _context.Jobs.Update(job);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var job = await _context.Jobs.FindAsync(new object[] { id }, ct);
        if (job != null)
        {
            _context.Jobs.Remove(job);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task AddConfigVersionAsync(JobConfigVersion version, CancellationToken ct = default)
    {
        await _context.JobConfigVersions.AddAsync(version, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<JobConfigVersion>> GetConfigVersionsAsync(string jobId, CancellationToken ct = default)
        => await _context.JobConfigVersions.Where(v => v.JobId == jobId)
            .OrderBy(v => v.VersionNumber).AsNoTracking().ToListAsync(ct);
}
