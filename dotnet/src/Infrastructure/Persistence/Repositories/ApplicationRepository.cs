using System.Data;
using System.Globalization;
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
        var entry = _context.Entry(application);
        if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            _context.Applications.Update(application);
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddDocumentAsync(ApplicationDocument document, CancellationToken ct = default)
    {
        var blobContent = document.ContentBase64;
        await _context.ApplicationDocuments.AddAsync(document, ct);
        if (!string.IsNullOrEmpty(blobContent))
        {
            await _context.DocumentBlobs.AddAsync(new DocumentBlob
            {
                DocumentId = document.Id,
                Content = blobContent
            }, ct);
        }
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationDocument>> GetDocumentsAsync(string applicationId, CancellationToken ct = default)
    {
        var documents = new List<ApplicationDocument>();
        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT
                    d.Id,
                    d.ApplicationId,
                    d.FileName,
                    COALESCE(NULLIF(d.MimeType, ''), NULLIF(d.FileType, ''), '') AS EffectiveFileType,
                    CASE
                        WHEN d.SizeBytes IS NOT NULL AND d.SizeBytes > 0 THEN d.SizeBytes
                        ELSE COALESCE(d.FileSize, 0)
                    END AS EffectiveFileSize,
                    d.Fingerprint,
                    COALESCE(NULLIF(d.UploadedAt, ''), NULLIF(d.UploadTimestamp, '')) AS EffectiveUploadedAt,
                    COALESCE(b.Content, NULLIF(d.ContentBase64, '')) AS EffectiveContentBase64
                FROM ApplicationDocuments AS d
                LEFT JOIN DocumentBlobs AS b ON b.DocumentId = d.Id
                WHERE d.ApplicationId = $applicationId
                ORDER BY d.Id;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "$applicationId";
            parameter.Value = applicationId;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                documents.Add(new ApplicationDocument
                {
                    Id = reader.GetString(0),
                    ApplicationId = reader.GetString(1),
                    FileName = reader.GetString(2),
                    FileType = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    FileSize = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    Fingerprint = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    UploadTimestamp = ParseOptionalDateTime(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    ContentBase64 = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return documents;
    }

    private static DateTime? ParseOptionalDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        if (DateTime.TryParse(value, out parsed))
            return parsed;

        return null;
    }

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

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var app = await _context.Applications
            .Include(a => a.Documents)
            .Include(a => a.ScoringRuns)
            .Include(a => a.AggregatedResult)
            .Include(a => a.ManualReview)
            .Include(a => a.Extraction)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app == null) return;
        _context.Applications.Remove(app);
        await _context.SaveChangesAsync(ct);
    }

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
