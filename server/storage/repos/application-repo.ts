import { randomUUID } from 'node:crypto'
import { Buffer } from 'node:buffer'
import { getPool, isAzureSql, sql } from '../db.js'
import { SCHEMA_NAME, T } from '../table-names.js'
import type {
  Application, ApplicationDocument, ExtractionArtifact,
  ScoringRun, AggregatedResult, ManualReviewData
} from '../../../src/types/index.js'

const applicationDocumentsColumnCache = new Map<string, boolean>()

async function hasApplicationDocumentsColumn(columnName: string): Promise<boolean> {
  if (!isAzureSql) {
    return true
  }

  const cached = applicationDocumentsColumnCache.get(columnName)
  if (cached !== undefined) {
    return cached
  }

  const pool = await getPool()
  const result = await pool.request()
    .input('schemaName', sql.NVarChar, SCHEMA_NAME)
    .input('tableName', sql.NVarChar, 'ApplicationDocuments')
    .input('columnName', sql.NVarChar, columnName)
    .query(`SELECT 1 AS ExistsFlag
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schemaName AND t.name = @tableName AND c.name = @columnName`)

  const exists = result.recordset.length > 0
  applicationDocumentsColumnCache.set(columnName, exists)
  return exists
}

function parseJson<T>(val: string | null | undefined, fallback: T): T {
  if (!val) return fallback
  try { return JSON.parse(val) } catch { return fallback }
}

function rowToApplication(r: any): Application {
  return {
    applicationId: r.Id,
    jobId: r.JobId,
    candidateRef: r.CandidateRef ?? `candidate-${String(r.Id).slice(0, 8)}`,
    candidateName: r.CandidateName ?? undefined,
    candidateEmail: r.CandidateEmail ?? undefined,
    status: r.Status,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    documents: [],
    finalScore: r.FinalScore ?? undefined,
    finalDecision: r.FinalDecision ?? undefined,
    variance: r.Variance ?? undefined,
    flagged: r.Flagged === true || r.Flagged === 1,
    testRunId: r.TestRunId ?? undefined,
  }
}

function rowToDocument(r: any): ApplicationDocument {
  return {
    documentId: r.Id,
    applicationId: r.ApplicationId,
    fileName: r.FileName,
    mimeType: r.MimeType || r.FileType || 'application/octet-stream',
    sizeBytes: r.SizeBytes ?? r.FileSize,
    sha256: r.Fingerprint,
    uploadedAt: r.UploadedAt?.toISOString?.() ?? r.UploadedAt ?? r.UploadTimestamp?.toISOString?.() ?? r.UploadTimestamp,
    blobUri: r.BlobUri ?? undefined,
    contentSha256: r.ContentSha256 ?? undefined,
  }
}

async function downloadBlobAsBase64(blobUri: string): Promise<string | undefined> {
  const trimmed = (blobUri ?? '').trim()
  if (!trimmed) return undefined

  try {
    const { BlobClient } = await import('@azure/storage-blob')
    const { DefaultAzureCredential } = await import('@azure/identity')

    const client = new BlobClient(trimmed, new DefaultAzureCredential())
    const response = await client.download()
    const stream = response.readableStreamBody
    if (!stream) return undefined

    const chunks: Buffer[] = []
    for await (const chunk of stream as any) {
      chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk))
    }
    return Buffer.concat(chunks).toString('base64')
  } catch (err) {
    console.warn(`[applicationRepo] Failed to download BlobUri ${trimmed}; falling back to DB content:`, err)
    return undefined
  }
}

function rowToScoringRun(r: any): ScoringRun {
  return {
    runId: r.Id,
    applicationId: r.ApplicationId,
    versionId: r.VersionId ?? '',
    runIndex: r.RunIndex,
    modelDeploymentId: r.ModelDeploymentId ?? r.AiModelId ?? '',
    promptVersionId: r.PromptVersionId ?? r.PromptVersion ?? '',
    overallScore: r.OverallScore ?? r.TotalScore,
    subScores: parseJson(r.SubScoresJson ?? r.CategoryScoresJson, {}),
    mustHaveResult: parseJson(r.MustHaveResultJson ?? r.MustHaveEvaluationJson, { passed: true, missingCriteria: [], details: {} }),
    evidenceCitations: parseJson(r.EvidenceCitationsJson, []),
    rationale: r.Rationale ?? '',
    improvementRecommendations: parseJson(r.ImprovementRecsJson ?? r.ImprovementTipsJson, []),
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    durationMs: r.DurationMs ?? 0,
    tokenUsage: r.TokenUsageJson
      ? parseJson(r.TokenUsageJson, undefined)
      : {
          promptTokens: r.InputTokens ?? 0,
          completionTokens: r.OutputTokens ?? 0,
          totalTokens: (r.InputTokens ?? 0) + (r.OutputTokens ?? 0),
        },
    status: r.Status ?? 'Success',
    rawResponseText: r.RawResponseText ?? undefined,
    rawParsedResponse: parseJson(r.RawParsedResponseJson, undefined),
    parserWarnings: parseJson(r.ParserWarningsJson, undefined),
    parserConfidence: r.ParserConfidence ?? undefined,
  }
}

function rowToAggregatedResult(r: any): AggregatedResult {
  return {
    resultId: r.Id,
    applicationId: r.ApplicationId,
    versionId: r.VersionId ?? '',
    finalScore: r.FinalScore,
    finalSubScores: parseJson(r.FinalSubScoresJson, {}),
    confidence: r.Confidence,
    variance: r.Variance,
    finalDecision: r.FinalDecision ?? r.Decision,
    rationaleText: r.RationaleText ?? r.ConsolidatedRationale,
    recommendationsText: r.RecommendationsText ?? parseJson(r.MergedImprovementTipsJson, []).join('; '),
    allRuns: parseJson(r.AllRunsJson, []),
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
  }
}

function rowToExtraction(r: any): ExtractionArtifact {
  return {
    artifactId: r.Id,
    applicationId: r.ApplicationId,
    markdown: r.Markdown ?? r.NormalisedText,
    extractionMetadata: {
      toolVersion: r.ToolVersion ?? 'stack-b',
      confidence: r.Confidence ?? r.ConfidenceScore ?? 0,
      extractedAt: r.ExtractedAt?.toISOString?.() ?? r.ExtractedAt ?? r.CreatedAt?.toISOString?.() ?? r.CreatedAt ?? '',
    },
    status: r.Status === 'completed' ? 'Success' : r.Status === 'failed' ? 'Failed' : r.Status,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
  }
}

function rowToManualReview(r: any): ManualReviewData {
  return {
    applicationId: r.ApplicationId,
    jobId: r.JobId ?? '',
    rubricScores: parseJson(r.RubricScoresJson, {}),
    overallComment: r.OverallComment,
    adjustedFinalScore: r.AdjustedFinalScore ?? undefined,
    auditTrail: parseJson(r.AuditTrailJson, []),
    humanEdited: r.HumanEdited === true || Number(r.HumanEdited ?? 0) === 1,
    lastModifiedAt: r.UpdatedAt?.toISOString?.() ?? r.UpdatedAt,
    lastModifiedBy: r.LastModifiedBy ?? '',
  }
}

export const applicationRepo = {
  async getByJobId(jobId: string, options?: { includeTestCases?: boolean; status?: string }): Promise<Application[]> {
    const pool = await getPool()
    const req = pool.request().input('jobId', sql.NVarChar, jobId)
    let where = 'WHERE JobId = @jobId'
    if (!options?.includeTestCases) where += ' AND TestRunId IS NULL'
    if (options?.status) { where += ' AND Status = @status'; req.input('status', sql.NVarChar, options.status) }
    const result = await req.query(`SELECT * FROM ${T('Applications')} ${where} ORDER BY CreatedAt DESC`)
    return result.recordset.map(rowToApplication)
  },

  async getByJobIdAll(jobId: string): Promise<Application[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('Applications')} WHERE JobId = @jobId ORDER BY CreatedAt DESC`)
    return result.recordset.map(rowToApplication)
  },

  async getByTestRunId(testRunId: string): Promise<Application[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('testRunId', sql.NVarChar, testRunId)
      .query(`SELECT * FROM ${T('Applications')} WHERE TestRunId = @testRunId ORDER BY CreatedAt DESC`)
    return result.recordset.map(rowToApplication)
  },

  async getById(applicationId: string): Promise<Application | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('Applications')} WHERE Id = @id`)
    return result.recordset[0] ? rowToApplication(result.recordset[0]) : undefined
  },

  async delete(applicationId: string): Promise<void> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()
    try {
      const documentIdsResult = await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`SELECT Id FROM ${T('ApplicationDocuments')} WHERE ApplicationId = @applicationId`)
      const documentIds = documentIdsResult.recordset.map((row: any) => row.Id as string)

      if (documentIds.length > 0) {
        const deleteBlobRequest = txn.request()
        const placeholders = documentIds.map((id: string, index: number) => {
          deleteBlobRequest.input(`doc${index}`, sql.NVarChar, id)
          return `@doc${index}`
        })
        await deleteBlobRequest.query(`DELETE FROM ${T('DocumentBlobs')} WHERE DocumentId IN (${placeholders.join(',')})`)
      }

      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('ApplicationDocuments')} WHERE ApplicationId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('ExtractionArtifacts')} WHERE ApplicationId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('ScoringRuns')} WHERE ApplicationId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('AggregatedResults')} WHERE ApplicationId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('ManualReviews')} WHERE ApplicationId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('FailureQueueItems')} WHERE ApplicationId = @applicationId OR EntityId = @applicationId`)
      await txn.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('Applications')} WHERE Id = @applicationId`)

      await txn.commit()
    } catch (err) {
      await txn.rollback()
      throw err
    }
  },

  async create(app: Application): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, app.applicationId)
      .input('jobId', sql.NVarChar, app.jobId)
      .input('candidateRef', sql.NVarChar, app.candidateRef)
      .input('candidateName', sql.NVarChar, app.candidateName ?? null)
      .input('candidateEmail', sql.NVarChar, app.candidateEmail ?? null)
      .input('status', sql.NVarChar, app.status)
      .input('createdAt', sql.DateTime2, new Date(app.createdAt))
            .input('updatedAt', sql.DateTime2, new Date(app.createdAt))
            .input('flagged', sql.Bit, app.flagged ? 1 : 0)
      .input('testRunId', sql.NVarChar, app.testRunId ?? null)
            .query(`INSERT INTO ${T('Applications')} (Id, JobId, CandidateRef, CandidateName, CandidateEmail, Status, CreatedAt, UpdatedAt, Flagged, TestRunId)
              VALUES (@id, @jobId, @candidateRef, @candidateName, @candidateEmail, @status, @createdAt, @updatedAt, @flagged, @testRunId)`)
  },

  async updateStatus(applicationId: string, status: string, extra?: { finalScore?: number; finalDecision?: string; variance?: number; flagged?: boolean; lastError?: string }): Promise<void> {
    const pool = await getPool()
    const req = pool.request()
      .input('id', sql.NVarChar, applicationId)
      .input('status', sql.NVarChar, status)
    let sets = 'Status = @status, UpdatedAt = SYSUTCDATETIME()'
    if (extra?.finalScore !== undefined) { sets += ', FinalScore = @finalScore'; req.input('finalScore', sql.Float, extra.finalScore) }
    if (extra?.finalDecision !== undefined) { sets += ', FinalDecision = @finalDecision'; req.input('finalDecision', sql.NVarChar, extra.finalDecision) }
    if (extra?.variance !== undefined) { sets += ', Variance = @variance'; req.input('variance', sql.Float, extra.variance) }
    if (extra?.flagged !== undefined) { sets += ', Flagged = @flagged'; req.input('flagged', sql.Bit, extra.flagged ? 1 : 0) }
    if (extra?.lastError !== undefined) { sets += ', LastError = @lastError'; req.input('lastError', sql.NVarChar, extra.lastError) }
    await req.query(`UPDATE ${T('Applications')} SET ${sets} WHERE Id = @id`)
  },

  async resetForRescore(applicationId: string): Promise<void> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()
    try {
      await txn.request().input('id', sql.NVarChar, applicationId)
        .query(`UPDATE ${T('Applications')} SET Status = 'Queued', FinalScore = NULL, FinalDecision = NULL, Variance = NULL, Flagged = 0, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id`)
      await txn.request().input('id', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('ScoringRuns')} WHERE ApplicationId = @id`)
      await txn.request().input('id', sql.NVarChar, applicationId)
        .query(`DELETE FROM ${T('AggregatedResults')} WHERE ApplicationId = @id`)
      await txn.commit()
    } catch (err) { await txn.rollback(); throw err }
  },

  async bulkResetFailed(jobId: string): Promise<string[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT Id FROM ${T('Applications')} WHERE JobId = @jobId AND TestRunId IS NULL AND Status IN ('ScoringFailed','ExtractionFailed')`)
    const ids = result.recordset.map((r: any) => r.Id as string)
    if (ids.length === 0) return []
    // Reset status
    await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`UPDATE ${T('Applications')} SET Status = 'Queued', UpdatedAt = SYSUTCDATETIME() WHERE JobId = @jobId AND TestRunId IS NULL AND Status IN ('ScoringFailed','ExtractionFailed')`)
    return ids
  },

  // Documents
  async createDocument(doc: ApplicationDocument): Promise<void> {
    const pool = await getPool()
    const hasFileType = await hasApplicationDocumentsColumn('FileType')
    const hasFileSize = await hasApplicationDocumentsColumn('FileSize')
    const hasContentBase64 = await hasApplicationDocumentsColumn('ContentBase64')
    const hasUploadTimestamp = await hasApplicationDocumentsColumn('UploadTimestamp')

    const columns = ['Id', 'ApplicationId', 'FileName', 'MimeType', 'SizeBytes', 'Fingerprint', 'UploadedAt']
    const values = ['@id', '@applicationId', '@fileName', '@mimeType', '@sizeBytes', '@fingerprint', '@uploadedAt']
    if (hasFileType) {
      columns.push('FileType')
      values.push('@mimeType')
    }
    if (hasFileSize) {
      columns.push('FileSize')
      values.push('@sizeBytes')
    }
    if (hasContentBase64) {
      columns.push('ContentBase64')
      values.push("''")
    }
    if (hasUploadTimestamp) {
      columns.push('UploadTimestamp')
      values.push('@uploadedAt')
    }

    await pool.request()
      .input('id', sql.NVarChar, doc.documentId)
      .input('applicationId', sql.NVarChar, doc.applicationId)
      .input('fileName', sql.NVarChar, doc.fileName)
      .input('mimeType', sql.NVarChar, doc.mimeType)
      .input('sizeBytes', sql.BigInt, doc.sizeBytes)
      .input('fingerprint', sql.NVarChar, doc.sha256)
      .input('uploadedAt', sql.DateTime2, new Date(doc.uploadedAt))
      .query(`INSERT INTO ${T('ApplicationDocuments')} (${columns.join(', ')})
              VALUES (${values.join(', ')})`)
  },

  async getDocuments(applicationId: string): Promise<ApplicationDocument[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('ApplicationDocuments')} WHERE ApplicationId = @applicationId`)
    return result.recordset.map(rowToDocument)
  },

  async getDocumentById(documentId: string): Promise<ApplicationDocument | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, documentId)
      .query(`SELECT * FROM ${T('ApplicationDocuments')} WHERE Id = @id`)
    return result.recordset[0] ? rowToDocument(result.recordset[0]) : undefined
  },

  async findDuplicateFingerprint(jobId: string, fingerprint: string): Promise<ApplicationDocument | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .input('fingerprint', sql.NVarChar, fingerprint)
      .query(`SELECT d.* FROM ${T('ApplicationDocuments')} d
              JOIN ${T('Applications')} a ON a.Id = d.ApplicationId
              WHERE a.JobId = @jobId AND d.Fingerprint = @fingerprint`)
    return result.recordset[0] ? rowToDocument(result.recordset[0]) : undefined
  },

  // Document blobs
  async storeBlob(documentId: string, content: string): Promise<void> {
    const pool = await getPool()
    const updateResult = await pool.request()
      .input('documentId', sql.NVarChar, documentId)
      .input('content', sql.NVarChar, content)
      .query(`UPDATE ${T('DocumentBlobs')} SET Content = @content WHERE DocumentId = @documentId`)
    if ((updateResult.rowsAffected?.[0] ?? 0) === 0) {
      await pool.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('content', sql.NVarChar, content)
        .query(`INSERT INTO ${T('DocumentBlobs')} (DocumentId, Content) VALUES (@documentId, @content)`)
    }

    if (await hasApplicationDocumentsColumn('ContentBase64')) {
      await pool.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('content', sql.NVarChar, content)
        .query(`UPDATE ${T('ApplicationDocuments')} SET ContentBase64 = @content WHERE Id = @documentId`)
    }
  },

  async setDocumentBlobReference(documentId: string, blobUri: string, contentSha256: string): Promise<void> {
    const pool = await getPool()

    // Canonical bytes are now the blob object for this row; remove DB-stored bytes
    // to avoid duplicate storage across Azure Blob and DB blob columns.
    const req = pool.request()
      .input('documentId', sql.NVarChar, documentId)
      .input('blobUri', sql.NVarChar, blobUri)
      .input('contentSha256', sql.NVarChar, contentSha256)

    try {
      await req.query(`UPDATE ${T('ApplicationDocuments')}
                       SET BlobUri = @blobUri, ContentSha256 = @contentSha256, ContentBase64 = NULL
                       WHERE Id = @documentId`)
    } catch {
      await pool.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('blobUri', sql.NVarChar, blobUri)
        .input('contentSha256', sql.NVarChar, contentSha256)
        .query(`UPDATE ${T('ApplicationDocuments')}
                SET BlobUri = @blobUri, ContentSha256 = @contentSha256
                WHERE Id = @documentId`)
    }

    await pool.request()
      .input('documentId', sql.NVarChar, documentId)
      .query(`DELETE FROM ${T('DocumentBlobs')} WHERE DocumentId = @documentId`)
  },

  async getBlob(documentId: string): Promise<string | undefined> {
    const pool = await getPool()
    const hasContentBase64 = await hasApplicationDocumentsColumn('ContentBase64')

    const documentMeta = await pool.request()
      .input('documentId', sql.NVarChar, documentId)
      .query(`SELECT BlobUri${hasContentBase64 ? ', ContentBase64' : ''}
              FROM ${T('ApplicationDocuments')}
              WHERE Id = @documentId`)

    const blobUri = documentMeta.recordset[0]?.BlobUri as string | undefined
    const contentBase64 = hasContentBase64
      ? (documentMeta.recordset[0]?.ContentBase64 as string | undefined)
      : undefined

    // C1: prefer BlobUri when present so platform-created records are readable
    // in sequential code paths without duplicating bytes in DB.
    if (blobUri) {
      const downloaded = await downloadBlobAsBase64(blobUri)
      if (downloaded) return downloaded
    }

    const blobResult = await pool.request()
      .input('documentId', sql.NVarChar, documentId)
      .query(`SELECT Content FROM ${T('DocumentBlobs')} WHERE DocumentId = @documentId`)
    const blobContent = blobResult.recordset[0]?.Content
    if (blobContent) {
      return blobContent
    }

    if (!contentBase64) {
      return undefined
    }

    // Keep legacy read acceleration only when BlobUri is absent. When BlobUri is
    // present, avoid re-materializing DocumentBlobs to prevent duplicate byte storage.
    if (!blobUri) {
      await pool.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('content', sql.NVarChar, contentBase64)
        .query(`INSERT INTO ${T('DocumentBlobs')} (DocumentId, Content)
                SELECT @documentId, @content
                WHERE NOT EXISTS (SELECT 1 FROM ${T('DocumentBlobs')} WHERE DocumentId = @documentId)`)
    }

    return contentBase64
  },

  // Extraction artifacts
  async setExtraction(artifact: ExtractionArtifact): Promise<void> {
    const pool = await getPool()
    const extractionLegacyUpdateSql = isAzureSql ? '' : ', NormalisedText = @markdown, ConfidenceScore = @confidence'
    const extractionLegacyInsertColumnsSql = isAzureSql ? '' : ', NormalisedText, ConfidenceScore'
    const extractionLegacyInsertValuesSql = isAzureSql ? '' : ', @markdown, @confidence'
    const updateResult = await pool.request()
      .input('id', sql.NVarChar, artifact.artifactId)
      .input('applicationId', sql.NVarChar, artifact.applicationId)
      .input('markdown', sql.NVarChar, artifact.markdown)
      .input('toolVersion', sql.NVarChar, artifact.extractionMetadata.toolVersion)
      .input('confidence', sql.Float, artifact.extractionMetadata.confidence)
      .input('extractedAt', sql.DateTime2, artifact.extractionMetadata.extractedAt ? new Date(artifact.extractionMetadata.extractedAt) : null)
      .input('status', sql.NVarChar, artifact.status)
      .input('createdAt', sql.DateTime2, new Date(artifact.createdAt))
      .query(`UPDATE ${T('ExtractionArtifacts')}
              SET Markdown = @markdown, ToolVersion = @toolVersion, Confidence = @confidence, ExtractedAt = @extractedAt,
              Status = @status${extractionLegacyUpdateSql}
              WHERE ApplicationId = @applicationId`)
    if ((updateResult.rowsAffected?.[0] ?? 0) === 0) {
      await pool.request()
        .input('id', sql.NVarChar, artifact.artifactId)
        .input('applicationId', sql.NVarChar, artifact.applicationId)
        .input('markdown', sql.NVarChar, artifact.markdown)
        .input('toolVersion', sql.NVarChar, artifact.extractionMetadata.toolVersion)
        .input('confidence', sql.Float, artifact.extractionMetadata.confidence)
        .input('extractedAt', sql.DateTime2, artifact.extractionMetadata.extractedAt ? new Date(artifact.extractionMetadata.extractedAt) : null)
        .input('status', sql.NVarChar, artifact.status)
        .input('createdAt', sql.DateTime2, new Date(artifact.createdAt))
        .query(`INSERT INTO ${T('ExtractionArtifacts')} (
          Id, ApplicationId, Markdown, ToolVersion, Confidence, ExtractedAt, Status, CreatedAt${extractionLegacyInsertColumnsSql})
                VALUES (
          @id, @applicationId, @markdown, @toolVersion, @confidence, @extractedAt, @status, @createdAt${extractionLegacyInsertValuesSql})`)
    }
  },

  async getExtraction(applicationId: string): Promise<ExtractionArtifact | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('ExtractionArtifacts')} WHERE ApplicationId = @applicationId`)
    return result.recordset[0] ? rowToExtraction(result.recordset[0]) : undefined
  },

  // Scoring runs
  async addScoringRun(run: ScoringRun): Promise<void> {
    const pool = await getPool()
    const promptTokens = run.tokenUsage?.promptTokens ?? 0
    const completionTokens = run.tokenUsage?.completionTokens ?? 0
    const req = pool.request()
      .input('id', sql.NVarChar, run.runId)
      .input('applicationId', sql.NVarChar, run.applicationId)
      .input('versionId', sql.NVarChar, run.versionId)
      .input('runIndex', sql.Int, run.runIndex)
      .input('modelDeploymentId', sql.NVarChar, run.modelDeploymentId)
      .input('promptVersionId', sql.NVarChar, run.promptVersionId)
      .input('overallScore', sql.Float, run.overallScore)
      .input('subScoresJson', sql.NVarChar, JSON.stringify(run.subScores))
      .input('mustHaveResultJson', sql.NVarChar, JSON.stringify(run.mustHaveResult))
      .input('evidenceCitationsJson', sql.NVarChar, JSON.stringify(run.evidenceCitations))
      .input('rationale', sql.NVarChar, run.rationale)
      .input('improvementRecsJson', sql.NVarChar, JSON.stringify(run.improvementRecommendations))
      .input('createdAt', sql.DateTime2, new Date(run.createdAt))
      .input('durationMs', sql.Int, run.durationMs)
      .input('tokenUsageJson', sql.NVarChar, run.tokenUsage ? JSON.stringify(run.tokenUsage) : null)
            .input('inputTokens', sql.Int, promptTokens)
            .input('outputTokens', sql.Int, completionTokens)
      .input('status', sql.NVarChar, run.status)
      .input('rawResponseText', sql.NVarChar, run.rawResponseText ?? null)
      .input('rawParsedResponseJson', sql.NVarChar, run.rawParsedResponse ? JSON.stringify(run.rawParsedResponse) : null)
      .input('parserWarningsJson', sql.NVarChar, run.parserWarnings ? JSON.stringify(run.parserWarnings) : null)
      .input('parserConfidence', sql.Float, run.parserConfidence ?? null)

    if (isAzureSql) {
      await req.query(`INSERT INTO ${T('ScoringRuns')} (
        Id, ApplicationId, VersionId, RunIndex, ModelDeploymentId, PromptVersionId,
        OverallScore, SubScoresJson, MustHaveResultJson, EvidenceCitationsJson, Rationale, ImprovementRecsJson,
        CreatedAt, DurationMs, TokenUsageJson, Status, RawResponseText,
        RawParsedResponseJson, ParserWarningsJson, ParserConfidence)
        VALUES (
        @id, @applicationId, @versionId, @runIndex, @modelDeploymentId, @promptVersionId,
        @overallScore, @subScoresJson, @mustHaveResultJson, @evidenceCitationsJson, @rationale, @improvementRecsJson,
        @createdAt, @durationMs, @tokenUsageJson, @status, @rawResponseText,
        @rawParsedResponseJson, @parserWarningsJson, @parserConfidence)`)
      return
    }

    await req.query(`INSERT INTO ${T('ScoringRuns')} (
      Id, ApplicationId, VersionId, RunIndex, ModelDeploymentId, PromptVersionId,
      OverallScore, SubScoresJson, MustHaveResultJson, EvidenceCitationsJson, Rationale, ImprovementRecsJson,
      TotalScore, CategoryScoresJson, MustHaveEvaluationJson, ImprovementTipsJson, AiModelId, PromptVersion,
      InputTokens, OutputTokens, CreatedAt, DurationMs, TokenUsageJson, Status, RawResponseText,
      RawParsedResponseJson, ParserWarningsJson, ParserConfidence)
      VALUES (
      @id, @applicationId, @versionId, @runIndex, @modelDeploymentId, @promptVersionId,
      @overallScore, @subScoresJson, @mustHaveResultJson, @evidenceCitationsJson, @rationale, @improvementRecsJson,
      @overallScore, @subScoresJson, @mustHaveResultJson, @improvementRecsJson, @modelDeploymentId, @promptVersionId,
      @inputTokens, @outputTokens, @createdAt, @durationMs, @tokenUsageJson, @status, @rawResponseText,
      @rawParsedResponseJson, @parserWarningsJson, @parserConfidence)`)
  },

  async getScoringRuns(applicationId: string): Promise<ScoringRun[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('ScoringRuns')} WHERE ApplicationId = @applicationId ORDER BY RunIndex`)
    return result.recordset.map(rowToScoringRun)
  },

  async deleteScoringRuns(applicationId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`DELETE FROM ${T('ScoringRuns')} WHERE ApplicationId = @applicationId`)
  },

  // Aggregated result
  async setAggregatedResult(result: AggregatedResult): Promise<void> {
    const pool = await getPool()
    const mergedImprovementTipsJson = JSON.stringify(
      result.recommendationsText
        .split(';')
        .map(part => part.trim())
        .filter(Boolean),
    )
    const aggregatedLegacyUpdateSql = isAzureSql
      ? ''
      : ', Decision = @finalDecision, ConsolidatedRationale = @rationaleText, MergedImprovementTipsJson = @mergedImprovementTipsJson'
    const aggregatedLegacyInsertColumnsSql = isAzureSql
      ? ''
      : ', Decision, ConsolidatedRationale, MergedImprovementTipsJson'
    const aggregatedLegacyInsertValuesSql = isAzureSql
      ? ''
      : ', @finalDecision, @rationaleText, @mergedImprovementTipsJson'

    const updateResult = await pool.request()
      .input('id', sql.NVarChar, result.resultId)
      .input('applicationId', sql.NVarChar, result.applicationId)
      .input('versionId', sql.NVarChar, result.versionId)
      .input('finalScore', sql.Float, result.finalScore)
      .input('finalSubScoresJson', sql.NVarChar, JSON.stringify(result.finalSubScores))
      .input('confidence', sql.Float, result.confidence)
      .input('variance', sql.Float, result.variance)
      .input('finalDecision', sql.NVarChar, result.finalDecision)
      .input('rationaleText', sql.NVarChar, result.rationaleText)
      .input('recommendationsText', sql.NVarChar, result.recommendationsText)
      .input('allRunsJson', sql.NVarChar, JSON.stringify(result.allRuns))
      .input('mergedImprovementTipsJson', sql.NVarChar, mergedImprovementTipsJson)
      .input('createdAt', sql.DateTime2, new Date(result.createdAt))
      .query(`UPDATE ${T('AggregatedResults')}
              SET VersionId = @versionId, FinalScore = @finalScore, FinalSubScoresJson = @finalSubScoresJson,
                  Confidence = @confidence, Variance = @variance, FinalDecision = @finalDecision,
                  RationaleText = @rationaleText, RecommendationsText = @recommendationsText,
                  AllRunsJson = @allRunsJson${aggregatedLegacyUpdateSql}
              WHERE ApplicationId = @applicationId`)
    if ((updateResult.rowsAffected?.[0] ?? 0) === 0) {
      await pool.request()
        .input('id', sql.NVarChar, result.resultId)
        .input('applicationId', sql.NVarChar, result.applicationId)
        .input('versionId', sql.NVarChar, result.versionId)
        .input('finalScore', sql.Float, result.finalScore)
        .input('finalSubScoresJson', sql.NVarChar, JSON.stringify(result.finalSubScores))
        .input('confidence', sql.Float, result.confidence)
        .input('variance', sql.Float, result.variance)
        .input('finalDecision', sql.NVarChar, result.finalDecision)
        .input('rationaleText', sql.NVarChar, result.rationaleText)
        .input('recommendationsText', sql.NVarChar, result.recommendationsText)
        .input('allRunsJson', sql.NVarChar, JSON.stringify(result.allRuns))
        .input('mergedImprovementTipsJson', sql.NVarChar, mergedImprovementTipsJson)
        .input('createdAt', sql.DateTime2, new Date(result.createdAt))
        .query(`INSERT INTO ${T('AggregatedResults')} (
                Id, ApplicationId, VersionId, FinalScore, FinalSubScoresJson, Confidence, Variance,
          FinalDecision, RationaleText, RecommendationsText,
          AllRunsJson, CreatedAt${aggregatedLegacyInsertColumnsSql})
                VALUES (
                @id, @applicationId, @versionId, @finalScore, @finalSubScoresJson, @confidence, @variance,
          @finalDecision, @rationaleText, @recommendationsText,
          @allRunsJson, @createdAt${aggregatedLegacyInsertValuesSql})`)
    }
  },

  async getAggregatedResult(applicationId: string): Promise<AggregatedResult | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('AggregatedResults')} WHERE ApplicationId = @applicationId`)
    return result.recordset[0] ? rowToAggregatedResult(result.recordset[0]) : undefined
  },

  async deleteAggregatedResult(applicationId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`DELETE FROM ${T('AggregatedResults')} WHERE ApplicationId = @applicationId`)
  },

  // Manual review
  async setManualReview(review: ManualReviewData): Promise<void> {
    const pool = await getPool()
    const now = new Date().toISOString()
    const updateResult = await pool.request()
      .input('applicationId', sql.NVarChar, review.applicationId)
      .input('jobId', sql.NVarChar, review.jobId)
      .input('rubricScoresJson', sql.NVarChar, JSON.stringify(review.rubricScores))
      .input('overallComment', sql.NVarChar, review.overallComment)
      .input('adjustedFinalScore', sql.Float, review.adjustedFinalScore ?? null)
      .input('auditTrailJson', sql.NVarChar, JSON.stringify(review.auditTrail))
      .input('humanEdited', sql.Bit, review.humanEdited ? 1 : 0)
      .input('lastModifiedBy', sql.NVarChar, review.lastModifiedBy)
      .input('updatedAt', sql.DateTime2, new Date(now))
      .query(`UPDATE ${T('ManualReviews')}
              SET JobId = @jobId, RubricScoresJson = @rubricScoresJson, OverallComment = @overallComment,
                  AdjustedFinalScore = @adjustedFinalScore, AuditTrailJson = @auditTrailJson,
                  HumanEdited = @humanEdited, UpdatedAt = @updatedAt, LastModifiedBy = @lastModifiedBy
              WHERE ApplicationId = @applicationId`)
    if ((updateResult.rowsAffected?.[0] ?? 0) === 0) {
      await pool.request()
        .input('id', sql.NVarChar, randomUUID())
        .input('applicationId', sql.NVarChar, review.applicationId)
        .input('jobId', sql.NVarChar, review.jobId)
        .input('rubricScoresJson', sql.NVarChar, JSON.stringify(review.rubricScores))
        .input('overallComment', sql.NVarChar, review.overallComment)
        .input('adjustedFinalScore', sql.Float, review.adjustedFinalScore ?? null)
        .input('auditTrailJson', sql.NVarChar, JSON.stringify(review.auditTrail))
        .input('humanEdited', sql.Bit, review.humanEdited ? 1 : 0)
        .input('lastModifiedBy', sql.NVarChar, review.lastModifiedBy)
        .input('createdAt', sql.DateTime2, new Date(now))
        .input('updatedAt', sql.DateTime2, new Date(now))
        .query(`INSERT INTO ${T('ManualReviews')} (
                Id, ApplicationId, JobId, RubricScoresJson, OverallComment, AdjustedFinalScore,
                AuditTrailJson, HumanEdited, CreatedAt, UpdatedAt, LastModifiedBy)
                VALUES (
                @id, @applicationId, @jobId, @rubricScoresJson, @overallComment, @adjustedFinalScore,
                @auditTrailJson, @humanEdited, @createdAt, @updatedAt, @lastModifiedBy)`)
    }
  },

  async getManualReview(applicationId: string): Promise<ManualReviewData | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('applicationId', sql.NVarChar, applicationId)
      .query(`SELECT * FROM ${T('ManualReviews')} WHERE ApplicationId = @applicationId`)
    return result.recordset[0] ? rowToManualReview(result.recordset[0]) : undefined
  },
}
