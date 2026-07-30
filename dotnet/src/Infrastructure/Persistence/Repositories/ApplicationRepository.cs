using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TalentMatch.Domain.Entities;
using TalentMatch.Domain.Interfaces;

namespace TalentMatch.Infrastructure.Persistence.Repositories;

#pragma warning disable EF1002 // SQL values remain parameterized; interpolation is limited to trusted provider-specific table identifiers.
public class ApplicationRepository : IApplicationRepository
{
    private readonly AppDbContext _context;
    public ApplicationRepository(AppDbContext context) => _context = context;

    private string DocumentsTable => _context.Database.IsSqlServer()
        ? "[talentmatch].[ApplicationDocuments]"
        : "ApplicationDocuments";

    private string DocumentBlobsTable => _context.Database.IsSqlServer()
        ? "[talentmatch].[DocumentBlobs]"
        : "DocumentBlobs";

    public async Task<IReadOnlyList<TalentMatch.Domain.Entities.Application>> GetByJobIdAsync(string jobId, CancellationToken ct = default)
        => await _context.Applications
            .Where(a => a.JobId == jobId)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<TalentMatch.Domain.Entities.Application?> GetByIdAsync(string id, CancellationToken ct = default)
        => await _context.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task AddAsync(TalentMatch.Domain.Entities.Application application, CancellationToken ct = default)
    {
        await _context.Applications.AddAsync(application, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TalentMatch.Domain.Entities.Application application, CancellationToken ct = default)
    {
        var entry = _context.Entry(application);
        if (entry.State == EntityState.Detached)
        {
            // Merge detached values into an already-tracked row when present to avoid
            // duplicate-key tracking exceptions in high-parallel scoring flows.
            var tracked = _context.Applications.Local.FirstOrDefault(a => a.Id == application.Id);
            if (tracked != null)
            {
                _context.Entry(tracked).CurrentValues.SetValues(application);
            }
            else
            {
                _context.Applications.Attach(application);
                _context.Entry(application).State = EntityState.Modified;
            }
        }

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
        // SQL Server: use a schema-tolerant projection to avoid selecting optional columns
        // that may not exist on older shared schemas (for example BlobUri/ContentSha256).
        if (_context.Database.IsSqlServer())
        {
            var sqlServerDocuments = new List<ApplicationDocument>();
            var dbConnection = _context.Database.GetDbConnection();
            var closeWhenDone = dbConnection.State != ConnectionState.Open;

            if (closeWhenDone)
                await dbConnection.OpenAsync(ct);

            try
            {
                var documentColumns = await GetSqlServerTableColumnsAsync("talentmatch", "ApplicationDocuments", ct);
                var blobColumns = await GetSqlServerTableColumnsAsync("talentmatch", "DocumentBlobs", ct);

                var fileTypeExprParts = new List<string>();
                if (documentColumns.Contains("MimeType"))
                    fileTypeExprParts.Add("NULLIF(d.[MimeType], '')");
                if (documentColumns.Contains("FileType"))
                    fileTypeExprParts.Add("NULLIF(d.[FileType], '')");
                var fileTypeExpr = fileTypeExprParts.Count > 0
                    ? $"COALESCE({string.Join(", ", fileTypeExprParts)}, 'application/octet-stream')"
                    : "'application/octet-stream'";

                var hasSizeBytes = documentColumns.Contains("SizeBytes");
                var hasFileSize = documentColumns.Contains("FileSize");
                var fileSizeExpr = hasSizeBytes && hasFileSize
                    ? "CASE WHEN d.[SizeBytes] IS NOT NULL AND d.[SizeBytes] > 0 THEN d.[SizeBytes] ELSE ISNULL(d.[FileSize], 0) END"
                    : hasSizeBytes
                        ? "ISNULL(d.[SizeBytes], 0)"
                        : hasFileSize
                            ? "ISNULL(d.[FileSize], 0)"
                            : "0";

                var hasUploadedAt = documentColumns.Contains("UploadedAt");
                var hasUploadTimestamp = documentColumns.Contains("UploadTimestamp");
                var uploadedAtExpr = hasUploadedAt && hasUploadTimestamp
                    ? "COALESCE(d.[UploadedAt], d.[UploadTimestamp])"
                    : hasUploadedAt
                        ? "d.[UploadedAt]"
                        : hasUploadTimestamp
                            ? "d.[UploadTimestamp]"
                            : "NULL";

                var fingerprintExpr = documentColumns.Contains("Fingerprint")
                    ? "d.[Fingerprint]"
                    : "NULL";

                var canJoinBlobs = blobColumns.Contains("DocumentId") && blobColumns.Contains("Content");
                var hasInlineContent = documentColumns.Contains("ContentBase64");
                var contentExprParts = new List<string>();
                if (canJoinBlobs)
                    contentExprParts.Add("b.[Content]");
                if (hasInlineContent)
                    contentExprParts.Add("NULLIF(d.[ContentBase64], '')");
                var contentExpr = contentExprParts.Count switch
                {
                    0 => "NULL",
                    1 => contentExprParts[0],
                    _ => $"COALESCE({string.Join(", ", contentExprParts)})"
                };

                var blobJoinClause = canJoinBlobs
                    ? $"LEFT JOIN {DocumentBlobsTable} AS b ON b.[DocumentId] = d.[Id]"
                    : string.Empty;

                await using var command = dbConnection.CreateCommand();
                command.CommandText = $@"
                    SELECT
                        d.[Id],
                        d.[ApplicationId],
                        d.[FileName],
                        {fileTypeExpr} AS [EffectiveFileType],
                        {fileSizeExpr} AS [EffectiveFileSize],
                        {fingerprintExpr} AS [EffectiveFingerprint],
                        {uploadedAtExpr} AS [EffectiveUploadedAt],
                        {contentExpr} AS [EffectiveContentBase64]
                    FROM {DocumentsTable} AS d
                    {blobJoinClause}
                    WHERE d.[ApplicationId] = @applicationId
                    ORDER BY d.[Id];";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@applicationId";
                parameter.Value = applicationId;
                command.Parameters.Add(parameter);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    sqlServerDocuments.Add(new ApplicationDocument
                    {
                        Id = reader.GetString(0),
                        ApplicationId = reader.GetString(1),
                        FileName = reader.GetString(2),
                        FileType = reader.IsDBNull(3) ? "application/octet-stream" : reader.GetString(3),
                        FileSize = reader.IsDBNull(4) ? 0 : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                        Fingerprint = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        UploadTimestamp = ReadOptionalDateTime(reader, 6),
                        ContentBase64 = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }
            }
            finally
            {
                if (closeWhenDone)
                    await dbConnection.CloseAsync();
            }

            return sqlServerDocuments;
        }

        // Non-SQLite fallback (for non-SQL Server providers).
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

    public async Task SetDocumentBlobReferenceAsync(string documentId, string blobUri, string contentSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("documentId is required", nameof(documentId));
        if (string.IsNullOrWhiteSpace(blobUri)) throw new ArgumentException("blobUri is required", nameof(blobUri));

        if (_context.Database.IsSqlite())
        {
            var columns = await GetSqliteTableColumnsAsync("ApplicationDocuments", ct);
            var setClauses = new List<string> { "BlobUri = {0}", "ContentSha256 = {1}" };
            var args = new List<object> { blobUri, contentSha256 };
            if (columns.Contains("ContentBase64"))
            {
                setClauses.Add("ContentBase64 = NULL");
            }

            await _context.Database.ExecuteSqlRawAsync(
                $"UPDATE {DocumentsTable} SET {string.Join(", ", setClauses)} WHERE Id = {{{args.Count}}}",
                args.Concat(new object[] { documentId }).ToArray());
        }
        else
        {
            await _context.Database.ExecuteSqlRawAsync(
                $"UPDATE {DocumentsTable} SET BlobUri = {{0}}, ContentSha256 = {{1}} WHERE Id = {{2}}",
                blobUri, contentSha256, documentId);
        }

        // Canonical source for this row is now BlobUri, so remove DB-stored bytes.
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {DocumentBlobsTable} WHERE DocumentId = {{0}}",
            documentId);
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

    private static DateTime? ReadOptionalDateTime(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        if (value is DateTime dt)
            return dt;

        if (value is DateTimeOffset dto)
            return dto.UtcDateTime;

        return ParseOptionalDateTime(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private async Task<HashSet<string>> GetSqlServerTableColumnsAsync(string schemaName, string tableName, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = _context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT [COLUMN_NAME]
                FROM [INFORMATION_SCHEMA].[COLUMNS]
                WHERE [TABLE_SCHEMA] = @schemaName
                  AND [TABLE_NAME] = @tableName;";

            var schemaParam = command.CreateParameter();
            schemaParam.ParameterName = "@schemaName";
            schemaParam.Value = schemaName;
            command.Parameters.Add(schemaParam);

            var tableParam = command.CreateParameter();
            tableParam.ParameterName = "@tableName";
            tableParam.Value = tableName;
            command.Parameters.Add(tableParam);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                    columns.Add(reader.GetString(0));
            }
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }

        return columns;
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

    public async Task<ScoringRun?> GetScoringRunByIdAsync(string scoringRunId, CancellationToken ct = default)
        => await _context.ScoringRuns
            .FirstOrDefaultAsync(r => r.Id == scoringRunId, ct);

    public async Task UpdateScoringRunAsync(ScoringRun run, CancellationToken ct = default)
    {
        var entry = _context.Entry(run);
        if (entry.State == EntityState.Detached)
            _context.ScoringRuns.Update(run);
        await _context.SaveChangesAsync(ct);
    }

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
            existing.FinalSubScoresJson = result.FinalSubScoresJson;
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
        // Use key-only delete to avoid eager-loading related tables. This keeps deletion
        // resilient across shared-schema drift where optional columns may differ.
        var stub = new TalentMatch.Domain.Entities.Application { Id = id };
        _context.Applications.Attach(stub);
        _context.Applications.Remove(stub);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Already removed by another concurrent cleanup path.
        }
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
            existing.HumanEdited = review.HumanEdited;
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
#pragma warning restore EF1002
