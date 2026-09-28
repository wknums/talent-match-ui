import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'
import type { JobSpecExtractionRecord, ExtractionValidationFinding } from '../../../src/types/index.js'

function parseFindings(raw: string | null | undefined): ExtractionValidationFinding[] {
  if (!raw) return []
  try { return JSON.parse(raw) } catch { return [] }
}

function mapExtraction(row: any): JobSpecExtractionRecord {
  return {
    id: row.Id,
    purpose: row.Purpose,
    instructionVersionId: row.InstructionVersionId,
    protectedContractVersion: row.ProtectedContractVersion,
    sourceFileName: row.SourceFileName,
    sourceMimeType: row.SourceMimeType,
    sourceSha256: row.SourceSha256,
    rawResponse: row.RawResponse,
    normalizedResponseJson: row.NormalizedResponseJson ?? null,
    validationStatus: row.ValidationStatus,
    validationFindings: parseFindings(row.ValidationFindingsJson),
    jobId: row.JobId ?? null,
    jobConfigVersionId: row.JobConfigVersionId ?? null,
    createdAt: row.CreatedAt?.toISOString?.() ?? row.CreatedAt,
    createdBy: row.CreatedBy ?? '',
    completedAt: row.CompletedAt?.toISOString?.() ?? row.CompletedAt,
    correlationId: row.CorrelationId ?? '',
  }
}

export const jobSpecExtractionRepo = {
  async create(record: JobSpecExtractionRecord): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, record.id)
      .input('purpose', sql.NVarChar, record.purpose)
      .input('instructionVersionId', sql.NVarChar, record.instructionVersionId)
      .input('protectedContractVersion', sql.NVarChar, record.protectedContractVersion)
      .input('sourceFileName', sql.NVarChar, record.sourceFileName)
      .input('sourceMimeType', sql.NVarChar, record.sourceMimeType)
      .input('sourceSha256', sql.NVarChar, record.sourceSha256)
      .input('rawResponse', sql.NVarChar, record.rawResponse)
      .input('normalizedResponseJson', sql.NVarChar, record.normalizedResponseJson ?? null)
      .input('validationStatus', sql.NVarChar, record.validationStatus)
      .input('validationFindingsJson', sql.NVarChar, JSON.stringify(record.validationFindings))
      .input('jobId', sql.NVarChar, record.jobId ?? null)
      .input('jobConfigVersionId', sql.NVarChar, record.jobConfigVersionId ?? null)
      .input('createdAt', sql.DateTime2, new Date(record.createdAt))
      .input('createdBy', sql.NVarChar, record.createdBy)
      .input('completedAt', sql.DateTime2, new Date(record.completedAt))
      .input('correlationId', sql.NVarChar, record.correlationId)
      .query(`INSERT INTO ${T('JobSpecExtractions')} (
        Id, Purpose, InstructionVersionId, ProtectedContractVersion, SourceFileName, SourceMimeType,
        SourceSha256, RawResponse, NormalizedResponseJson, ValidationStatus, ValidationFindingsJson,
        JobId, JobConfigVersionId, CreatedAt, CreatedBy, CompletedAt, CorrelationId)
        VALUES (
        @id, @purpose, @instructionVersionId, @protectedContractVersion, @sourceFileName, @sourceMimeType,
        @sourceSha256, @rawResponse, @normalizedResponseJson, @validationStatus, @validationFindingsJson,
        @jobId, @jobConfigVersionId, @createdAt, @createdBy, @completedAt, @correlationId)`)
  },

  async getLatestForJob(jobId: string): Promise<JobSpecExtractionRecord | undefined> {
    const pool = await getPool()
    const result = await pool.request().input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('JobSpecExtractions')} WHERE JobId=@jobId ORDER BY CreatedAt DESC`)
    return result.recordset[0] ? mapExtraction(result.recordset[0]) : undefined
  },

  async getByConfigVersionId(jobConfigVersionId: string): Promise<JobSpecExtractionRecord | undefined> {
    const pool = await getPool()
    const result = await pool.request().input('jobConfigVersionId', sql.NVarChar, jobConfigVersionId)
      .query(`SELECT * FROM ${T('JobSpecExtractions')} WHERE JobConfigVersionId=@jobConfigVersionId ORDER BY CreatedAt DESC`)
    return result.recordset[0] ? mapExtraction(result.recordset[0]) : undefined
  },

  async linkToJobConfig(extractionId: string, jobId: string, jobConfigVersionId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, extractionId)
      .input('jobId', sql.NVarChar, jobId)
      .input('jobConfigVersionId', sql.NVarChar, jobConfigVersionId)
      .query(`UPDATE ${T('JobSpecExtractions')} SET JobId=@jobId, JobConfigVersionId=@jobConfigVersionId WHERE Id=@id`)
  },
}
