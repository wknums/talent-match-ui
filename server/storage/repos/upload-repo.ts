import { randomUUID } from 'node:crypto'
import {
  OPTIONAL_UPLOAD_DEFAULTS,
  type UploadItem,
  type UploadItemStatus,
  type UploadSessionDetail,
  type UploadSessionSummary,
} from '../../../src/types/index.js'
import { getPool, isAzureSql, sql } from '../db.js'
import { lockedTable, SCHEMA_NAME, T } from '../table-names.js'
import type {
  CompleteStoredUploadItem,
  CreateStoredUploadItem,
  CreateStoredUploadSession,
  SaveUploadSettingsInput,
  UploadRepository,
} from '../types.js'

const terminalStatuses = new Set<UploadItemStatus>([
  'succeeded',
  'skipped_duplicate',
  'failed',
  'interrupted',
])
const applicationDocumentColumnCache = new Map<string, boolean>()

async function hasApplicationDocumentColumn(
  transaction: any,
  columnName: string,
): Promise<boolean> {
  const cached = applicationDocumentColumnCache.get(columnName)
  if (cached !== undefined) return cached
  const request = transaction.request().input('columnName', sql.NVarChar, columnName)
  const result = isAzureSql
    ? await request
        .input('schemaName', sql.NVarChar, SCHEMA_NAME)
        .query(`SELECT 1 AS ExistsFlag
                FROM sys.columns c
                INNER JOIN sys.tables t ON t.object_id = c.object_id
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = @schemaName
                  AND t.name = 'ApplicationDocuments'
                  AND c.name = @columnName`)
    : await request.query(`SELECT 1 AS ExistsFlag
                           FROM pragma_table_info('ApplicationDocuments')
                           WHERE name = @columnName`)
  const exists = result.recordset.length > 0
  applicationDocumentColumnCache.set(columnName, exists)
  return exists
}

function iso(value: unknown): string {
  if (value instanceof Date) return value.toISOString()
  return String(value)
}

function nullableIso(value: unknown): string | null {
  return value === null || value === undefined ? null : iso(value)
}

function rowToItem(row: any): UploadItem {
  return {
    id: row.Id,
    sessionId: row.SessionId,
    occurrenceKey: row.OccurrenceKey,
    ordinal: Number(row.Ordinal),
    fileName: row.FileName,
    mimeType: row.MimeType,
    rawSizeBytes: Number(row.RawSizeBytes),
    status: row.Status,
    attemptCount: Number(row.AttemptCount),
    contentFingerprint: row.ContentFingerprint ?? null,
    applicationId: row.ApplicationId ?? null,
    outcomeCode: row.OutcomeCode ?? null,
    outcomeMessage: row.OutcomeMessage ?? null,
    nextRetryAt: nullableIso(row.NextRetryAt),
    createdAt: iso(row.CreatedAt),
    updatedAt: iso(row.UpdatedAt),
    completedAt: nullableIso(row.CompletedAt),
    concurrencyVersion: Number(row.ConcurrencyVersion),
  }
}

function rowToSummary(row: any): UploadSessionSummary {
  const total = Number(row.TotalItemCount)
  const terminal = Number(row.TerminalItemCount)
  return {
    id: row.Id,
    jobId: row.JobId,
    status: row.Status,
    allowDuplicates: row.AllowDuplicates === true || Number(row.AllowDuplicates) === 1,
    limits: {
      fileConcurrency: Number(row.FileConcurrency),
      maxIndividualFileBytes: Number(row.MaxIndividualFileBytes),
      maxInFlightBytes: Number(row.MaxInFlightBytes),
    },
    counts: {
      total,
      waitingOrThrottled: Number(row.WaitingCount),
      activeOrRetrying: Number(row.ActiveCount),
      succeeded: Number(row.SucceededCount),
      skipped: Number(row.SkippedCount),
      failed: Number(row.FailedCount),
      interrupted: Number(row.InterruptedCount),
      terminal,
    },
    progressPercent: total === 0 ? 0 : Math.floor(terminal / total * 100),
    correlationId: row.CorrelationId,
    createdAt: iso(row.CreatedAt),
    startedAt: nullableIso(row.StartedAt),
    lastHeartbeatAt: iso(row.LastHeartbeatAt),
    completedAt: nullableIso(row.CompletedAt),
    concurrencyVersion: Number(row.ConcurrencyVersion),
  }
}

async function selectSession(
  request: any,
  sessionId: string,
  ownerActorId: string,
  lock = false,
): Promise<any | undefined> {
  const table = lock ? lockedTable('UploadSessions', 's') : `${T('UploadSessions')} s`
  const result = await request
    .input('sessionId', sql.NVarChar, sessionId)
    .input('ownerActorId', sql.NVarChar, ownerActorId)
    .query(`SELECT s.* FROM ${table} WHERE s.Id = @sessionId AND s.OwnerActorId = @ownerActorId`)
  return result.recordset[0]
}

async function selectItem(
  request: any,
  sessionId: string,
  itemId: string,
  ownerActorId: string,
  lock = false,
): Promise<any | undefined> {
  const itemTable = lock ? lockedTable('UploadItems', 'i') : `${T('UploadItems')} i`
  const result = await request
    .input('sessionId', sql.NVarChar, sessionId)
    .input('itemId', sql.NVarChar, itemId)
    .input('ownerActorId', sql.NVarChar, ownerActorId)
    .query(`SELECT i.*
            FROM ${itemTable}
            INNER JOIN ${T('UploadSessions')} s ON s.Id = i.SessionId
            WHERE i.Id = @itemId AND i.SessionId = @sessionId AND s.OwnerActorId = @ownerActorId`)
  return result.recordset[0]
}

async function appendEvent(
  request: any,
  actor: string,
  action: string,
  entityType: string,
  entityId: string,
  details: Record<string, unknown>,
  correlationId: string,
  timestamp: string,
): Promise<void> {
  await request
    .input('eventId', sql.NVarChar, randomUUID())
    .input('eventActor', sql.NVarChar, actor)
    .input('eventAction', sql.NVarChar, action)
    .input('eventEntityType', sql.NVarChar, entityType)
    .input('eventEntityId', sql.NVarChar, entityId)
    .input('eventDetails', sql.NVarChar, JSON.stringify(details))
    .input('eventTimestamp', sql.DateTime2, new Date(timestamp))
    .input('eventCorrelationId', sql.NVarChar, correlationId)
    .query(`INSERT INTO ${T('ProcessingEvents')}
      (Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId)
      VALUES (@eventId, @eventActor, @eventAction, @eventEntityType, @eventEntityId, @eventDetails, @eventTimestamp, @eventCorrelationId)`)
}

async function refreshAggregates(
  request: any,
  sessionId: string,
  now: string,
  incrementVersion = true,
): Promise<void> {
  const result = await request
    .input('aggregateSessionId', sql.NVarChar, sessionId)
    .query(`SELECT Status, COUNT(*) AS ItemCount
            FROM ${T('UploadItems')}
            WHERE SessionId = @aggregateSessionId
            GROUP BY Status`)
  const counts = new Map<string, number>(
    result.recordset.map((row: any) => [String(row.Status), Number(row.ItemCount)]),
  )
  const waiting = (counts.get('waiting') ?? 0) + (counts.get('throttled') ?? 0)
  const active = (counts.get('uploading') ?? 0) + (counts.get('retrying') ?? 0)
  const succeeded = counts.get('succeeded') ?? 0
  const skipped = counts.get('skipped_duplicate') ?? 0
  const failed = counts.get('failed') ?? 0
  const interrupted = counts.get('interrupted') ?? 0
  const terminal = succeeded + skipped + failed + interrupted
  const total = [...counts.values()].reduce((sum, count) => sum + count, 0)
  const completed = total > 0 && terminal === total
  await request
    .input('refreshSessionId', sql.NVarChar, sessionId)
    .input('waitingCount', sql.Int, waiting)
    .input('activeCount', sql.Int, active)
    .input('succeededCount', sql.Int, succeeded)
    .input('skippedCount', sql.Int, skipped)
    .input('failedCount', sql.Int, failed)
    .input('interruptedCount', sql.Int, interrupted)
    .input('terminalItemCount', sql.Int, terminal)
    .input('sessionStatus', sql.NVarChar, completed ? 'completed' : 'active')
    .input('completedAt', sql.DateTime2, completed ? new Date(now) : null)
    .query(`UPDATE ${T('UploadSessions')}
            SET WaitingCount = @waitingCount,
                ActiveCount = @activeCount,
                SucceededCount = @succeededCount,
                SkippedCount = @skippedCount,
                FailedCount = @failedCount,
                InterruptedCount = @interruptedCount,
                TerminalItemCount = @terminalItemCount,
                Status = @sessionStatus,
                CompletedAt = @completedAt
                ${incrementVersion ? ', ConcurrencyVersion = ConcurrencyVersion + 1' : ''}
            WHERE Id = @refreshSessionId`)
}

async function getDetailWithRequest(
  request: any,
  sessionId: string,
  ownerActorId: string,
): Promise<UploadSessionDetail | undefined> {
  const session = await selectSession(request, sessionId, ownerActorId)
  if (!session) return undefined
  const result = await request
    .input('detailSessionId', sql.NVarChar, sessionId)
    .query(`SELECT * FROM ${T('UploadItems')} WHERE SessionId = @detailSessionId ORDER BY Ordinal`)
  return { ...rowToSummary(session), items: result.recordset.map(rowToItem) }
}

async function withTransaction<T>(work: (transaction: any) => Promise<T>): Promise<T> {
  const pool = await getPool()
  const transaction = pool.transaction()
  await transaction.begin()
  try {
    const result = await work(transaction)
    await transaction.commit()
    return result
  } catch (error) {
    await transaction.rollback()
    throw error
  }
}

export const uploadRepo: UploadRepository = {
  async getSettings() {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, 'optional-file-upload')
      .query(`SELECT * FROM ${T('UploadSettings')} WHERE Id = @id`)
    const row = result.recordset[0]
    if (!row) {
      return {
        ...OPTIONAL_UPLOAD_DEFAULTS,
        concurrencyVersion: 0,
        persisted: false,
        updatedAt: null,
        updatedBy: null,
      }
    }
    return {
      fileConcurrency: Number(row.FileConcurrency),
      maxIndividualFileBytes: Number(row.MaxIndividualFileBytes),
      maxInFlightBytes: Number(row.MaxInFlightBytes),
      concurrencyVersion: Number(row.ConcurrencyVersion),
      persisted: true,
      updatedAt: iso(row.UpdatedAt),
      updatedBy: row.UpdatedBy,
    }
  },

  async saveSettings(input: SaveUploadSettingsInput) {
    return withTransaction(async transaction => {
      const request = transaction.request()
      const result = await request
        .input('id', sql.NVarChar, 'optional-file-upload')
        .query(`SELECT * FROM ${lockedTable('UploadSettings')} WHERE Id = @id`)
      const current = result.recordset[0]
      if (Number(current?.ConcurrencyVersion ?? 0) !== input.expectedConcurrencyVersion) {
        throw Object.assign(new Error('The upload settings have changed.'), {
          code: 'stale_version',
          statusCode: 409,
        })
      }
      const version = input.expectedConcurrencyVersion + 1
      const mutation = transaction.request()
        .input('id', sql.NVarChar, 'optional-file-upload')
        .input('fileConcurrency', sql.Int, input.fileConcurrency)
        .input('maxIndividualFileBytes', sql.BigInt, input.maxIndividualFileBytes)
        .input('maxInFlightBytes', sql.BigInt, input.maxInFlightBytes)
        .input('concurrencyVersion', sql.Int, version)
        .input('createdAt', sql.DateTime2, new Date(input.now))
        .input('createdBy', sql.NVarChar, current?.CreatedBy ?? input.actorId)
        .input('updatedAt', sql.DateTime2, new Date(input.now))
        .input('updatedBy', sql.NVarChar, input.actorId)
      if (current) {
        await mutation.query(`UPDATE ${T('UploadSettings')}
          SET FileConcurrency = @fileConcurrency,
              MaxIndividualFileBytes = @maxIndividualFileBytes,
              MaxInFlightBytes = @maxInFlightBytes,
              ConcurrencyVersion = @concurrencyVersion,
              UpdatedAt = @updatedAt,
              UpdatedBy = @updatedBy
          WHERE Id = @id`)
      } else {
        await mutation.query(`INSERT INTO ${T('UploadSettings')}
          (Id, FileConcurrency, MaxIndividualFileBytes, MaxInFlightBytes, ConcurrencyVersion, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
          VALUES (@id, @fileConcurrency, @maxIndividualFileBytes, @maxInFlightBytes, @concurrencyVersion, @createdAt, @createdBy, @updatedAt, @updatedBy)`)
      }
      return {
        fileConcurrency: input.fileConcurrency,
        maxIndividualFileBytes: input.maxIndividualFileBytes,
        maxInFlightBytes: input.maxInFlightBytes,
        concurrencyVersion: version,
        persisted: true,
        updatedAt: input.now,
        updatedBy: input.actorId,
      }
    })
  },

  async createSession(session: CreateStoredUploadSession, items: CreateStoredUploadItem[]) {
    return withTransaction(async transaction => {
      const settingsResult = await transaction.request()
        .input('settingsId', sql.NVarChar, 'optional-file-upload')
        .query(`SELECT * FROM ${lockedTable('UploadSettings')} WHERE Id = @settingsId`)
      const currentSettings = settingsResult.recordset[0]
      const currentSettingsVersion = Number(currentSettings?.ConcurrencyVersion ?? 0)
      if (currentSettingsVersion !== session.settingsConcurrencyVersion) {
        throw Object.assign(new Error('Upload settings changed while the session was being created.'), {
          code: 'settings_stale',
          statusCode: 409,
        })
      }
      const terminal = items.filter(item => terminalStatuses.has(item.status)).length
      const failed = items.filter(item => item.status === 'failed').length
      const request = transaction.request()
        .input('id', sql.NVarChar, session.id)
        .input('jobId', sql.NVarChar, session.jobId)
        .input('ownerActorId', sql.NVarChar, session.ownerActorId)
        .input('ownerDisplayName', sql.NVarChar, session.ownerDisplayName)
        .input('allowDuplicates', sql.Bit, session.allowDuplicates ? 1 : 0)
        .input('status', sql.NVarChar, terminal === items.length ? 'completed' : 'active')
        .input('fileConcurrency', sql.Int, session.fileConcurrency)
        .input('maxIndividualFileBytes', sql.BigInt, session.maxIndividualFileBytes)
        .input('maxInFlightBytes', sql.BigInt, session.maxInFlightBytes)
        .input('totalItemCount', sql.Int, items.length)
        .input('waitingCount', sql.Int, items.length - terminal)
        .input('failedCount', sql.Int, failed)
        .input('terminalItemCount', sql.Int, terminal)
        .input('correlationId', sql.NVarChar, session.correlationId)
        .input('now', sql.DateTime2, new Date(session.createdAt))
        .input('completedAt', sql.DateTime2, terminal === items.length ? new Date(session.createdAt) : null)
      await request.query(`INSERT INTO ${T('UploadSessions')} (
        Id, JobId, OwnerActorId, OwnerDisplayName, AllowDuplicates, Status,
        FileConcurrency, MaxIndividualFileBytes, MaxInFlightBytes,
        TotalItemCount, WaitingCount, ActiveCount, SucceededCount, SkippedCount,
        FailedCount, InterruptedCount, TerminalItemCount, CorrelationId,
        LastHeartbeatAt, CreatedAt, StartedAt, CompletedAt, ConcurrencyVersion)
        VALUES (
        @id, @jobId, @ownerActorId, @ownerDisplayName, @allowDuplicates, @status,
        @fileConcurrency, @maxIndividualFileBytes, @maxInFlightBytes,
        @totalItemCount, @waitingCount, 0, 0, 0,
        @failedCount, 0, @terminalItemCount, @correlationId,
        @now, @now, NULL, @completedAt, 1)`)
      for (const item of items) {
        await transaction.request()
          .input('id', sql.NVarChar, item.id)
          .input('sessionId', sql.NVarChar, session.id)
          .input('occurrenceKey', sql.NVarChar, item.occurrenceKey)
          .input('ordinal', sql.Int, item.ordinal)
          .input('fileName', sql.NVarChar, item.fileName)
          .input('mimeType', sql.NVarChar, item.mimeType)
          .input('rawSizeBytes', sql.BigInt, item.rawSizeBytes)
          .input('status', sql.NVarChar, item.status)
          .input('outcomeCode', sql.NVarChar, item.outcomeCode)
          .input('outcomeMessage', sql.NVarChar, item.outcomeMessage)
          .input('createdAt', sql.DateTime2, new Date(item.createdAt))
          .input('completedAt', sql.DateTime2, item.completedAt ? new Date(item.completedAt) : null)
          .query(`INSERT INTO ${T('UploadItems')} (
            Id, SessionId, OccurrenceKey, Ordinal, FileName, MimeType, RawSizeBytes,
            Status, AttemptCount, ContentFingerprint, ApplicationId, OutcomeCode,
            OutcomeMessage, LastHttpStatus, LastAttemptAt, NextRetryAt, CreatedAt,
            UpdatedAt, CompletedAt, ConcurrencyVersion)
            VALUES (
            @id, @sessionId, @occurrenceKey, @ordinal, @fileName, @mimeType, @rawSizeBytes,
            @status, 0, NULL, NULL, @outcomeCode,
            @outcomeMessage, NULL, NULL, NULL, @createdAt,
            @createdAt, @completedAt, 1)`)
      }
      await appendEvent(
        transaction.request(),
        session.ownerActorId,
        'upload-session.created',
        'UploadSession',
        session.id,
        {
          jobId: session.jobId,
          itemCount: items.length,
          allowDuplicates: session.allowDuplicates,
          limits: {
            fileConcurrency: session.fileConcurrency,
            maxIndividualFileBytes: session.maxIndividualFileBytes,
            maxInFlightBytes: session.maxInFlightBytes,
          },
        },
        session.correlationId,
        session.createdAt,
      )
      const detail = await getDetailWithRequest(
        transaction.request(),
        session.id,
        session.ownerActorId,
      )
      if (!detail) throw new Error('Created upload session could not be reloaded.')
      return detail
    })
  },

  async getOwnedSession(sessionId, ownerActorId, includeItems = true) {
    const pool = await getPool()
    if (includeItems) {
      return getDetailWithRequest(pool.request(), sessionId, ownerActorId)
    }
    const row = await selectSession(pool.request(), sessionId, ownerActorId)
    return row ? { ...rowToSummary(row), items: [] } : undefined
  },

  async listOwnedSessions(ownerActorId, jobId, includeTerminal = true) {
    const pool = await getPool()
    const request = pool.request().input('ownerActorId', sql.NVarChar, ownerActorId)
    let where = 'OwnerActorId = @ownerActorId'
    if (jobId) {
      request.input('jobId', sql.NVarChar, jobId)
      where += ' AND JobId = @jobId'
    }
    if (!includeTerminal) where += " AND Status <> 'completed'"
    const result = await request.query(
      `SELECT * FROM ${T('UploadSessions')} WHERE ${where} ORDER BY CreatedAt DESC`,
    )
    return result.recordset.map(rowToSummary)
  },

  async getOwnedItem(sessionId, itemId, ownerActorId) {
    const pool = await getPool()
    const row = await selectItem(pool.request(), sessionId, itemId, ownerActorId)
    return row ? rowToItem(row) : undefined
  },

  async startItemAttempt(sessionId, itemId, ownerActorId, occurrenceKey, now) {
    return withTransaction(async transaction => {
      const session = await selectSession(transaction.request(), sessionId, ownerActorId, true)
      if (!session) throw Object.assign(new Error('Upload session was not found.'), { code: 'not_found', statusCode: 404 })
      const row = await selectItem(transaction.request(), sessionId, itemId, ownerActorId, true)
      if (!row) throw Object.assign(new Error('Upload item was not found.'), { code: 'not_found', statusCode: 404 })
      if (row.OccurrenceKey !== occurrenceKey) throw Object.assign(new Error('Occurrence key does not match.'), { code: 'occurrence_key_mismatch', statusCode: 409 })
      if (terminalStatuses.has(row.Status)) return rowToItem(row)
      if (!['waiting', 'throttled', 'retrying'].includes(row.Status)) {
        throw Object.assign(new Error('The upload item is already in progress.'), { code: 'attempt_not_active', statusCode: 409 })
      }
      if (Number(row.AttemptCount) >= 4) {
        throw Object.assign(new Error('The upload item has exhausted its attempts.'), { code: 'attempt_exhausted', statusCode: 409 })
      }
      await transaction.request()
        .input('itemId', sql.NVarChar, itemId)
        .input('now', sql.DateTime2, new Date(now))
        .query(`UPDATE ${T('UploadItems')}
          SET Status = 'uploading',
              AttemptCount = AttemptCount + 1,
              LastAttemptAt = @now,
              NextRetryAt = NULL,
              UpdatedAt = @now,
              ConcurrencyVersion = ConcurrencyVersion + 1
          WHERE Id = @itemId`)
      await transaction.request()
        .input('sessionId', sql.NVarChar, sessionId)
        .input('now', sql.DateTime2, new Date(now))
        .query(`UPDATE ${T('UploadSessions')}
          SET StartedAt = COALESCE(StartedAt, @now)
          WHERE Id = @sessionId`)
      await refreshAggregates(transaction.request(), sessionId, now)
      const updated = await selectItem(transaction.request(), sessionId, itemId, ownerActorId)
      await appendEvent(
        transaction.request(),
        ownerActorId,
        'upload-item.state-changed',
        'UploadItem',
        itemId,
        {
          previousStatus: row.Status,
          newStatus: 'uploading',
          attempt: Number(updated.AttemptCount),
          rawSizeBytes: Number(row.RawSizeBytes),
        },
        session.CorrelationId,
        now,
      )
      return rowToItem(updated)
    })
  },

  async completeItem(input: CompleteStoredUploadItem) {
    return withTransaction(async transaction => {
      const session = await selectSession(
        transaction.request(),
        input.sessionId,
        input.ownerActorId,
        true,
      )
      if (!session) throw Object.assign(new Error('Upload session was not found.'), { code: 'not_found', statusCode: 404 })
      const row = await selectItem(
        transaction.request(),
        input.sessionId,
        input.itemId,
        input.ownerActorId,
        true,
      )
      if (!row) throw Object.assign(new Error('Upload item was not found.'), { code: 'not_found', statusCode: 404 })
      if (row.OccurrenceKey !== input.occurrenceKey) throw Object.assign(new Error('Occurrence key does not match.'), { code: 'occurrence_key_mismatch', statusCode: 409 })
      if (terminalStatuses.has(row.Status)) return rowToItem(row)
      if (row.Status !== 'uploading') throw Object.assign(new Error('The upload item is not active.'), { code: 'attempt_not_active', statusCode: 409 })

      if (!(session.AllowDuplicates === true || Number(session.AllowDuplicates) === 1)) {
        await transaction.request()
          .input('duplicateBoundaryJobId', sql.NVarChar, session.JobId)
          .query(`SELECT Id
                  FROM ${lockedTable('Jobs')}
                  WHERE Id = @duplicateBoundaryJobId`)
        const duplicate = await transaction.request()
          .input('sessionId', sql.NVarChar, input.sessionId)
          .input('itemId', sql.NVarChar, input.itemId)
          .input('fingerprint', sql.NVarChar, input.fingerprint)
          .input('jobId', sql.NVarChar, session.JobId)
          .query(`SELECT
            CASE WHEN EXISTS (
              SELECT 1 FROM ${T('UploadItems')}
              WHERE SessionId = @sessionId
                AND Id <> @itemId
                AND ContentFingerprint = @fingerprint
                AND Status IN ('uploading', 'succeeded')
            ) THEN 1 ELSE 0 END AS SameSession,
            CASE WHEN EXISTS (
              SELECT 1
              FROM ${T('ApplicationDocuments')} d
              INNER JOIN ${T('Applications')} a ON a.Id = d.ApplicationId
              WHERE a.JobId = @jobId AND d.Fingerprint = @fingerprint
            ) THEN 1 ELSE 0 END AS ExistingJob`)
        const sameSession = Number(duplicate.recordset[0]?.SameSession) === 1
        const existingJob = Number(duplicate.recordset[0]?.ExistingJob) === 1
        if (sameSession || existingJob) {
          const outcomeCode = existingJob ? 'duplicate_existing' : 'duplicate_selection'
          const outcomeMessage = existingJob
            ? 'This document already belongs to the job.'
            : 'The same document was already selected for this upload session.'
          await transaction.request()
            .input('itemId', sql.NVarChar, input.itemId)
            .input('fingerprint', sql.NVarChar, input.fingerprint)
            .input('outcomeCode', sql.NVarChar, outcomeCode)
            .input('outcomeMessage', sql.NVarChar, outcomeMessage)
            .input('now', sql.DateTime2, new Date(input.now))
            .query(`UPDATE ${T('UploadItems')}
              SET ContentFingerprint = @fingerprint,
                  Status = 'skipped_duplicate',
                  OutcomeCode = @outcomeCode,
                  OutcomeMessage = @outcomeMessage,
                  UpdatedAt = @now,
                  CompletedAt = @now,
                  ConcurrencyVersion = ConcurrencyVersion + 1
              WHERE Id = @itemId`)
          await refreshAggregates(transaction.request(), input.sessionId, input.now)
          const updated = await selectItem(
            transaction.request(),
            input.sessionId,
            input.itemId,
            input.ownerActorId,
          )
          await appendEvent(
            transaction.request(),
            input.ownerActorId,
            'upload-item.completed',
            'UploadItem',
            input.itemId,
            {
              status: 'skipped_duplicate',
              attempt: Number(updated.AttemptCount),
              rawSizeBytes: input.rawSizeBytes,
              outcomeCode,
            },
            session.CorrelationId,
            input.now,
          )
          return rowToItem(updated)
        }
      }

      const applicationId = randomUUID()
      const documentId = randomUUID()
      const candidateName = input.fileName
        .replace(/\.[^.]+$/, '')
        .replace(/[_-]/g, ' ')
        .trim() || 'Unknown Applicant'
      await transaction.request()
        .input('applicationId', sql.NVarChar, applicationId)
        .input('jobId', sql.NVarChar, session.JobId)
        .input('candidateRef', sql.NVarChar, `candidate-${applicationId.slice(0, 8)}`)
        .input('candidateName', sql.NVarChar, candidateName)
        .input('createdAt', sql.DateTime2, new Date(input.now))
        .query(`INSERT INTO ${T('Applications')}
          (Id, JobId, CandidateRef, CandidateName, CandidateEmail, Status, CreatedAt, UpdatedAt, Flagged, TestRunId)
          VALUES (@applicationId, @jobId, @candidateRef, @candidateName, NULL, 'Queued', @createdAt, @createdAt, 0, NULL)`)
      const documentColumns = [
        'Id',
        'ApplicationId',
        'FileName',
        'MimeType',
        'SizeBytes',
        'Fingerprint',
        'UploadedAt',
      ]
      const documentValues = [
        '@documentId',
        '@applicationId',
        '@fileName',
        '@mimeType',
        '@rawSizeBytes',
        '@fingerprint',
        '@now',
      ]
      if (await hasApplicationDocumentColumn(transaction, 'FileType')) {
        documentColumns.push('FileType')
        documentValues.push('@mimeType')
      }
      if (await hasApplicationDocumentColumn(transaction, 'FileSize')) {
        documentColumns.push('FileSize')
        documentValues.push('@rawSizeBytes')
      }
      if (await hasApplicationDocumentColumn(transaction, 'ContentBase64')) {
        documentColumns.push('ContentBase64')
        documentValues.push('@contentBase64')
      }
      if (await hasApplicationDocumentColumn(transaction, 'UploadTimestamp')) {
        documentColumns.push('UploadTimestamp')
        documentValues.push('@now')
      }
      await transaction.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('applicationId', sql.NVarChar, applicationId)
        .input('fileName', sql.NVarChar, input.fileName)
        .input('mimeType', sql.NVarChar, input.mimeType)
        .input('rawSizeBytes', sql.BigInt, input.rawSizeBytes)
        .input('fingerprint', sql.NVarChar, input.fingerprint)
        .input('contentBase64', sql.NVarChar, input.contentBase64)
        .input('now', sql.DateTime2, new Date(input.now))
        .query(`INSERT INTO ${T('ApplicationDocuments')}
          (${documentColumns.join(', ')})
          VALUES (${documentValues.join(', ')})`)
      await transaction.request()
        .input('documentId', sql.NVarChar, documentId)
        .input('content', sql.NVarChar, input.contentBase64)
        .query(`INSERT INTO ${T('DocumentBlobs')} (DocumentId, Content) VALUES (@documentId, @content)`)
      await transaction.request()
        .input('itemId', sql.NVarChar, input.itemId)
        .input('applicationId', sql.NVarChar, applicationId)
        .input('fingerprint', sql.NVarChar, input.fingerprint)
        .input('now', sql.DateTime2, new Date(input.now))
        .query(`UPDATE ${T('UploadItems')}
          SET ContentFingerprint = @fingerprint,
              ApplicationId = @applicationId,
              Status = 'succeeded',
              OutcomeCode = NULL,
              OutcomeMessage = NULL,
              UpdatedAt = @now,
              CompletedAt = @now,
              ConcurrencyVersion = ConcurrencyVersion + 1
          WHERE Id = @itemId`)
      await refreshAggregates(transaction.request(), input.sessionId, input.now)
      const updated = await selectItem(
        transaction.request(),
        input.sessionId,
        input.itemId,
        input.ownerActorId,
      )
      await appendEvent(
        transaction.request(),
        input.ownerActorId,
        'upload-item.completed',
        'UploadItem',
        input.itemId,
        {
          status: 'succeeded',
          applicationId,
          attempt: Number(updated.AttemptCount),
          rawSizeBytes: input.rawSizeBytes,
          outcomeCode: null,
        },
        session.CorrelationId,
        input.now,
      )
      const refreshedSession = await selectSession(
        transaction.request(),
        input.sessionId,
        input.ownerActorId,
      )
      if (refreshedSession.Status === 'completed') {
        await appendEvent(
          transaction.request(),
          input.ownerActorId,
          'upload-session.completed',
          'UploadSession',
          input.sessionId,
          {
            succeeded: Number(refreshedSession.SucceededCount),
            skipped: Number(refreshedSession.SkippedCount),
            failed: Number(refreshedSession.FailedCount),
            interrupted: Number(refreshedSession.InterruptedCount),
          },
          session.CorrelationId,
          input.now,
        )
      }
      return rowToItem(updated)
    })
  },

  async recordItemOutcome(
    sessionId,
    itemId,
    ownerActorId,
    status,
    outcomeCode,
    outcomeMessage,
    now,
    lastHttpStatus = null,
    nextRetryAt = null,
  ) {
    return withTransaction(async transaction => {
      const session = await selectSession(transaction.request(), sessionId, ownerActorId, true)
      if (!session) throw Object.assign(new Error('Upload session was not found.'), { code: 'not_found', statusCode: 404 })
      const row = await selectItem(transaction.request(), sessionId, itemId, ownerActorId, true)
      if (!row) throw Object.assign(new Error('Upload item was not found.'), { code: 'not_found', statusCode: 404 })
      if (terminalStatuses.has(row.Status)) return rowToItem(row)
      await transaction.request()
        .input('itemId', sql.NVarChar, itemId)
        .input('status', sql.NVarChar, status)
        .input('outcomeCode', sql.NVarChar, outcomeCode)
        .input('outcomeMessage', sql.NVarChar, outcomeMessage)
        .input('lastHttpStatus', sql.Int, lastHttpStatus)
        .input('nextRetryAt', sql.DateTime2, nextRetryAt ? new Date(nextRetryAt) : null)
        .input('completedAt', sql.DateTime2, terminalStatuses.has(status) ? new Date(now) : null)
        .input('now', sql.DateTime2, new Date(now))
        .query(`UPDATE ${T('UploadItems')}
          SET Status = @status,
              OutcomeCode = @outcomeCode,
              OutcomeMessage = @outcomeMessage,
              LastHttpStatus = @lastHttpStatus,
              NextRetryAt = @nextRetryAt,
              CompletedAt = @completedAt,
              UpdatedAt = @now,
              ConcurrencyVersion = ConcurrencyVersion + 1
          WHERE Id = @itemId`)
      await refreshAggregates(transaction.request(), sessionId, now)
      const updated = await selectItem(transaction.request(), sessionId, itemId, ownerActorId)
      await appendEvent(
        transaction.request(),
        ownerActorId,
        terminalStatuses.has(status) ? 'upload-item.completed' : 'upload-item.state-changed',
        'UploadItem',
        itemId,
        {
          previousStatus: row.Status,
          status,
          attempt: Number(updated.AttemptCount),
          rawSizeBytes: Number(row.RawSizeBytes),
          outcomeCode,
        },
        session.CorrelationId,
        now,
      )
      return rowToItem(updated)
    })
  },

  async updateItemStatus(sessionId, itemId, ownerActorId, request, now) {
    const allowed = new Set(['waiting', 'throttled', 'retrying', 'failed', 'interrupted'])
    if (!allowed.has(request.status)) {
      throw Object.assign(new Error('The requested client status is invalid.'), {
        code: 'validation_error',
        statusCode: 422,
      })
    }
    return withTransaction(async transaction => {
      const session = await selectSession(transaction.request(), sessionId, ownerActorId, true)
      if (!session) throw Object.assign(new Error('Upload session was not found.'), { code: 'not_found', statusCode: 404 })
      const row = await selectItem(transaction.request(), sessionId, itemId, ownerActorId, true)
      if (!row) throw Object.assign(new Error('Upload item was not found.'), { code: 'not_found', statusCode: 404 })
      if (row.OccurrenceKey !== request.occurrenceKey) throw Object.assign(new Error('Occurrence key does not match.'), { code: 'state_conflict', statusCode: 409 })
      if (terminalStatuses.has(row.Status)) return rowToItem(row)
      if (Number(row.ConcurrencyVersion) !== request.expectedConcurrencyVersion) throw Object.assign(new Error('The upload item has changed.'), { code: 'stale_version', statusCode: 409 })
      await transaction.request()
        .input('itemId', sql.NVarChar, itemId)
        .input('status', sql.NVarChar, request.status)
        .input('outcomeCode', sql.NVarChar, request.outcomeCode ?? null)
        .input('outcomeMessage', sql.NVarChar, request.outcomeMessage ?? null)
        .input('nextRetryAt', sql.DateTime2, request.nextRetryAt ? new Date(request.nextRetryAt) : null)
        .input('completedAt', sql.DateTime2, terminalStatuses.has(request.status) ? new Date(now) : null)
        .input('now', sql.DateTime2, new Date(now))
        .query(`UPDATE ${T('UploadItems')}
          SET Status = @status,
              OutcomeCode = @outcomeCode,
              OutcomeMessage = @outcomeMessage,
              NextRetryAt = @nextRetryAt,
              CompletedAt = @completedAt,
              UpdatedAt = @now,
              ConcurrencyVersion = ConcurrencyVersion + 1
          WHERE Id = @itemId`)
      await refreshAggregates(transaction.request(), sessionId, now)
      const updated = await selectItem(transaction.request(), sessionId, itemId, ownerActorId)
      await appendEvent(
        transaction.request(),
        ownerActorId,
        terminalStatuses.has(request.status) ? 'upload-item.completed' : 'upload-item.state-changed',
        'UploadItem',
        itemId,
        {
          previousStatus: row.Status,
          newStatus: request.status,
          attempt: Number(updated.AttemptCount),
          transportAttemptCount: request.transportAttemptCount ?? null,
          outcomeCode: request.outcomeCode ?? null,
        },
        session.CorrelationId,
        now,
      )
      return rowToItem(updated)
    })
  },

  async heartbeat(sessionId, ownerActorId, expectedConcurrencyVersion, now) {
    return withTransaction(async transaction => {
      const row = await selectSession(transaction.request(), sessionId, ownerActorId, true)
      if (!row) throw Object.assign(new Error('Upload session was not found.'), { code: 'not_found', statusCode: 404 })
      if (row.Status === 'completed' || Number(row.ConcurrencyVersion) !== expectedConcurrencyVersion) {
        throw Object.assign(new Error('The upload session has changed.'), { code: 'stale_version', statusCode: 409 })
      }
      await transaction.request()
        .input('sessionId', sql.NVarChar, sessionId)
        .input('now', sql.DateTime2, new Date(now))
        .query(`UPDATE ${T('UploadSessions')}
          SET LastHeartbeatAt = @now, ConcurrencyVersion = ConcurrencyVersion + 1
          WHERE Id = @sessionId`)
      const updated = await selectSession(transaction.request(), sessionId, ownerActorId)
      await appendEvent(
        transaction.request(),
        ownerActorId,
        'upload-session.heartbeat',
        'UploadSession',
        sessionId,
        { activeItemCount: Number(updated.ActiveCount) },
        row.CorrelationId,
        now,
      )
      return rowToSummary(updated)
    })
  },

  async reconcileStale(sessionId, ownerActorId, staleBefore, now) {
    return withTransaction(async transaction => {
      const row = await selectSession(transaction.request(), sessionId, ownerActorId, true)
      if (!row) return undefined
      if (row.Status !== 'completed' && iso(row.LastHeartbeatAt) < staleBefore) {
        await transaction.request()
          .input('sessionId', sql.NVarChar, sessionId)
          .input('outcomeCode', sql.NVarChar, 'tab_interrupted')
          .input('outcomeMessage', sql.NVarChar, 'The originating browser tab is no longer uploading this file.')
          .input('now', sql.DateTime2, new Date(now))
          .query(`UPDATE ${T('UploadItems')}
            SET Status = 'interrupted',
                OutcomeCode = @outcomeCode,
                OutcomeMessage = @outcomeMessage,
                CompletedAt = @now,
                UpdatedAt = @now,
                ConcurrencyVersion = ConcurrencyVersion + 1
            WHERE SessionId = @sessionId
              AND Status NOT IN ('succeeded', 'skipped_duplicate', 'failed', 'interrupted')`)
        await refreshAggregates(transaction.request(), sessionId, now)
      }
      return getDetailWithRequest(transaction.request(), sessionId, ownerActorId)
    })
  },
}
