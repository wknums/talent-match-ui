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
        document.UploadTimestamp ??= DateTime.UtcNow;

        if (_context.Database.IsSqlite())
        {
            await InsertSqliteDocumentAsync(document, blobContent, ct);
            return;
        }

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

    private async Task InsertSqliteDocumentAsync(ApplicationDocument document, string? blobContent, CancellationToken ct)
    {
        var docColumns = await GetSqliteTableColumnsAsync("ApplicationDocuments", ct);
        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            var columns = new List<string>();
            var values = new List<string>();

            void AddValue(string columnName, object value)
            {
                if (!docColumns.Contains(columnName))
                    return;

                var parameterName = $"${columnName}";
                columns.Add($"\"{columnName}\"");
                values.Add(parameterName);
                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterName;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var fileType = string.IsNullOrWhiteSpace(document.FileType)
                ? "application/octet-stream"
                : document.FileType;
            var uploadedAtInstant = document.UploadTimestamp ?? DateTime.UtcNow;
            document.UploadTimestamp = uploadedAtInstant;
            var uploadedAt = uploadedAtInstant.ToString("O", CultureInfo.InvariantCulture);

            AddValue("Id", document.Id);
            AddValue("ApplicationId", document.ApplicationId);
            AddValue("FileName", document.FileName);
            AddValue("MimeType", fileType);
            AddValue("FileType", fileType);
            AddValue("SizeBytes", document.FileSize);
            AddValue("FileSize", document.FileSize);
            AddValue("Fingerprint", document.Fingerprint);
            AddValue("UploadedAt", uploadedAt);
            AddValue("UploadTimestamp", uploadedAt);

            command.CommandText = $"INSERT INTO \"ApplicationDocuments\" ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
            await command.ExecuteNonQueryAsync(ct);

            if (!string.IsNullOrEmpty(blobContent))
            {
                var blobColumns = await GetSqliteTableColumnsAsync("DocumentBlobs", ct);
                if (blobColumns.Contains("DocumentId") && blobColumns.Contains("Content"))
                {
                    await using var blobCommand = connection.CreateCommand();
                    blobCommand.CommandText = "INSERT OR REPLACE INTO \"DocumentBlobs\" (\"DocumentId\", \"Content\") VALUES ($DocumentId, $Content);";

                    var idParameter = blobCommand.CreateParameter();
                    idParameter.ParameterName = "$DocumentId";
                    idParameter.Value = document.Id;
                    blobCommand.Parameters.Add(idParameter);

                    var contentParameter = blobCommand.CreateParameter();
                    contentParameter.ParameterName = "$Content";
                    contentParameter.Value = blobContent;
                    blobCommand.Parameters.Add(contentParameter);

                    await blobCommand.ExecuteNonQueryAsync(ct);
                }
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private async Task<HashSet<string>> GetSqliteTableColumnsAsync(string tableName, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                    columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return columns;
    }

    public async Task<IReadOnlyList<ApplicationDocument>> GetDocumentsAsync(string applicationId, CancellationToken ct = default)
    {
        // SQL Server: EF handles column mapping (MimeType→FileType, SizeBytes→FileSize, UploadedAt→UploadTimestamp).
        if (!_context.Database.IsSqlite())
        {
            var efDocs = await _context.ApplicationDocuments
                .Where(d => d.ApplicationId == applicationId)
                .OrderBy(d => d.Id)
                .AsNoTracking()
                .ToListAsync(ct);

            // Blob content lives in DocumentBlobs; load it in a second query and merge.
            var docIds = efDocs.Select(d => d.Id).ToList();
            var blobs = await _context.DocumentBlobs
                .Where(b => docIds.Contains(b.DocumentId))
                .AsNoTracking()
                .ToDictionaryAsync(b => b.DocumentId, b => b.Content, ct);

            foreach (var doc in efDocs)
            {
                if (blobs.TryGetValue(doc.Id, out var content))
                    doc.ContentBase64 = content;
            }

            return efDocs;
        }

        // SQLite: use raw SQL to handle dual column-name schema (legacy + new names).
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
        if (_context.Database.IsSqlite())
        {
            await UpsertSqliteExtractionAsync(extraction, ct);
            return;
        }

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

    private async Task UpsertSqliteExtractionAsync(ExtractionArtifact extraction, CancellationToken ct)
    {
        var columns = await GetSqliteTableColumnsAsync("ExtractionArtifacts", ct);
        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            string? existingId = null;
            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.CommandText = "SELECT \"Id\" FROM \"ExtractionArtifacts\" WHERE \"ApplicationId\" = $ApplicationId LIMIT 1;";
                var appIdParameter = selectCommand.CreateParameter();
                appIdParameter.ParameterName = "$ApplicationId";
                appIdParameter.Value = extraction.ApplicationId;
                selectCommand.Parameters.Add(appIdParameter);

                var value = await selectCommand.ExecuteScalarAsync(ct);
                existingId = value as string;
            }

            if (!string.IsNullOrWhiteSpace(existingId))
                extraction.Id = existingId;

            var normalisedText = extraction.NormalisedText ?? string.Empty;
            var createdAt = extraction.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
            var assignments = new List<string>();

            await using var command = connection.CreateCommand();

            void AddParam(string name, object value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            void AddAssignment(string columnName, string parameterName, object value)
            {
                if (!columns.Contains(columnName))
                    return;

                assignments.Add($"\"{columnName}\" = {parameterName}");
                AddParam(parameterName, value);
            }

            if (!string.IsNullOrWhiteSpace(existingId))
            {
                AddAssignment("Markdown", "$Markdown", normalisedText);
                AddAssignment("NormalisedText", "$NormalisedText", normalisedText);
                AddAssignment("Confidence", "$Confidence", extraction.ConfidenceScore);
                AddAssignment("ConfidenceScore", "$ConfidenceScore", extraction.ConfidenceScore);
                AddAssignment("Status", "$Status", extraction.Status);

                if (assignments.Count > 0)
                {
                    AddParam("$Id", extraction.Id);
                    command.CommandText = $"UPDATE \"ExtractionArtifacts\" SET {string.Join(", ", assignments)} WHERE \"Id\" = $Id;";
                    await command.ExecuteNonQueryAsync(ct);
                }

                return;
            }

            var insertColumns = new List<string>();
            var insertValues = new List<string>();

            void AddInsert(string columnName, string parameterName, object value)
            {
                if (!columns.Contains(columnName))
                    return;

                insertColumns.Add($"\"{columnName}\"");
                insertValues.Add(parameterName);
                AddParam(parameterName, value);
            }

            AddInsert("Id", "$Id", extraction.Id);
            AddInsert("ApplicationId", "$ApplicationId", extraction.ApplicationId);
            AddInsert("Markdown", "$Markdown", normalisedText);
            AddInsert("NormalisedText", "$NormalisedText", normalisedText);
            AddInsert("Confidence", "$Confidence", extraction.ConfidenceScore);
            AddInsert("ConfidenceScore", "$ConfidenceScore", extraction.ConfidenceScore);
            AddInsert("Status", "$Status", extraction.Status);
            AddInsert("CreatedAt", "$CreatedAt", createdAt);

            command.CommandText = $"INSERT INTO \"ExtractionArtifacts\" ({string.Join(", ", insertColumns)}) VALUES ({string.Join(", ", insertValues)});";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
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
