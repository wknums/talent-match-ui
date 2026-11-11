import { getPool, sql } from '../db.js'
import type { ProcessingEvent, DLQItem, SystemStats } from '../../../src/types/index.js'
import { T } from '../table-names.js'

function parseJson<T>(val: string | null | undefined, fallback: T): T {
  if (!val) return fallback
  try { return JSON.parse(val) } catch { return fallback }
}

function rowToEvent(r: any): ProcessingEvent {
  return {
    eventId: r.Id,
    timestamp: r.Timestamp?.toISOString?.() ?? r.Timestamp,
    actor: r.Actor,
    action: r.Action ?? r.EventType,
    entityType: r.EntityType,
    entityId: r.EntityId,
    correlationId: r.CorrelationId,
    details: parseJson(r.DetailsJson ?? r.PayloadJson, {}),
  }
}

function rowToDLQ(r: any): DLQItem {
  return {
    itemId: r.Id,
    applicationId: r.ApplicationId ?? (r.EntityType === 'Application' ? r.EntityId : ''),
    jobId: r.JobId ?? '',
    failureType: (r.FailureType ?? 'Scoring') as DLQItem['failureType'],
    failureReason: r.FailureReason,
    attemptCount: r.AttemptCount ?? r.RetryCount ?? 0,
    firstFailedAt: r.FirstFailedAt?.toISOString?.() ?? r.FirstFailedAt ?? r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    lastAttemptedAt: r.LastAttemptedAt?.toISOString?.() ?? r.LastAttemptedAt ?? r.UpdatedAt?.toISOString?.() ?? r.UpdatedAt,
    canRetry: r.CanRetry === undefined ? true : r.CanRetry === true || r.CanRetry === 1,
    notes: r.Notes ?? undefined,
  }
}

export const auditRepo = {
  async appendEvent(event: ProcessingEvent): Promise<void> {
    const pool = await getPool()
    const request = pool.request()
      .input('id', sql.NVarChar, event.eventId)
      .input('actor', sql.NVarChar, event.actor)
      .input('action', sql.NVarChar, event.action)
      .input('entityType', sql.NVarChar, event.entityType)
      .input('entityId', sql.NVarChar, event.entityId)
      .input('detailsJson', sql.NVarChar, JSON.stringify(event.details))
      .input('timestamp', sql.DateTime2, new Date(event.timestamp))
      .input('correlationId', sql.NVarChar, event.correlationId)

    await request.query(`INSERT INTO ${T('ProcessingEvents')} (Id, Actor, Action, EntityType, EntityId, DetailsJson, Timestamp, CorrelationId)
      VALUES (@id, @actor, @action, @entityType, @entityId, @detailsJson, @timestamp, @correlationId)`)
  },

  async query(filters: { entityType?: string; eventType?: string; startDate?: string; endDate?: string; page?: number; pageSize?: number }): Promise<{ events: ProcessingEvent[]; total: number }> {
    const pool = await getPool()
    const conditions: string[] = []
    const req = pool.request()
    if (filters.entityType) { conditions.push('EntityType = @entityType'); req.input('entityType', sql.NVarChar, filters.entityType) }
    if (filters.eventType) { conditions.push('Action = @action'); req.input('action', sql.NVarChar, filters.eventType) }
    if (filters.startDate) { conditions.push('Timestamp >= @startDate'); req.input('startDate', sql.DateTime2, new Date(filters.startDate)) }
    if (filters.endDate) { conditions.push('Timestamp <= @endDate'); req.input('endDate', sql.DateTime2, new Date(filters.endDate)) }

    const where = conditions.length > 0 ? 'WHERE ' + conditions.join(' AND ') : ''
    const p = filters.page ?? 1
    const ps = filters.pageSize ?? 50

    const countResult = await req.query(`SELECT COUNT(*) AS total FROM ${T('ProcessingEvents')} ${where}`)
    const total = countResult.recordset[0].total

    // Need a fresh request for the data query since inputs are consumed
    const dataReq = pool.request()
    if (filters.entityType) dataReq.input('entityType', sql.NVarChar, filters.entityType)
    if (filters.eventType) dataReq.input('action', sql.NVarChar, filters.eventType)
    if (filters.startDate) dataReq.input('startDate', sql.DateTime2, new Date(filters.startDate))
    if (filters.endDate) dataReq.input('endDate', sql.DateTime2, new Date(filters.endDate))
    dataReq.input('offset', sql.Int, (p - 1) * ps)
    dataReq.input('pageSize', sql.Int, ps)

    const dataResult = await dataReq.query(`
      SELECT * FROM ${T('ProcessingEvents')} ${where}
      ORDER BY Timestamp DESC
      OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
    `)
    return { events: dataResult.recordset.map(rowToEvent), total }
  },
}

export const dlqRepo = {
  async getAll(): Promise<DLQItem[]> {
    const pool = await getPool()
    const result = await pool.request().query(`SELECT * FROM ${T('FailureQueueItems')} ORDER BY LastAttemptedAt DESC`)
    return result.recordset.map(rowToDLQ)
  },

  async getById(itemId: string): Promise<DLQItem | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, itemId)
      .query(`SELECT * FROM ${T('FailureQueueItems')} WHERE Id = @id`)
    return result.recordset[0] ? rowToDLQ(result.recordset[0]) : undefined
  },

  async add(item: DLQItem): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, item.itemId)
      .input('applicationId', sql.NVarChar, item.applicationId)
      .input('jobId', sql.NVarChar, item.jobId)
      .input('failureType', sql.NVarChar, item.failureType)
      .input('failureReason', sql.NVarChar, item.failureReason)
      .input('attemptCount', sql.Int, item.attemptCount)
      .input('firstFailedAt', sql.DateTime2, new Date(item.firstFailedAt))
      .input('lastAttemptedAt', sql.DateTime2, new Date(item.lastAttemptedAt))
      .input('canRetry', sql.Bit, item.canRetry ? 1 : 0)
      .input('notes', sql.NVarChar, item.notes ?? null)
            .query(`INSERT INTO ${T('FailureQueueItems')} (
              Id, EntityType, EntityId, FailureReason, RetryCount, CreatedAt, UpdatedAt,
              ApplicationId, JobId, FailureType, AttemptCount, FirstFailedAt, LastAttemptedAt, CanRetry, Notes)
              VALUES (
              @id, 'Application', @applicationId, @failureReason, @attemptCount, @firstFailedAt, @lastAttemptedAt,
              @applicationId, @jobId, @failureType, @attemptCount, @firstFailedAt, @lastAttemptedAt, @canRetry, @notes)`)
  },

  async remove(itemId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, itemId)
      .query(`DELETE FROM ${T('FailureQueueItems')} WHERE Id = @id`)
  },

  async removeByJobAndAppIds(jobId: string, applicationIds: string[]): Promise<void> {
    if (applicationIds.length === 0) return
    const pool = await getPool()
    // Use a temp table approach for the IN clause to avoid SQL injection
    const req = pool.request().input('jobId', sql.NVarChar, jobId)
    const placeholders = applicationIds.map((id, i) => { req.input(`a${i}`, sql.NVarChar, id); return `@a${i}` })
    await req.query(`DELETE FROM ${T('FailureQueueItems')} WHERE JobId = @jobId AND ApplicationId IN (${placeholders.join(',')})`)
  },
}

export const statsRepo = {
  async getSystemStats(): Promise<SystemStats> {
    const pool = await getPool()
    const result = await pool.request().query(`
      SELECT
        (SELECT COUNT(*) FROM ${T('Jobs')}) AS totalJobs,
        (SELECT COUNT(*) FROM ${T('Jobs')} WHERE Status IN ('Active','Processing')) AS activeJobs,
        (SELECT COUNT(*) FROM ${T('Applications')} WHERE TestRunId IS NULL) AS totalApplications,
        (SELECT COUNT(*) FROM ${T('Applications')} WHERE TestRunId IS NULL AND Status = 'Queued') AS queuedApplications,
        (SELECT COUNT(*) FROM ${T('Applications')} WHERE TestRunId IS NULL AND Status IN ('Extracting','Scoring','Aggregating')) AS processingApplications,
        (SELECT COUNT(*) FROM ${T('Applications')} WHERE TestRunId IS NULL AND Status IN ('Completed','NeedsManualReview')) AS completedApplications,
        (SELECT COUNT(*) FROM ${T('Applications')} WHERE TestRunId IS NULL AND Status IN ('ExtractionFailed','ScoringFailed')) AS failedApplications
    `)
    const r = result.recordset[0]
    return {
      totalJobs: r.totalJobs,
      activeJobs: r.activeJobs,
      totalApplications: r.totalApplications,
      queuedApplications: r.queuedApplications,
      processingApplications: r.processingApplications,
      completedApplications: r.completedApplications,
      failedApplications: r.failedApplications,
      averageThroughputPerHour: r.completedApplications > 0 ? Math.round(r.completedApplications / Math.max(1, r.totalJobs)) : 0,
    }
  },
}
