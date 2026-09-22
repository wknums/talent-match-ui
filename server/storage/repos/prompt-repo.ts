import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'
import type { PromptGenerationInstruction, ScoringPrompt, PromptTestRun } from '../../../src/types/index.js'

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
    generationInstructionVersionId: r.GenerationInstructionVersionId ?? undefined,
    modelId: r.ModelId ?? '',
    reasoningLevel: r.ReasoningLevel ?? '',
    approvedModelId: r.ApprovedModelId ?? undefined,
    approvedReasoningLevel: r.ApprovedReasoningLevel ?? undefined,
    approvedTestRunId: r.ApprovedTestRunId ?? undefined,
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
    modelId: r.ModelId ?? '',
    reasoningLevel: r.ReasoningLevel ?? '',
    approvedModelId: r.ApprovedModelId ?? undefined,
    approvedReasoningLevel: r.ApprovedReasoningLevel ?? undefined,
  }
}

function rowToInstruction(r: any): PromptGenerationInstruction {
  return {
    id: r.Id,
    jobId: r.JobId ?? undefined,
    versionNumber: Number(r.VersionNumber),
    instructionText: r.InstructionText,
    modelId: r.ModelId ?? '',
    reasoningLevel: r.ReasoningLevel ?? 'high',
    status: r.Status,
    changeNote: r.ChangeNote ?? undefined,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    createdBy: r.CreatedBy,
    activatedAt: r.ActivatedAt?.toISOString?.() ?? r.ActivatedAt ?? undefined,
    activatedBy: r.ActivatedBy ?? undefined,
  }
}

export const promptRepo = {
  async getByJobId(jobId: string): Promise<ScoringPrompt[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('ScoringPrompts')} WHERE JobId = @jobId ORDER BY VersionNumber DESC`)
    return result.recordset.map(rowToPrompt)
  },

  async getById(promptId: string): Promise<ScoringPrompt | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, promptId)
      .query(`SELECT * FROM ${T('ScoringPrompts')} WHERE Id = @id`)
    return result.recordset[0] ? rowToPrompt(result.recordset[0]) : undefined
  },

  async getMaxVersion(jobId: string): Promise<number> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT ISNULL(MAX(VersionNumber), 0) AS maxVer FROM ${T('ScoringPrompts')} WHERE JobId = @jobId`)
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
      .input('generationInstructionVersionId', sql.NVarChar, prompt.generationInstructionVersionId ?? null)
      .input('modelId', sql.NVarChar, prompt.modelId ?? '')
      .input('reasoningLevel', sql.NVarChar, prompt.reasoningLevel ?? '')
      .input('approvedModelId', sql.NVarChar, prompt.approvedModelId ?? null)
      .input('approvedReasoningLevel', sql.NVarChar, prompt.approvedReasoningLevel ?? null)
      .input('approvedTestRunId', sql.NVarChar, prompt.approvedTestRunId ?? null)
      .query(`INSERT INTO ${T('ScoringPrompts')} (
        Id, JobId, VersionNumber, PromptText, Status, CreatedAt, LastModifiedAt, Author,
        Rating, Comments, Source, GenerationMetadataJson, GenerationInstructionVersionId,
        ModelId, ReasoningLevel, ApprovedModelId, ApprovedReasoningLevel, ApprovedTestRunId)
        VALUES (
        @id, @jobId, @versionNumber, @promptText, @status, @createdAt, @lastModifiedAt, @author,
        @rating, @comments, @source, @generationMetadataJson, @generationInstructionVersionId,
        @modelId, @reasoningLevel, @approvedModelId, @approvedReasoningLevel, @approvedTestRunId)`)
  },

  async updateStatus(promptId: string, status: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, promptId)
      .input('status', sql.NVarChar, status)
      .query(`UPDATE ${T('ScoringPrompts')} SET Status = @status, LastModifiedAt = SYSUTCDATETIME() WHERE Id = @id`)
  },

  async updateProductionApproval(
    promptId: string,
    testRunId: string,
    modelId: string,
    reasoningLevel: string,
  ): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, promptId)
      .input('testRunId', sql.NVarChar, testRunId)
      .input('modelId', sql.NVarChar, modelId)
      .input('reasoningLevel', sql.NVarChar, reasoningLevel)
      .query(`UPDATE ${T('ScoringPrompts')}
        SET Status='production-approved', ApprovedTestRunId=@testRunId,
            ApprovedModelId=@modelId, ApprovedReasoningLevel=@reasoningLevel,
            LastModifiedAt=SYSUTCDATETIME()
        WHERE Id=@id`)
  },

  async updateRating(promptId: string, rating: number, comments?: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, promptId)
      .input('rating', sql.Int, rating)
      .input('comments', sql.NVarChar, comments ?? null)
      .query(`UPDATE ${T('ScoringPrompts')} SET Rating = @rating, Comments = @comments, LastModifiedAt = SYSUTCDATETIME() WHERE Id = @id`)
  },

  async deactivateAllForJob(jobId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`UPDATE ${T('ScoringPrompts')} SET Status = 'inactive', LastModifiedAt = SYSUTCDATETIME() WHERE JobId = @jobId AND Status = 'active'`)
  },

  async deactivateProductionForJob(jobId: string, exceptPromptId?: string): Promise<void> {
    const pool = await getPool()
    const request = pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .input('exceptPromptId', sql.NVarChar, exceptPromptId ?? '')
    await request.query(`UPDATE ${T('ScoringPrompts')}
      SET Status='inactive', LastModifiedAt=SYSUTCDATETIME()
      WHERE JobId=@jobId AND Status='production-approved' AND Id<>@exceptPromptId`)
  },

  async getProductionApproved(jobId: string): Promise<ScoringPrompt | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('ScoringPrompts')} WHERE JobId = @jobId AND Status = 'production-approved'`)
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
      .input('modelId', sql.NVarChar, testRun.modelId ?? '')
      .input('reasoningLevel', sql.NVarChar, testRun.reasoningLevel ?? '')
      .query(`INSERT INTO ${T('PromptTestRuns')} (
        Id, JobId, PromptId, Status, ApplicationIdsJson, CreatedAt, ModelId, ReasoningLevel)
        VALUES (
        @id, @jobId, @promptId, @status, @applicationIdsJson, @createdAt, @modelId, @reasoningLevel)`)
  },

  async getTestRun(testRunId: string): Promise<PromptTestRun | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, testRunId)
      .query(`SELECT * FROM ${T('PromptTestRuns')} WHERE Id = @id`)
    return result.recordset[0] ? rowToTestRun(result.recordset[0]) : undefined
  },

  async getTestRunsByPrompt(promptId: string): Promise<PromptTestRun[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('promptId', sql.NVarChar, promptId)
      .query(`SELECT * FROM ${T('PromptTestRuns')} WHERE PromptId = @promptId ORDER BY CreatedAt DESC`)
    return result.recordset.map(rowToTestRun)
  },

  async updateTestRun(testRunId: string, fields: Partial<Pick<PromptTestRun, 'status' | 'completedAt' | 'reviewedBy' | 'reviewNotes' | 'applicationIds' | 'approvedModelId' | 'approvedReasoningLevel'>>): Promise<void> {
    const pool = await getPool()
    const sets: string[] = []
    const req = pool.request().input('id', sql.NVarChar, testRunId)
    if (fields.status !== undefined) { sets.push('Status = @status'); req.input('status', sql.NVarChar, fields.status) }
    if (fields.completedAt !== undefined) { sets.push('CompletedAt = @completedAt'); req.input('completedAt', sql.DateTime2, new Date(fields.completedAt)) }
    if (fields.reviewedBy !== undefined) { sets.push('ReviewedBy = @reviewedBy'); req.input('reviewedBy', sql.NVarChar, fields.reviewedBy) }
    if (fields.reviewNotes !== undefined) { sets.push('ReviewNotes = @reviewNotes'); req.input('reviewNotes', sql.NVarChar, fields.reviewNotes) }
    if (fields.applicationIds !== undefined) { sets.push('ApplicationIdsJson = @appIds'); req.input('appIds', sql.NVarChar, JSON.stringify(fields.applicationIds)) }
    if (fields.approvedModelId !== undefined) { sets.push('ApprovedModelId = @approvedModelId'); req.input('approvedModelId', sql.NVarChar, fields.approvedModelId) }
    if (fields.approvedReasoningLevel !== undefined) { sets.push('ApprovedReasoningLevel = @approvedReasoningLevel'); req.input('approvedReasoningLevel', sql.NVarChar, fields.approvedReasoningLevel) }
    if (sets.length === 0) return
    await req.query(`UPDATE ${T('PromptTestRuns')} SET ${sets.join(', ')} WHERE Id = @id`)
  },

  async listInstructions(jobId?: string): Promise<PromptGenerationInstruction[]> {
    const pool = await getPool()
    const request = pool.request()
    const where = jobId
      ? 'JobId = @jobId'
      : 'JobId IS NULL'
    if (jobId) request.input('jobId', sql.NVarChar, jobId)
    const result = await request.query(
      `SELECT * FROM ${T('PromptGenerationInstructions')} WHERE ${where} ORDER BY VersionNumber DESC`,
    )
    return result.recordset.map(rowToInstruction)
  },

  async getInstructionById(id: string): Promise<PromptGenerationInstruction | undefined> {
    const pool = await getPool()
    const result = await pool.request().input('id', sql.NVarChar, id)
      .query(`SELECT * FROM ${T('PromptGenerationInstructions')} WHERE Id=@id`)
    return result.recordset[0] ? rowToInstruction(result.recordset[0]) : undefined
  },

  async getActiveInstruction(jobId?: string): Promise<PromptGenerationInstruction | undefined> {
    const pool = await getPool()
    const request = pool.request()
    const where = jobId ? 'JobId=@jobId' : 'JobId IS NULL'
    if (jobId) request.input('jobId', sql.NVarChar, jobId)
    const result = await request.query(
      `SELECT * FROM ${T('PromptGenerationInstructions')} WHERE ${where} AND Status='active' ORDER BY VersionNumber DESC`,
    )
    return result.recordset[0] ? rowToInstruction(result.recordset[0]) : undefined
  },

  async createInstruction(instruction: PromptGenerationInstruction): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, instruction.id)
      .input('jobId', sql.NVarChar, instruction.jobId ?? null)
      .input('versionNumber', sql.Int, instruction.versionNumber)
      .input('instructionText', sql.NVarChar, instruction.instructionText)
      .input('modelId', sql.NVarChar, instruction.modelId)
      .input('reasoningLevel', sql.NVarChar, instruction.reasoningLevel)
      .input('status', sql.NVarChar, instruction.status)
      .input('changeNote', sql.NVarChar, instruction.changeNote ?? null)
      .input('createdAt', sql.DateTime2, new Date(instruction.createdAt))
      .input('createdBy', sql.NVarChar, instruction.createdBy)
      .query(`INSERT INTO ${T('PromptGenerationInstructions')} (
        Id, JobId, VersionNumber, InstructionText, ModelId, ReasoningLevel, Status, ChangeNote, CreatedAt, CreatedBy)
        VALUES (@id, @jobId, @versionNumber, @instructionText, @modelId, @reasoningLevel, @status, @changeNote, @createdAt, @createdBy)`)
  },

  async activateInstruction(id: string, jobId: string | undefined, actor: string): Promise<void> {
    const pool = await getPool()
    const transaction = pool.transaction()
    await transaction.begin()
    try {
      const request = transaction.request().input('targetId', sql.NVarChar, id)
      const where = jobId ? 'JobId=@jobId' : 'JobId IS NULL'
      if (jobId) request.input('jobId', sql.NVarChar, jobId)
      await request.query(
        `UPDATE ${T('PromptGenerationInstructions')}
         SET Status=CASE WHEN Id=@targetId THEN 'active' ELSE 'inactive' END
         WHERE ${where} AND (Status='active' OR Id=@targetId)`,
      )
      await transaction.request()
        .input('id', sql.NVarChar, id)
        .input('actor', sql.NVarChar, actor)
        .query(`UPDATE ${T('PromptGenerationInstructions')}
          SET ActivatedAt=SYSUTCDATETIME(), ActivatedBy=@actor WHERE Id=@id`)
      await transaction.commit()
    } catch (error) {
      await transaction.rollback()
      throw error
    }
  },
}
