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
 
export let sql: any = {
  NVarChar: 'NVarChar',
  Int: 'Int',
  Float: 'Float',
  BigInt: 'BigInt',
  Bit: 'Bit',
  DateTime2: 'DateTime2',
}

// ---- Shared pool variable --------------------------------------------------
 
let _pool: any = null
 
let _sqliteDb: any = null

const azureSqlRetryConfig = {
  // Covers typical Azure SQL pay-as-you-go wake-up windows (~30-90s)
  maxAttempts: Number(process.env.AZURE_SQL_WAKEUP_MAX_ATTEMPTS ?? 8),
  initialDelayMs: Number(process.env.AZURE_SQL_WAKEUP_INITIAL_DELAY_MS ?? 2000),
  maxDelayMs: Number(process.env.AZURE_SQL_WAKEUP_MAX_DELAY_MS ?? 15000),
}

 
export async function getPool(): Promise<any> {
  if (_pool) return _pool
  if (isAzureSql) {
    _pool = await createMssqlPool()
  } else {
    _pool = createSqlitePool()
  }
  return _pool
}

export async function getStorageProvider(): Promise<import('./types.js').StorageProvider> {
  const [{ userRepo }, { organizationRepo }, { roleAssignmentRepo }, { accessManagementRepo }] = await Promise.all([
    import('./repos/user-repo.js'),
    import('./repos/organization-repo.js'),
    import('./repos/role-assignment-repo.js'),
    import('./repos/access-management-repo.js'),
  ])
  return { users: userRepo, organizations: organizationRepo, roleAssignments: roleAssignmentRepo, accessManagement: accessManagementRepo }
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
     
    (mssqlModule as any)?.default,
     
    (mssqlModule as any)?.sql,
  ]

  for (const candidate of candidates) {
     
    if (typeof (candidate as any)?.connect === 'function') {
       
      return (config: MssqlConnectConfig) => (candidate as any).connect(config)
    }
     
    if (typeof (candidate as any)?.ConnectionPool === 'function') {
       
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
  let transactionTail = Promise.resolve()

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
      // better-sqlite3 exposes one synchronous connection, so transaction owners queue.
      let committed = false
      let release: (() => void) | undefined
      return {
        async begin() {
          const previous = transactionTail
          transactionTail = new Promise<void>(resolve => { release = resolve })
          await previous
          try {
            db.prepare('BEGIN IMMEDIATE').run()
          } catch (error) {
            release?.()
            throw error
          }
        },
        request: () => makeRequest(db),
        commit() {
          try {
            db.prepare('COMMIT').run()
            committed = true
            return Promise.resolve()
          } finally {
            release?.()
          }
        },
        rollback() {
          try {
            if (!committed) db.prepare('ROLLBACK').run()
            return Promise.resolve()
          } finally {
            release?.()
          }
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

function ensureSqliteEntraUsersSchema(db: any): void {
  ensureSqliteColumn(db, 'Users', 'AuthenticationProvider', "TEXT NOT NULL DEFAULT 'simple'")
  ensureSqliteColumn(db, 'Users', 'EntraTenantId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Users', 'EntraObjectId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Users', 'IsActive', 'INTEGER NOT NULL DEFAULT 1')
  ensureSqliteColumn(db, 'Users', 'AuthorizationVersion', 'INTEGER NOT NULL DEFAULT 0')

  const passwordHash = db.prepare('PRAGMA table_info(Users)').all()
    .find((column: { name: string }) => column.name === 'PasswordHash') as { notnull: number } | undefined
  if (!passwordHash?.notnull) {
    db.exec(`
      DROP INDEX IF EXISTS UX_Users_Username;
      CREATE UNIQUE INDEX UX_Users_Username ON Users (Username) WHERE AuthenticationProvider = 'simple';
    `)
    return
  }

  db.exec(`
    PRAGMA foreign_keys = OFF;
    BEGIN;
    CREATE TABLE Users_EntraUpgrade (
      Id TEXT NOT NULL PRIMARY KEY,
      Username TEXT NOT NULL,
      Role TEXT NOT NULL,
      FullName TEXT NOT NULL DEFAULT '',
      Email TEXT NOT NULL DEFAULT '',
      Department TEXT NOT NULL DEFAULT '',
      PasswordHash TEXT NULL,
      CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
      LastLogin TEXT NULL,
      PasswordResetRequired INTEGER NOT NULL DEFAULT 0,
      AuthenticationProvider TEXT NOT NULL DEFAULT 'simple',
      EntraTenantId TEXT NULL,
      EntraObjectId TEXT NULL,
      IsActive INTEGER NOT NULL DEFAULT 1,
      AuthorizationVersion INTEGER NOT NULL DEFAULT 0,
      CHECK ((AuthenticationProvider = 'simple' AND PasswordHash IS NOT NULL AND EntraTenantId IS NULL AND EntraObjectId IS NULL) OR (AuthenticationProvider = 'entra' AND PasswordHash IS NULL AND EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL AND PasswordResetRequired = 0))
    );
    INSERT INTO Users_EntraUpgrade (Id, Username, Role, FullName, Email, Department, PasswordHash, CreatedAt, LastLogin, PasswordResetRequired, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive, AuthorizationVersion)
    SELECT Id, Username, Role, FullName, Email, Department, PasswordHash, CreatedAt, LastLogin, PasswordResetRequired, AuthenticationProvider, EntraTenantId, EntraObjectId, IsActive, AuthorizationVersion FROM Users;
    DROP TABLE Users;
    ALTER TABLE Users_EntraUpgrade RENAME TO Users;
    CREATE UNIQUE INDEX UX_Users_Username ON Users (Username) WHERE AuthenticationProvider = 'simple';
    CREATE UNIQUE INDEX UX_Users_EntraIdentity ON Users (EntraTenantId, EntraObjectId) WHERE AuthenticationProvider = 'entra';
    COMMIT;
    PRAGMA foreign_keys = ON;
  `)
}

function ensureSqliteAuthorizationAggregateSchema(db: any): void {
  ensureSqliteColumn(db, 'Users', 'AuthorizationVersion', 'INTEGER NOT NULL DEFAULT 0')
  if (!sqliteTableExists(db, 'OrganizationMemberships') || !sqliteTableExists(db, 'DepartmentMemberships')) return

  db.exec(`CREATE UNIQUE INDEX IF NOT EXISTS UQ_DepartmentMemberships_Id_User_Organization
    ON DepartmentMemberships (Id, UserId, OrganizationId)`)

  const hasDefault = sqliteTableHasColumn(db, 'OrganizationMemberships', 'DefaultDepartmentMembershipId')
  if (!hasDefault) {
    const ambiguous = db.prepare(`
      SELECT om.Id
      FROM OrganizationMemberships om
      LEFT JOIN DepartmentMemberships dm
        ON dm.UserId = om.UserId
       AND dm.OrganizationId = om.OrganizationId
       AND dm.Status = 'active'
      LEFT JOIN Departments d
        ON d.Id = dm.DepartmentId
       AND d.OrganizationId = dm.OrganizationId
       AND d.Status = 'active'
      WHERE om.Status = 'active'
      GROUP BY om.Id
      HAVING COUNT(*) <> 1 OR MAX(d.Id) IS NULL
      LIMIT 1
    `).get()
    if (ambiguous) {
      throw new Error('Every active organization membership requires an explicit default department; select one for memberships with zero or multiple candidates.')
    }

    db.pragma('foreign_keys = OFF')
    try {
      db.exec(`
        BEGIN;
        CREATE TABLE DepartmentMemberships_ExplicitDefaultUpgrade (
          Id TEXT NOT NULL PRIMARY KEY,
          UserId TEXT NOT NULL REFERENCES Users(Id),
          OrganizationId TEXT NOT NULL REFERENCES Organizations(Id),
          DefaultDepartmentMembershipId TEXT NULL,
          Status TEXT NOT NULL DEFAULT 'active',
          EffectiveAt TEXT NOT NULL DEFAULT (datetime('now')),
          RevokedAt TEXT NULL,
          UpdatedBy TEXT NOT NULL,
          FOREIGN KEY (DefaultDepartmentMembershipId, UserId, OrganizationId)
            REFERENCES DepartmentMemberships(Id, UserId, OrganizationId)
            DEFERRABLE INITIALLY DEFERRED,
          CHECK ((Status = 'active' AND RevokedAt IS NULL AND DefaultDepartmentMembershipId IS NOT NULL)
            OR (Status = 'revoked' AND RevokedAt IS NOT NULL))
        );
        INSERT INTO DepartmentMemberships_ExplicitDefaultUpgrade (
          Id, UserId, OrganizationId, DefaultDepartmentMembershipId, Status, EffectiveAt, RevokedAt, UpdatedBy
        )
        SELECT om.Id, om.UserId, om.OrganizationId,
          CASE WHEN om.Status = 'active' THEN (
            SELECT MIN(dm.Id)
            FROM DepartmentMemberships dm
            INNER JOIN Departments d ON d.Id = dm.DepartmentId AND d.OrganizationId = dm.OrganizationId
            WHERE dm.UserId = om.UserId
              AND dm.OrganizationId = om.OrganizationId
              AND dm.Status = 'active'
              AND d.Status = 'active'
          ) ELSE NULL END,
          om.Status, om.EffectiveAt, om.RevokedAt, om.UpdatedBy
        FROM OrganizationMemberships om;
        DROP TABLE OrganizationMemberships;
        ALTER TABLE DepartmentMemberships_ExplicitDefaultUpgrade RENAME TO OrganizationMemberships;
        CREATE UNIQUE INDEX UX_OrganizationMemberships_ActiveUserOrganization
          ON OrganizationMemberships (UserId, OrganizationId) WHERE Status = 'active';
        COMMIT;
      `)
    } catch (error) {
      if (db.inTransaction) db.exec('ROLLBACK')
      throw error
    } finally {
      db.pragma('foreign_keys = ON')
    }
  }

  db.exec(`
    DROP INDEX IF EXISTS UX_RoleAssignments_Idempotency;
    CREATE UNIQUE INDEX IF NOT EXISTS UX_RoleAssignments_ActiveDelegated
      ON RoleAssignments (TenantId, UserObjectId, Role, OrganizationId, DepartmentId)
      WHERE Status = 'active' AND Source = 'delegated';
  `)
}

function ensureSqliteCompatibilitySchema(db: any): void {
  ensureSqliteColumn(db, 'Users', 'PasswordResetRequired', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteEntraUsersSchema(db)

  ensureSqliteColumn(db, 'PasswordResetRequests', 'FullName', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'Reason', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'RequestedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'CreatedAt', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'PasswordResetRequests', 'ResolvedAt', 'TEXT NULL')
  ensureSqliteColumn(db, 'PasswordResetRequests', 'ResolvedBy', 'TEXT NULL')

  ensureSqliteColumn(db, 'Jobs', 'SpecDocumentId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Jobs', 'RubricDocumentId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Jobs', 'OrganizationId', 'TEXT NULL')
  ensureSqliteColumn(db, 'Jobs', 'DepartmentId', 'TEXT NULL')

  ensureSqliteColumn(db, 'JobConfigVersions', 'MustHavesJson', "TEXT NOT NULL DEFAULT '[]'")
  ensureSqliteColumn(db, 'JobConfigVersions', 'RunsPerApplication', 'INTEGER NOT NULL DEFAULT 3')

  ensureSqliteColumn(db, 'Applications', 'CandidateRef', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'Applications', 'CandidateName', 'TEXT NULL')
  ensureSqliteColumn(db, 'Applications', 'CandidateEmail', 'TEXT NULL')
  ensureSqliteColumn(db, 'Applications', 'Flagged', 'INTEGER NOT NULL DEFAULT 0')

  ensureSqliteColumn(db, 'ApplicationDocuments', 'MimeType', "TEXT NOT NULL DEFAULT ''")
  ensureSqliteColumn(db, 'ApplicationDocuments', 'SizeBytes', 'INTEGER NOT NULL DEFAULT 0')
  ensureSqliteColumn(db, 'ApplicationDocuments', 'UploadedAt', "TEXT NOT NULL DEFAULT ''")
  // Platform mode (spec 008): blob-by-reference
  ensureSqliteColumn(db, 'ApplicationDocuments', 'BlobUri', 'TEXT NULL')
  ensureSqliteColumn(db, 'ApplicationDocuments', 'ContentSha256', 'TEXT NULL')
  ensureSqliteColumn(db, 'Applications', 'BatchId', 'TEXT NULL')

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
  ensureSqliteColumn(db, 'ManualReviews', 'HumanEdited', 'INTEGER NOT NULL DEFAULT 0')

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

    // Must precede the batch loop; see the header of the upgrades file for why.
    const preBatchPath = resolve(import.meta.dirname, 'schema-pre-batch-upgrades.sql')
    const preBatchSql = readFileSync(preBatchPath, 'utf-8')
    const preBatchUpgrades = preBatchSql
      .split(/\r?\n\s*GO\s*(?:\r?\n|$)/)
      .map(b => b.trim())
      .filter(b => b.length > 0 && !b.split('\n').every(line => line.trim().startsWith('--')))
    for (const upgrade of preBatchUpgrades) {
      await pool.request().query(upgrade)
    }

    const batches = schemaSql
      .split(/\n(?=IF NOT EXISTS|CREATE (?:UNIQUE )?INDEX)/)
      .map(b => b.trim())
      .filter(b => b.length > 0 && !b.startsWith('--'))
    for (const batch of batches) {
      await pool.request().query(batch)
    }

    // Backward compatibility for existing Azure SQL environments.
    await pool.request().query(`
IF COL_LENGTH('talentmatch.ManualReviews', 'HumanEdited') IS NULL
BEGIN
  ALTER TABLE [talentmatch].ManualReviews
    ADD [HumanEdited] BIT NOT NULL CONSTRAINT DF_ManualReviews_HumanEdited DEFAULT 0;
END;
`)

    // Platform mode (spec 008): blob-by-reference + per-app batch linkage
    await pool.request().query(`
IF COL_LENGTH('talentmatch.ApplicationDocuments', 'BlobUri') IS NULL
  ALTER TABLE [talentmatch].ApplicationDocuments ADD BlobUri NVARCHAR(1024) NULL;
IF COL_LENGTH('talentmatch.ApplicationDocuments', 'ContentSha256') IS NULL
  ALTER TABLE [talentmatch].ApplicationDocuments ADD ContentSha256 CHAR(64) NULL;
IF COL_LENGTH('talentmatch.Applications', 'BatchId') IS NULL
  ALTER TABLE [talentmatch].Applications ADD BatchId UNIQUEIDENTIFIER NULL;
`)

    await pool.request().query(`
IF COL_LENGTH('talentmatch.Users', 'AuthenticationProvider') IS NULL
  ALTER TABLE [talentmatch].Users ADD AuthenticationProvider NVARCHAR(20) NOT NULL CONSTRAINT DF_Users_AuthenticationProvider DEFAULT 'simple';
IF COL_LENGTH('talentmatch.Users', 'EntraTenantId') IS NULL
  ALTER TABLE [talentmatch].Users ADD EntraTenantId NVARCHAR(36) NULL;
IF COL_LENGTH('talentmatch.Users', 'EntraObjectId') IS NULL
  ALTER TABLE [talentmatch].Users ADD EntraObjectId NVARCHAR(36) NULL;
IF COL_LENGTH('talentmatch.Users', 'IsActive') IS NULL
  ALTER TABLE [talentmatch].Users ADD IsActive BIT NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1;
IF COL_LENGTH('talentmatch.Users', 'AuthorizationVersion') IS NULL
  ALTER TABLE [talentmatch].Users ADD AuthorizationVersion INT NOT NULL CONSTRAINT DF_Users_AuthorizationVersion DEFAULT 0;
ALTER TABLE [talentmatch].Users ALTER COLUMN PasswordHash NVARCHAR(128) NULL;
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UX_Users_EntraIdentity' AND object_id = OBJECT_ID('talentmatch.Users'))
  CREATE UNIQUE INDEX UX_Users_EntraIdentity ON [talentmatch].Users (EntraTenantId, EntraObjectId) WHERE AuthenticationProvider = 'entra';

IF COL_LENGTH('talentmatch.Jobs', 'OrganizationId') IS NULL
  ALTER TABLE [talentmatch].Jobs ADD OrganizationId NVARCHAR(36) NULL;
IF COL_LENGTH('talentmatch.Jobs', 'DepartmentId') IS NULL
  ALTER TABLE [talentmatch].Jobs ADD DepartmentId NVARCHAR(36) NULL;
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Jobs_Organization_Department' AND object_id = OBJECT_ID('talentmatch.Jobs'))
  CREATE INDEX IX_Jobs_Organization_Department ON [talentmatch].Jobs (OrganizationId, DepartmentId);
`)

    await pool.request().query(`
IF COL_LENGTH('talentmatch.OrganizationMemberships', 'DefaultDepartmentMembershipId') IS NULL
  ALTER TABLE [talentmatch].OrganizationMemberships ADD DefaultDepartmentMembershipId NVARCHAR(36) NULL;

IF NOT EXISTS (
  SELECT 1 FROM sys.key_constraints
  WHERE name = 'UQ_DepartmentMemberships_Id_User_Organization'
    AND parent_object_id = OBJECT_ID('talentmatch.DepartmentMemberships')
)
  ALTER TABLE [talentmatch].DepartmentMemberships ADD CONSTRAINT UQ_DepartmentMemberships_Id_User_Organization UNIQUE (Id, UserId, OrganizationId);

UPDATE om
SET DefaultDepartmentMembershipId = candidate.Id
FROM [talentmatch].OrganizationMemberships om
CROSS APPLY (
  SELECT MIN(dm.Id) AS Id, COUNT(*) AS CandidateCount
  FROM [talentmatch].DepartmentMemberships dm
  INNER JOIN [talentmatch].Departments d ON d.Id = dm.DepartmentId AND d.OrganizationId = dm.OrganizationId
  WHERE dm.UserId = om.UserId
    AND dm.OrganizationId = om.OrganizationId
    AND dm.Status = 'active'
    AND d.Status = 'active'
) candidate
WHERE om.Status = 'active'
  AND om.DefaultDepartmentMembershipId IS NULL
  AND candidate.CandidateCount = 1;

IF EXISTS (
  SELECT 1
  FROM [talentmatch].OrganizationMemberships om
  WHERE om.Status = 'active' AND om.DefaultDepartmentMembershipId IS NULL
)
  THROW 51000, 'Every active organization membership requires an explicit default department; select one for memberships with zero or multiple candidates.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_OrganizationMemberships_DefaultDepartmentMembership')
  ALTER TABLE [talentmatch].OrganizationMemberships ADD CONSTRAINT FK_OrganizationMemberships_DefaultDepartmentMembership
    FOREIGN KEY (DefaultDepartmentMembershipId, UserId, OrganizationId)
    REFERENCES [talentmatch].DepartmentMemberships (Id, UserId, OrganizationId);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RoleAssignments_Idempotency' AND object_id = OBJECT_ID('talentmatch.RoleAssignments'))
  DROP INDEX UX_RoleAssignments_Idempotency ON [talentmatch].RoleAssignments;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RoleAssignments_ActiveDelegated' AND object_id = OBJECT_ID('talentmatch.RoleAssignments'))
  CREATE UNIQUE INDEX UX_RoleAssignments_ActiveDelegated ON [talentmatch].RoleAssignments (TenantId, UserObjectId, Role, OrganizationId, DepartmentId)
    WHERE Status = 'active' AND Source = 'delegated';
`)
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
      ensureSqliteAuthorizationAggregateSchema(_sqliteDb)
    } else {
      _sqliteDb.exec(schemaSql)
      ensureSqliteCompatibilitySchema(_sqliteDb)
      ensureSqliteAuthorizationAggregateSchema(_sqliteDb)
    }
  }

  console.log(`[db] Schema migration complete (${isAzureSql ? 'Azure SQL' : 'SQLite'})`)
}

