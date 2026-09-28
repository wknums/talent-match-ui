import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { describe, expect, it } from 'vitest'

const require = createRequire(import.meta.url)
const Database = require('better-sqlite3')
const root = path.resolve(import.meta.dirname, '..', '..')
const sqliteSchema = fs.readFileSync(path.join(root, 'server', 'storage', 'schema-sqlite.sql'), 'utf8')
const azureSchema = fs.readFileSync(path.join(root, 'server', 'storage', 'schema.sql'), 'utf8')
const stackBMigration = fs.readFileSync(
  path.join(root, 'dotnet', 'src', 'Infrastructure', 'Persistence', 'Migrations', '20260922231530_AddOptionalParallelUploads.cs'),
  'utf8',
)

const columns = {
  UploadSettings: [
    'Id', 'FileConcurrency', 'MaxIndividualFileBytes', 'MaxInFlightBytes',
    'ConcurrencyVersion', 'CreatedAt', 'CreatedBy', 'UpdatedAt', 'UpdatedBy',
  ],
  UploadSessions: [
    'Id', 'JobId', 'OwnerActorId', 'OwnerDisplayName', 'AllowDuplicates', 'Status',
    'FileConcurrency', 'MaxIndividualFileBytes', 'MaxInFlightBytes', 'TotalItemCount',
    'WaitingCount', 'ActiveCount', 'SucceededCount', 'SkippedCount', 'FailedCount',
    'InterruptedCount', 'TerminalItemCount', 'CorrelationId', 'LastHeartbeatAt',
    'CreatedAt', 'StartedAt', 'CompletedAt', 'ConcurrencyVersion',
  ],
  UploadItems: [
    'Id', 'SessionId', 'OccurrenceKey', 'Ordinal', 'FileName', 'MimeType',
    'RawSizeBytes', 'Status', 'AttemptCount', 'ContentFingerprint', 'ApplicationId',
    'OutcomeCode', 'OutcomeMessage', 'LastHttpStatus', 'LastAttemptAt', 'NextRetryAt',
    'CreatedAt', 'UpdatedAt', 'CompletedAt', 'ConcurrencyVersion',
  ],
}

describe('optional upload schema parity', () => {
  it('applies SQLite schema idempotently and creates only additive upload tables', () => {
    const db = new Database(':memory:')
    expect(() => {
      db.exec(sqliteSchema)
      db.exec(sqliteSchema)
    }).not.toThrow()
    const tables = db.prepare(`
      SELECT name FROM sqlite_master
      WHERE type = 'table' AND name LIKE 'Upload%'
      ORDER BY name
    `).all().map((row: { name: string }) => row.name)
    expect(tables).toEqual(['UploadItems', 'UploadSessions', 'UploadSettings'])
    db.close()
  })

  it('matches Stack B table and column names in both providers', () => {
    for (const [table, names] of Object.entries(columns)) {
      expect(sqliteSchema).toContain(`CREATE TABLE IF NOT EXISTS ${table}`)
      expect(azureSchema).toContain(`CREATE TABLE [talentmatch].${table}`)
      expect(stackBMigration).toContain(`name: "${table}"`)
      for (const column of names) {
        expect(sqliteSchema).toMatch(new RegExp(`\\b${column}\\b`))
        expect(azureSchema).toMatch(new RegExp(`\\b${column}\\b`))
        expect(stackBMigration).toMatch(new RegExp(`\\b${column}\\b`))
      }
    }
  })

  it('matches Azure SQL foreign-key identifier widths', () => {
    expect(azureSchema).toMatch(/CREATE TABLE \[talentmatch\]\.UploadSessions \([\s\S]*?\bId\s+NVARCHAR\(36\)[\s\S]*?\bJobId\s+NVARCHAR\(36\)/)
    expect(azureSchema).toMatch(/CREATE TABLE \[talentmatch\]\.UploadItems \([\s\S]*?\bId\s+NVARCHAR\(36\)[\s\S]*?\bSessionId\s+NVARCHAR\(36\)[\s\S]*?\bApplicationId\s+NVARCHAR\(36\)/)
    expect(stackBMigration).toContain('var idType = sqlServer ? "nvarchar(36)" : "TEXT"')
    expect(stackBMigration).not.toContain('nvarchar(450)')
  })

  it.each([
    'IX_UploadItems_ApplicationId',
    'IX_UploadItems_SessionId_ContentFingerprint',
    'IX_UploadItems_SessionId_OccurrenceKey',
    'IX_UploadItems_SessionId_Ordinal',
    'IX_UploadItems_SessionId_Status_Ordinal',
    'IX_UploadItems_Status_UpdatedAt',
    'IX_UploadSessions_JobId_CreatedAt',
    'IX_UploadSessions_OwnerActorId_CreatedAt',
    'IX_UploadSessions_Status_LastHeartbeatAt',
  ])('matches Stack B index %s', index => {
    expect(sqliteSchema).toContain(index)
    expect(azureSchema).toContain(index)
    expect(stackBMigration).toContain(index)
  })

  it('uses absent settings semantics and exact shared defaults', () => {
    expect(sqliteSchema).not.toMatch(/INSERT\s+INTO\s+UploadSettings/i)
    expect(azureSchema).not.toMatch(/INSERT\s+INTO\s+\[talentmatch\]\.UploadSettings/i)
    expect(stackBMigration).not.toMatch(/InsertData[\s\S]*UploadSettings/i)
  })
})
