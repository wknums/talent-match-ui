using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public sealed class JobSpecExtractionRepository : IJobSpecExtractionRepository
{
    private readonly AppDbContext _context;

    public JobSpecExtractionRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(JobSpecExtraction extraction, CancellationToken cancellationToken = default)
    {
        await _context.JobSpecExtractions.AddAsync(extraction, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<JobSpecExtraction?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        => _context.JobSpecExtractions.AsNoTracking().FirstOrDefaultAsync(extraction => extraction.Id == id, cancellationToken);

    public Task<JobSpecExtraction?> GetLatestForJobAsync(string jobId, CancellationToken cancellationToken = default)
        => _context.JobSpecExtractions.AsNoTracking()
            .Where(extraction => extraction.JobId == jobId)
            .OrderByDescending(extraction => extraction.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<JobSpecExtraction?> GetByConfigVersionIdAsync(string jobConfigVersionId, CancellationToken cancellationToken = default)
        => _context.JobSpecExtractions.AsNoTracking()
            .OrderByDescending(extraction => extraction.CreatedAt)
            .FirstOrDefaultAsync(extraction => extraction.JobConfigVersionId == jobConfigVersionId, cancellationToken);

    public async Task LinkToJobConfigAsync(string extractionId, string jobId, string jobConfigVersionId, CancellationToken cancellationToken = default)
    {
        var extraction = await _context.JobSpecExtractions.FirstOrDefaultAsync(item => item.Id == extractionId, cancellationToken)
            ?? throw new InvalidOperationException($"Job specification extraction '{extractionId}' was not found.");

        if (!string.IsNullOrWhiteSpace(extraction.JobConfigVersionId)
            && !string.Equals(extraction.JobConfigVersionId, jobConfigVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The extraction record is already linked to a different job configuration version.");
        }

        extraction.JobId = jobId;
        extraction.JobConfigVersionId = jobConfigVersionId;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
