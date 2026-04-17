import { getPool, sql } from '../db.js'
import type { ScoringPrompt, PromptTestRun } from '../../../src/types/index.js'

function parseJson<T>(val: string | null | undefined, fallback: T): T {
  if (!val) return fallback
  try { return JSON.parse(val) } catch { return fallback }
}

function rowToPrompt(r: any): ScoringPrompt {
  return {
    promptId: r.Id,
    jobId: r.JobId,
    versionNumber: r.VersionNumber,
    promptText: r.PromptText,
    status: r.Status,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    lastModifiedAt: r.LastModifiedAt?.toISOString?.() ?? r.LastModifiedAt,
    author: r.Author,
    rating: r.Rating ?? undefined,
    comments: r.Comments ?? undefined,
    source: r.Source,
    generationMetadata: parseJson(r.GenerationMetadataJson, undefined),
  }
}

function rowToTestRun(r: any): PromptTestRun {
  return {
    testRunId: r.Id,
    jobId: r.JobId,
    promptId: r.PromptId,
    status: r.Status,
    applicationIds: parseJson(r.ApplicationIdsJson, []),
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    completedAt: r.CompletedAt?.toISOString?.() ?? r.CompletedAt ?? undefined,
    reviewedBy: r.ReviewedBy ?? undefined,
    reviewNotes: r.ReviewNotes ?? undefined,
  }
}

export const promptRepo = {
  async getByJobId(jobId: string): Promise<ScoringPrompt[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query('SELECT * FROM ScoringPrompts WHERE JobId = @jobId ORDER BY VersionNumber DESC')
    return result.recordset.map(rowToPrompt)
  },

  async getById(promptId: string): Promise<ScoringPrompt | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, promptId)
      .query('SELECT * FROM ScoringPrompts WHERE Id = @id')
    return result.recordset[0] ? rowToPrompt(result.recordset[0]) : undefined
  },

  async getMaxVersion(jobId: string): Promise<number> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query('SELECT ISNULL(MAX(VersionNumber), 0) AS maxVer FROM ScoringPrompts WHERE JobId = @jobId')
    return result.recordset[0].maxVer
  },

  async create(prompt: ScoringPrompt): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, prompt.promptId)
      .input('jobId', sql.NVarChar, prompt.jobId)
      .input('versionNumber', sql.Int, prompt.versionNumber)
      .input('promptText', sql.NVarChar, prompt.promptText)
      .input('status', sql.NVarChar, prompt.status)
      .input('createdAt', sql.DateTime2, new Date(prompt.createdAt))
      .input('lastModifiedAt', sql.DateTime2, new Date(prompt.lastModifiedAt))
      .input('author', sql.NVarChar, prompt.author)
      .input('rating', sql.Int, prompt.rating ?? null)
      .input('comments', sql.NVarChar, prompt.comments ?? null)
      .input('source', sql.NVarChar, prompt.source)
      .input('generationMetadataJson', sql.NVarChar, prompt.generationMetadata ? JSON.stringify(prompt.generationMetadata) : null)
      .query(`INSERT INTO ScoringPrompts (Id, JobId, VersionNumber, PromptText, Status, CreatedAt, LastModifiedAt, Author, Rating, Comments, Source, GenerationMetadataJson)
              VALUES (@id, @jobId, @versionNumber, @promptText, @status, @createdAt, @lastModifiedAt, @author, @rating, @comments, @source, @generationMetadataJson)`)
  },

  async updateStatus(promptId: string, status: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, promptId)
      .input('status', sql.NVarChar, status)
      .query('UPDATE ScoringPrompts SET Status = @status, LastModifiedAt = SYSUTCDATETIME() WHERE Id = @id')
  },

  async updateRating(promptId: string, rating: number, comments?: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, promptId)
      .input('rating', sql.Int, rating)
      .input('comments', sql.NVarChar, comments ?? null)
      .query('UPDATE ScoringPrompts SET Rating = @rating, Comments = @comments, LastModifiedAt = SYSUTCDATETIME() WHERE Id = @id')
  },

  async deactivateAllForJob(jobId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query("UPDATE ScoringPrompts SET Status = 'inactive', LastModifiedAt = SYSUTCDATETIME() WHERE JobId = @jobId AND Status = 'active'")
  },

  async getProductionApproved(jobId: string): Promise<ScoringPrompt | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query("SELECT * FROM ScoringPrompts WHERE JobId = @jobId AND Status = 'production-approved'")
    return result.recordset[0] ? rowToPrompt(result.recordset[0]) : undefined
  },

  // Test runs
  async createTestRun(testRun: PromptTestRun): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, testRun.testRunId)
      .input('jobId', sql.NVarChar, testRun.jobId)
      .input('promptId', sql.NVarChar, testRun.promptId)
      .input('status', sql.NVarChar, testRun.status)
      .input('applicationIdsJson', sql.NVarChar, JSON.stringify(testRun.applicationIds))
      .input('createdAt', sql.DateTime2, new Date(testRun.createdAt))
      .query(`INSERT INTO PromptTestRuns (Id, JobId, PromptId, Status, ApplicationIdsJson, CreatedAt)
              VALUES (@id, @jobId, @promptId, @status, @applicationIdsJson, @createdAt)`)
  },

  async getTestRun(testRunId: string): Promise<PromptTestRun | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, testRunId)
      .query('SELECT * FROM PromptTestRuns WHERE Id = @id')
    return result.recordset[0] ? rowToTestRun(result.recordset[0]) : undefined
  },

  async getTestRunsByPrompt(promptId: string): Promise<PromptTestRun[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('promptId', sql.NVarChar, promptId)
      .query('SELECT * FROM PromptTestRuns WHERE PromptId = @promptId ORDER BY CreatedAt DESC')
    return result.recordset.map(rowToTestRun)
  },

  async updateTestRun(testRunId: string, fields: Partial<Pick<PromptTestRun, 'status' | 'completedAt' | 'reviewedBy' | 'reviewNotes' | 'applicationIds'>>): Promise<void> {
    const pool = await getPool()
    const sets: string[] = []
    const req = pool.request().input('id', sql.NVarChar, testRunId)
    if (fields.status !== undefined) { sets.push('Status = @status'); req.input('status', sql.NVarChar, fields.status) }
    if (fields.completedAt !== undefined) { sets.push('CompletedAt = @completedAt'); req.input('completedAt', sql.DateTime2, new Date(fields.completedAt)) }
    if (fields.reviewedBy !== undefined) { sets.push('ReviewedBy = @reviewedBy'); req.input('reviewedBy', sql.NVarChar, fields.reviewedBy) }
    if (fields.reviewNotes !== undefined) { sets.push('ReviewNotes = @reviewNotes'); req.input('reviewNotes', sql.NVarChar, fields.reviewNotes) }
    if (fields.applicationIds !== undefined) { sets.push('ApplicationIdsJson = @appIds'); req.input('appIds', sql.NVarChar, JSON.stringify(fields.applicationIds)) }
    if (sets.length === 0) return
    await req.query(`UPDATE PromptTestRuns SET ${sets.join(', ')} WHERE Id = @id`)
  },
}
