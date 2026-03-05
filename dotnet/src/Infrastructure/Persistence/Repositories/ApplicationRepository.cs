using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

public class ApplicationRepository : IApplicationRepository
{
    private readonly AppDbContext _context;
    public ApplicationRepository(AppDbContext context) => _context = context;

    public async Task<IReadOnlyList<TalentMatch.Domain.Entities.Application>> GetByJobIdAsync(string jobId, CancellationToken ct = default)
        => await _context.Applications
            .Include(a => a.Documents)
            .Include(a => a.ScoringRuns)
            .Where(a => a.JobId == jobId)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<TalentMatch.Domain.Entities.Application?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.Applications
            .Include(a => a.Documents)
            .Include(a => a.ScoringRuns)
            .Include(a => a.AggregatedResult)
            .Include(a => a.ManualReview)
            .Include(a => a.Extraction)
            .FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task AddAsync(TalentMatch.Domain.Entities.Application application, CancellationToken ct = default)
    {
        await _context.Applications.AddAsync(application, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TalentMatch.Domain.Entities.Application application, CancellationToken ct = default)
    {
        _context.Applications.Update(application);
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddDocumentAsync(ApplicationDocument document, CancellationToken ct = default)
    {
        await _context.ApplicationDocuments.AddAsync(document, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationDocument>> GetDocumentsAsync(string applicationId, CancellationToken ct = default)
        => await _context.ApplicationDocuments
            .Where(d => d.ApplicationId == applicationId)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task AddScoringRunAsync(ScoringRun run, CancellationToken ct = default)
    {
        await _context.ScoringRuns.AddAsync(run, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ScoringRun>> GetScoringRunsAsync(string applicationId, CancellationToken ct = default)
        => await _context.ScoringRuns
            .Where(r => r.ApplicationId == applicationId)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task SetAggregatedResultAsync(AggregatedResult result, CancellationToken ct = default)
    {
        var existing = await _context.AggregatedResults
            .FirstOrDefaultAsync(r => r.ApplicationId == result.ApplicationId, ct);

        if (existing != null)
        {
            existing.FinalScore = result.FinalScore;
            existing.Decision = result.Decision;
            existing.Variance = result.Variance;
            existing.Confidence = result.Confidence;
            existing.ConsolidatedRationale = result.ConsolidatedRationale;
            existing.MergedImprovementTipsJson = result.MergedImprovementTipsJson;
            _context.AggregatedResults.Update(existing);
        }
        else
        {
            await _context.AggregatedResults.AddAsync(result, ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<AggregatedResult?> GetAggregatedResultAsync(string applicationId, CancellationToken ct = default)
        => await _context.AggregatedResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ApplicationId == applicationId, ct);

    public async Task SetExtractionAsync(ExtractionArtifact extraction, CancellationToken ct = default)
    {
        var existing = await _context.ExtractionArtifacts
            .FirstOrDefaultAsync(e => e.ApplicationId == extraction.ApplicationId, ct);

        if (existing != null)
        {
            existing.NormalisedText = extraction.NormalisedText;
            existing.ConfidenceScore = extraction.ConfidenceScore;
            existing.Status = extraction.Status;
            _context.ExtractionArtifacts.Update(existing);
        }
        else
        {
            await _context.ExtractionArtifacts.AddAsync(extraction, ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<ExtractionArtifact?> GetExtractionAsync(string applicationId, CancellationToken ct = default)
        => await _context.ExtractionArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ApplicationId == applicationId, ct);

    public async Task SetManualReviewAsync(ManualReviewData review, CancellationToken ct = default)
    {
        var existing = await _context.ManualReviews
            .FirstOrDefaultAsync(r => r.ApplicationId == review.ApplicationId, ct);

        if (existing != null)
        {
            existing.RubricScoresJson = review.RubricScoresJson;
            existing.OverallComment = review.OverallComment;
            existing.AdjustedFinalScore = review.AdjustedFinalScore;
            existing.AuditTrailJson = review.AuditTrailJson;
            existing.UpdatedAt = DateTime.UtcNow;
            _context.ManualReviews.Update(existing);
        }
        else
        {
            await _context.ManualReviews.AddAsync(review, ct);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<ManualReviewData?> GetManualReviewAsync(string applicationId, CancellationToken ct = default)
        => await _context.ManualReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ApplicationId == applicationId, ct);
}
