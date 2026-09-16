import { getPool, sql } from '../db.js'
import { T } from '../table-names.js'
import type { ExtractionInstructionVersion, ExtractionValidationFinding } from '../../../src/types/index.js'

function parseFindings(raw: string | null | undefined): ExtractionValidationFinding[] {
  if (!raw) return []
  try { return JSON.parse(raw) } catch { return [] }
}

function mapVersion(row: any): ExtractionInstructionVersion {
  return {
    id: row.Id,
    versionNumber: Number(row.VersionNumber ?? 0),
    instructionText: row.InstructionText ?? '',
    protectedContractVersion: row.ProtectedContractVersion ?? '',
    status: row.Status ?? 'draft',
    validationStatus: row.ValidationStatus ?? 'unvalidated',
    changeNote: row.ChangeNote ?? null,
    validationFindings: parseFindings(row.ValidationFindingsJson),
    createdAt: row.CreatedAt?.toISOString?.() ?? row.CreatedAt,
    createdBy: row.CreatedBy ?? '',
    validatedAt: row.ValidatedAt?.toISOString?.() ?? row.ValidatedAt ?? null,
    validatedBy: row.ValidatedBy ?? null,
    activatedAt: row.ActivatedAt?.toISOString?.() ?? row.ActivatedAt ?? null,
    activatedBy: row.ActivatedBy ?? null,
    concurrencyVersion: Number(row.ConcurrencyVersion ?? 1),
  }
}

export const extractionInstructionRepo = {
  async any(): Promise<boolean> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT COUNT(*) AS count FROM ${T('ExtractionInstructionVersions')}`)
    return Number(result.recordset[0]?.count ?? 0) > 0
  },

  async getNextVersionNumber(): Promise<number> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT COALESCE(MAX(VersionNumber), 0) AS maxVersion FROM ${T('ExtractionInstructionVersions')}`)
    return Number(result.recordset[0]?.maxVersion ?? 0) + 1
  },

  async list(): Promise<ExtractionInstructionVersion[]> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT * FROM ${T('ExtractionInstructionVersions')} ORDER BY VersionNumber DESC`)
    return result.recordset.map(mapVersion)
  },

  async getById(id: string): Promise<ExtractionInstructionVersion | undefined> {
    const pool = await getPool()
    const result = await pool.request().input('id', sql.NVarChar, id)
      .query(`SELECT * FROM ${T('ExtractionInstructionVersions')} WHERE Id = @id`)
    return result.recordset[0] ? mapVersion(result.recordset[0]) : undefined
  },

  async getActive(): Promise<ExtractionInstructionVersion | undefined> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT * FROM ${T('ExtractionInstructionVersions')} WHERE Status = 'active' ORDER BY VersionNumber DESC`)
    return result.recordset[0] ? mapVersion(result.recordset[0]) : undefined
  },

  async create(version: ExtractionInstructionVersion): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, version.id)
      .input('versionNumber', sql.Int, version.versionNumber)
      .input('instructionText', sql.NVarChar, version.instructionText)
      .input('protectedContractVersion', sql.NVarChar, version.protectedContractVersion)
      .input('status', sql.NVarChar, version.status)
      .input('changeNote', sql.NVarChar, version.changeNote ?? null)
      .input('validationStatus', sql.NVarChar, version.validationStatus)
      .input('validationFindingsJson', sql.NVarChar, JSON.stringify(version.validationFindings))
      .input('createdAt', sql.DateTime2, new Date(version.createdAt))
      .input('createdBy', sql.NVarChar, version.createdBy)
      .input('validatedAt', sql.DateTime2, version.validatedAt ? new Date(version.validatedAt) : null)
      .input('validatedBy', sql.NVarChar, version.validatedBy ?? null)
      .input('activatedAt', sql.DateTime2, version.activatedAt ? new Date(version.activatedAt) : null)
      .input('activatedBy', sql.NVarChar, version.activatedBy ?? null)
      .input('concurrencyVersion', sql.Int, version.concurrencyVersion)
      .query(`INSERT INTO ${T('ExtractionInstructionVersions')} (
        Id, VersionNumber, InstructionText, ProtectedContractVersion, Status, ChangeNote,
        ValidationStatus, ValidationFindingsJson, CreatedAt, CreatedBy, ValidatedAt, ValidatedBy,
        ActivatedAt, ActivatedBy, ConcurrencyVersion)
        VALUES (
        @id, @versionNumber, @instructionText, @protectedContractVersion, @status, @changeNote,
        @validationStatus, @validationFindingsJson, @createdAt, @createdBy, @validatedAt, @validatedBy,
        @activatedAt, @activatedBy, @concurrencyVersion)`)
  },

  async update(version: ExtractionInstructionVersion): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, version.id)
      .input('instructionText', sql.NVarChar, version.instructionText)
      .input('status', sql.NVarChar, version.status)
      .input('changeNote', sql.NVarChar, version.changeNote ?? null)
      .input('validationStatus', sql.NVarChar, version.validationStatus)
      .input('validationFindingsJson', sql.NVarChar, JSON.stringify(version.validationFindings))
      .input('validatedAt', sql.DateTime2, version.validatedAt ? new Date(version.validatedAt) : null)
      .input('validatedBy', sql.NVarChar, version.validatedBy ?? null)
      .input('activatedAt', sql.DateTime2, version.activatedAt ? new Date(version.activatedAt) : null)
      .input('activatedBy', sql.NVarChar, version.activatedBy ?? null)
      .input('concurrencyVersion', sql.Int, version.concurrencyVersion)
      .query(`UPDATE ${T('ExtractionInstructionVersions')}
        SET InstructionText=@instructionText, Status=@status, ChangeNote=@changeNote,
            ValidationStatus=@validationStatus, ValidationFindingsJson=@validationFindingsJson,
            ValidatedAt=@validatedAt, ValidatedBy=@validatedBy, ActivatedAt=@activatedAt,
            ActivatedBy=@activatedBy, ConcurrencyVersion=@concurrencyVersion
        WHERE Id=@id`)
  },

  async activate(id: string, actor: string, activatedAt: string): Promise<void> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()
    try {
      await txn.request()
        .input('targetId', sql.NVarChar, id)
        .query(`UPDATE ${T('ExtractionInstructionVersions')}
          SET Status = CASE WHEN Id = @targetId THEN 'active' ELSE 'retired' END
          WHERE Status = 'active' OR Id = @targetId`)
      await txn.request()
        .input('targetId', sql.NVarChar, id)
        .input('activatedAt', sql.DateTime2, new Date(activatedAt))
        .input('activatedBy', sql.NVarChar, actor)
        .query(`UPDATE ${T('ExtractionInstructionVersions')}
          SET ActivatedAt=@activatedAt, ActivatedBy=@activatedBy, ConcurrencyVersion = COALESCE(ConcurrencyVersion, 0) + 1
          WHERE Id=@targetId`)
      await txn.commit()
    } catch (error) {
      await txn.rollback()
      throw error
    }
  },
}
