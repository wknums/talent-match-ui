using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class ExtractionInstructionRepository : IExtractionInstructionRepository
{
    private readonly AppDbContext _context;

    public ExtractionInstructionRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<bool> AnyAsync(CancellationToken cancellationToken = default)
        => _context.ExtractionInstructionVersions.AnyAsync(cancellationToken);

    public async Task<int> GetNextVersionNumberAsync(CancellationToken cancellationToken = default)
        => (await _context.ExtractionInstructionVersions
            .MaxAsync(version => (int?)version.VersionNumber, cancellationToken) ?? 0) + 1;

    public Task<ExtractionInstructionVersion?> GetActiveAsync(CancellationToken cancellationToken = default)
        => _context.ExtractionInstructionVersions
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(version => version.Status == "active", cancellationToken);

    public Task<ExtractionInstructionVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => _context.ExtractionInstructionVersions.FirstOrDefaultAsync(version => version.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ExtractionInstructionVersion>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.ExtractionInstructionVersions
            .OrderByDescending(version => version.VersionNumber)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ExtractionInstructionVersion version, CancellationToken cancellationToken = default)
    {
        await _context.ExtractionInstructionVersions.AddAsync(version, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ExtractionInstructionVersion version, CancellationToken cancellationToken = default)
    {
        version.ConcurrencyVersion += 1;
        _context.ExtractionInstructionVersions.Update(version);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ActivateAsync(string id, string actor, DateTime activatedAtUtc, CancellationToken cancellationToken = default)
    {
        await _context.ExecuteInTransactionAsync(async token =>
        {
            var versions = await _context.ExtractionInstructionVersions
                .Where(version => version.Status == "active" || version.Id == id)
                .ToListAsync(token);

            var target = versions.SingleOrDefault(version => version.Id == id)
                ?? throw new InvalidOperationException($"Extraction instruction version '{id}' was not found.");

            foreach (var active in versions.Where(version => version.Status == "active" && version.Id != id))
            {
                active.Status = "retired";
                active.ConcurrencyVersion += 1;
            }

            target.Status = "active";
            target.ActivatedAt = activatedAtUtc;
            target.ActivatedBy = actor;
            target.ConcurrencyVersion += 1;

            await _context.SaveChangesAsync(token);
        }, cancellationToken);
    }
}
