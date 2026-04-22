import { mkdirSync, readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, resolve } from 'node:path'
import { randomUUID } from 'node:crypto'

const require = createRequire(import.meta.url)

// ---------------------------------------------------------------------------
// Dual-driver database layer
//   • Production  → Azure SQL via 'mssql' using Entra managed identity
//                   (or a legacy connection string when explicitly provided)
//   • Development → SQLite via 'better-sqlite3' (default when Azure SQL is unset)
// ---------------------------------------------------------------------------

type AzureSqlSettings =
  | { mode: 'connection-string'; connectionString: string }
  | { mode: 'entra'; server: string; database: string; clientId?: string }

const azureSqlSettings = resolveAzureSqlSettings()

/** Are we using Azure SQL? */
export const isAzureSql: boolean = azureSqlSettings !== null

// ---- mssql type-tag stubs used by repos in .input(name, TYPE, value) ------
// When using SQLite the shim ignores them.
// When using mssql, `sql` is replaced with the real mssql module in createMssqlPool().
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export let sql: any = {
  NVarChar: 'NVarChar',
  Int: 'Int',
  Float: 'Float',
  BigInt: 'BigInt',
  Bit: 'Bit',
  DateTime2: 'DateTime2',
}

// ---- Shared pool variable --------------------------------------------------
// eslint-disable-next-line @typescript-eslint/no-explicit-any
let _pool: any = null
// eslint-disable-next-line @typescript-eslint/no-explicit-any
let _sqliteDb: any = null

const azureSqlRetryConfig = {
  // Covers typical Azure SQL pay-as-you-go wake-up windows (~30-90s)
  maxAttempts: Number(process.env.AZURE_SQL_WAKEUP_MAX_ATTEMPTS ?? 8),
  initialDelayMs: Number(process.env.AZURE_SQL_WAKEUP_INITIAL_DELAY_MS ?? 2000),
  maxDelayMs: Number(process.env.AZURE_SQL_WAKEUP_MAX_DELAY_MS ?? 15000),
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export async function getPool(): Promise<any> {
  if (_pool) return _pool
  if (isAzureSql) {
    _pool = await createMssqlPool()
  } else {
    _pool = createSqlitePool()
  }
  return _pool
}

// ===========================================================================
// Azure SQL (mssql) driver
// ===========================================================================
async function createMssqlPool() {
  const mssqlModule = require('mssql')
  const mssql = mssqlModule?.default ?? mssqlModule
  const connectWithConfig = resolveMssqlConnect(mssqlModule)
  // Replace the stub type-tags with real mssql type objects
  sql = mssql ?? mssqlModule
  if (!azureSqlSettings) {
    throw new Error('Azure SQL settings are required when STORAGE_PROVIDER=azuresql.')
  }

  const connectionConfig =
    azureSqlSettings.mode === 'connection-string'
      ? azureSqlSettings.connectionString
      : {
          server: azureSqlSettings.server,
          database: azureSqlSettings.database,
          connectionTimeout: 90_000,
          requestTimeout: 180_000,
          options: {
            encrypt: true,
            trustServerCertificate: false,
          },
          authentication: {
            type: 'azure-active-directory-default',
            options: azureSqlSettings.clientId
              ? { clientId: azureSqlSettings.clientId }
              : {},
          },
        }

  const startedAt = Date.now()
  let lastError: unknown
  let attemptsMade = 0

  for (let attempt = 1; attempt <= azureSqlRetryConfig.maxAttempts; attempt++) {
    attemptsMade = attempt
    try {
      const pool = await connectWithConfig(connectionConfig)
      if (attempt > 1) {
        const elapsedMs = Date.now() - startedAt
        console.log(`[db] Azure SQL connection established after retry (attempt=${attempt}, elapsedMs=${elapsedMs})`)
      }
      return pool
    } catch (error) {
      lastError = error
      const shouldRetry = isTransientAzureSqlConnectionError(error)
      const hasAttemptsLeft = attempt < azureSqlRetryConfig.maxAttempts
      const elapsedMs = Date.now() - startedAt

      if (!shouldRetry || !hasAttemptsLeft) {
        break
      }

      const delayMs = Math.min(
        azureSqlRetryConfig.maxDelayMs,
        azureSqlRetryConfig.initialDelayMs * Math.pow(2, attempt - 1),
      )

      const message = normalizeErrorMessage(error)
      console.warn(
        `[db] Azure SQL connect retry scheduled (attempt=${attempt}/${azureSqlRetryConfig.maxAttempts}, elapsedMs=${elapsedMs}, delayMs=${delayMs}, error="${message}")`,
      )

      await sleep(delayMs)
    }
  }

  const finalMessage = normalizeErrorMessage(lastError)
  console.error(
    `[db] Azure SQL connect failed after retries (attempts=${attemptsMade}, elapsedMs=${Date.now() - startedAt}, error="${finalMessage}")`,
  )
  throw lastError
}

type MssqlConnectConfig = string | Record<string, unknown>

function resolveMssqlConnect(mssqlModule: unknown): (config: MssqlConnectConfig) => Promise<unknown> {
  const candidates = [
    mssqlModule,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (mssqlModule as any)?.default,
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (mssqlModule as any)?.sql,
  ]

  for (const candidate of candidates) {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    if (typeof (candidate as any)?.connect === 'function') {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      return (config: MssqlConnectConfig) => (candidate as any).connect(config)
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    if (typeof (candidate as any)?.ConnectionPool === 'function') {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      return async (config: MssqlConnectConfig) => new (candidate as any).ConnectionPool(config).connect()
    }
  }

  const availableKeys = Object.keys(
    (mssqlModule && typeof mssqlModule === 'object'
      ? (mssqlModule as Record<string, unknown>)
      : {}) as Record<string, unknown>,
  )
  throw new Error(
    `Unable to resolve mssql connect API. Module keys: ${availableKeys.length ? availableKeys.join(', ') : '(none)'}`,
  )
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms))
}

function normalizeErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return String(error)
}

function isTransientAzureSqlConnectionError(error: unknown): boolean {
  const message = normalizeErrorMessage(error).toLowerCase()

  const transientMessageFragments = [
    'timeout',
    'timed out',
    'etimedout',
    'econnreset',
    'econnrefused',
    'transient',
    'temporarily unavailable',
    'service is busy',
    'could not open a connection',
    'connection was denied',
    'resource limit',
    'throttle',
    '40501',
    '40613',
    '40197',
    '10928',
    '10929',
  ]

  return transientMessageFragments.some(fragment => message.includes(fragment))
}

function resolveAzureSqlSettings(): AzureSqlSettings | null {
  const legacyConnectionString = process.env.AZURE_SQL_CONNECTION_STRING?.trim()
  if (legacyConnectionString) {
    return {
      mode: 'connection-string',
      connectionString: legacyConnectionString,
    }
  }

  const storageProvider = (process.env.STORAGE_PROVIDER ?? '').trim().toLowerCase()
  const authMode = (process.env.AZURE_SQL_AUTH_MODE ?? '').trim().toLowerCase()
  const server = process.env.AZURE_SQL_SERVER_FQDN?.trim()
  const database = process.env.AZURE_SQL_DATABASE_NAME?.trim()

  if (storageProvider !== 'azuresql' || authMode !== 'entra') {
    return null
  }

  if (!server || !database) {
    throw new Error(
      'AZURE_SQL_SERVER_FQDN and AZURE_SQL_DATABASE_NAME are required when Azure SQL Entra authentication is enabled.',
    )
  }

  return {
    mode: 'entra',
    server,
    database,
    clientId: process.env.AZURE_CLIENT_ID?.trim() || undefined,
  }
}

// ===========================================================================
// SQLite (better-sqlite3) driver — mssql-API-compatible shim
// ===========================================================================

/**
 * Rewrite a SQL string authored for SQL Server so it works on SQLite.
 *   • SYSUTCDATETIME()  →  datetime('now')
 *   • NEWID()           →  <pre-generated uuid>
 *   • ISNULL(a, b)      →  IFNULL(a, b)
 *   • OFFSET n ROWS FETCH NEXT m ROWS ONLY  →  LIMIT m OFFSET n
 * Note: @param syntax is natively supported by better-sqlite3.
 */
function adaptSqlForSqlite(query: string): string {
  let q = query
  q = q.replace(/SYSUTCDATETIME\(\)/gi, "datetime('now')")
  q = q.replace(/NEWID\(\)/gi, `'${randomUUID()}'`)
  q = q.replace(/\bISNULL\s*\(/gi, 'IFNULL(')
  // Convert SQL-Server pagination to SQLite LIMIT/OFFSET
  q = q.replace(
    /OFFSET\s+(@?\w+)\s+ROWS\s+FETCH\s+NEXT\s+(@?\w+)\s+ROWS\s+ONLY/gi,
    'LIMIT $2 OFFSET $1',
  )
  return q
}

function createSqlitePool() {
  const Database = require('better-sqlite3')
  const dbPath = process.env.SQLITE_DB_PATH
    ? resolve(process.cwd(), process.env.SQLITE_DB_PATH)
    : resolve(import.meta.dirname, '..', '..', 'shared-data', 'talentmatch.db')
  mkdirSync(dirname(dbPath), { recursive: true })
  const db = new Database(dbPath)
  db.pragma('journal_mode = WAL')
  db.pragma('foreign_keys = ON')
  _sqliteDb = db

  /** Build an mssql-compatible request builder. */
  function makeRequest(parentDb: any = db) {
    const params: Record<string, unknown> = {}
    const builder = {
      input(name: string, _type: unknown, value: unknown) {
        // Normalise Date objects to ISO-8601 strings for SQLite TEXT columns
        // Convert undefined/NaN to null for SQLite compatibility
        if (value instanceof Date) {
          params[name] = value.toISOString()
        } else if (value === undefined) {
          params[name] = null
        } else {
          params[name] = value
        }
        return builder
      },
      query(sqlText: string) {
        const adapted = adaptSqlForSqlite(sqlText)
        const trimmed = adapted.trim()
        const isSelect = /^(SELECT|WITH)\b/i.test(trimmed)
        if (isSelect) {
          const rows = parentDb.prepare(adapted).all(params)
          return Promise.resolve({ recordset: rows, rowsAffected: [rows.length] })
        }
        const info = parentDb.prepare(adapted).run(params)
        return Promise.resolve({ recordset: [], rowsAffected: [info.changes] })
      },
    }
    return builder
  }

  return {
    request: () => makeRequest(db),
    transaction: () => {
      // SQLite transactions use the same db handle; we fake begin/commit/rollback.
      let committed = false
      return {
        begin() {
          db.prepare('BEGIN').run()
          return Promise.resolve()
        },
        request: () => makeRequest(db),
        commit() {
          db.prepare('COMMIT').run()
          committed = true
          return Promise.resolve()
        },
        rollback() {
          if (!committed) db.prepare('ROLLBACK').run()
          return Promise.resolve()
        },
      }
    },
    close() {
      db.close()
      _pool = null
      return Promise.resolve()
    },
  }
}

function sqliteTableHasColumn(db: any, tableName: string, columnName: string): boolean {
  const columns = db.prepare(`PRAGMA table_info(${tableName})`).all()
  return columns.some((column: { name: string }) => column.name === columnName)
}

function sqliteTableExists(db: any, tableName: string): boolean {
  return Boolean(db.prepare('SELECT name FROM sqlite_master WHERE type = ? AND name = ?').get('table', tableName))
}

function ensureSqliteColumn(db: any, tableName: string, columnName: string, definition: string): void {
  if (!sqliteTableHasColumn(db, tableName, columnName)) {
    db.exec(`ALTER TABLE ${tableName} ADD COLUMN ${columnName} ${definition}`)
  }
}

function ensureSqliteCompatibilitySchema(db: any): void {
  ensureSqliteColumn(db, 'Users', 'PasswordResetRequired', 'INTEGER NOT NULL DEFAULT 0')

  ensureSqliteColumn(db, 'PasswordResetRequests', 'FullName', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'RequestedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'ResolvedAt', 'TEXT NULL')
  ensureSqliteColumn(db, 'PasswordResetRequests', 'ResolvedBy', 'TEXT NULL')

  ensureSqliteColumn(db, 'Jobs', 'SpecDocumentId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Jobs', 'RubricDocumentId', 'TEXT NULL')

  ensureSqliteColumn(db, 'JobConfigVersions', 'MustHavesJson', "TEXT NOT NULL DEFAULT '[]'")
  ensureSqliteColumn(db, 'JobConfigVersions', 'RunsPerApplication', 'INTEGER NOT NULL DEFAULT 3')

  ensureSqliteColumn(db, 'Applications', 'CandidateRef', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'Applications', 'CandidateName', 'TEXT NULL')
  ensureSqliteColumn(db, 'Applications', 'CandidateEmail', 'TEXT NULL')
  ensureSqliteColumn(db, 'Applications', 'Flagged', 'INTEGER NOT NULL DEFAULT 0')

  ensureSqliteColumn(db, 'ApplicationDocuments', 'MimeType', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ApplicationDocuments', 'SizeBytes', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'ApplicationDocuments', 'UploadedAt', "TEXT NOT NULL DEFAULT ''")

  ensureSqliteColumn(db, 'ExtractionArtifacts', 'Markdown', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ExtractionArtifacts', 'ToolVersion', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ExtractionArtifacts', 'Confidence', 'REAL NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'ExtractionArtifacts', 'ExtractedAt', 'TEXT NULL')

  ensureSqliteColumn(db, 'ScoringRuns', 'VersionId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ScoringRuns', 'ModelDeploymentId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ScoringRuns', 'PromptVersionId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ScoringRuns', 'OverallScore', 'REAL NULL')
  ensureSqliteColumn(db, 'ScoringRuns', 'SubScoresJson', "TEXT NOT NULL DEFAULT '{}'")
  ensureSqliteColumn(db, 'ScoringRuns', 'MustHaveResultJson', "TEXT NOT NULL DEFAULT '{}'")
  ensureSqliteColumn(db, 'ScoringRuns', 'Rationale', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ScoringRuns', 'ImprovementRecsJson', "TEXT NOT NULL DEFAULT '[]'")
  ensureSqliteColumn(db, 'ScoringRuns', 'DurationMs', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'ScoringRuns', 'TokenUsageJson', 'TEXT NULL')
  ensureSqliteColumn(db, 'ScoringRuns', 'Status', "TEXT NOT NULL DEFAULT 'Success'")
  ensureSqliteColumn(db, 'ScoringRuns', 'RawResponseText', 'TEXT NULL')
  ensureSqliteColumn(db, 'ScoringRuns', 'RawParsedResponseJson', 'TEXT NULL')
  ensureSqliteColumn(db, 'ScoringRuns', 'ParserWarningsJson', 'TEXT NULL')
  ensureSqliteColumn(db, 'ScoringRuns', 'ParserConfidence', 'REAL NULL')

  ensureSqliteColumn(db, 'AggregatedResults', 'VersionId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'AggregatedResults', 'FinalSubScoresJson', "TEXT NOT NULL DEFAULT '{}'")
  ensureSqliteColumn(db, 'AggregatedResults', 'FinalDecision', 'TEXT NULL')
  ensureSqliteColumn(db, 'AggregatedResults', 'RationaleText', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'AggregatedResults', 'RecommendationsText', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'AggregatedResults', 'AllRunsJson', "TEXT NOT NULL DEFAULT '[]'")

  ensureSqliteColumn(db, 'ManualReviews', 'JobId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ManualReviews', 'LastModifiedBy', "TEXT NOT NULL DEFAULT ''")

  ensureSqliteColumn(db, 'FailureQueueItems', 'ApplicationId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'JobId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'EntityType', "TEXT NOT NULL DEFAULT 'Application'")
  ensureSqliteColumn(db, 'FailureQueueItems', 'EntityId', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'FailureType', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'AttemptCount', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'FailureQueueItems', 'RetryCount', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'FailureQueueItems', 'FirstFailedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'LastAttemptedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'CreatedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'UpdatedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'FailureQueueItems', 'CanRetry', 'INTEGER NOT NULL DEFAULT 1')
  ensureSqliteColumn(db, 'FailureQueueItems', 'Notes', 'TEXT NULL')

  ensureSqliteColumn(db, 'ProcessingEvents', 'Action', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ProcessingEvents', 'DetailsJson', "TEXT NOT NULL DEFAULT '{}'")
}

// ===========================================================================
// Schema migration
// ===========================================================================
export async function initializeDatabase(): Promise<void> {
  const pool = await getPool()

  if (isAzureSql) {
    // Azure SQL — ensure the talentmatch schema exists before running DDL
    await pool.request().query(
      "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch') EXEC('CREATE SCHEMA [talentmatch]')"
    )
    // Run the T-SQL schema with IF NOT EXISTS guards
    const schemaPath = resolve(import.meta.dirname, 'schema.sql')
    const schemaSql = readFileSync(schemaPath, 'utf-8')
    const batches = schemaSql
      .split(/\n(?=IF NOT EXISTS|CREATE (?:UNIQUE )?INDEX)/)
      .map(b => b.trim())
      .filter(b => b.length > 0 && !b.startsWith('--'))
    for (const batch of batches) {
      await pool.request().query(batch)
    }
  } else {
    // SQLite — run the DDL using the underlying db handle directly
    const schemaPath = resolve(import.meta.dirname, 'schema-sqlite.sql')
    const schemaSql = readFileSync(schemaPath, 'utf-8')
    if (sqliteTableExists(_sqliteDb, 'Users')) {
      _sqliteDb.exec(`CREATE TABLE IF NOT EXISTS DocumentBlobs (
        DocumentId TEXT NOT NULL PRIMARY KEY REFERENCES ApplicationDocuments(Id) ON DELETE CASCADE,
        Content TEXT NOT NULL
      )`)
      ensureSqliteCompatibilitySchema(_sqliteDb)
      _sqliteDb.exec(schemaSql)
    } else {
      _sqliteDb.exec(schemaSql)
      ensureSqliteCompatibilitySchema(_sqliteDb)
    }
  }

  console.log(`[db] Schema migration complete (${isAzureSql ? 'Azure SQL' : 'SQLite'})`)
}

