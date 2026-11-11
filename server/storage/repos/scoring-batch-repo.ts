// Repository for platform-mode scoring batches and per-job progress.
// See specs/008-platform-mode-shift/platform-contract.md §3.
//
// Cross-driver: uses the same `pool.request().input(...).query(...)` shape
// that the existing repos use; the SQLite shim in `db.ts` rewrites
// SYSUTCDATETIME() → datetime('now'). To stay portable we avoid SQL-Server-
// specific extensions (no UPDATE TOP, no OUTPUT inserted.*) and instead
// SELECT-then-conditional-UPDATE for row-leasing — safe across providers.

import { randomUUID } from 'node:crypto'
import { getPool, isAzureSql, sql } from '../db.js'
import { T } from '../table-names.js'

export type BatchStatus =
  | 'pending'
  | 'submitting'
  | 'submitted'
  | 'completed'
  | 'failed'
  | 'cancelling'
  | 'cancelled'

export interface ScoringBatch {
  batchId: string
  jobId: string
  promptVersionId: string
  applicationIds: string[]
  runCount: number
  status: BatchStatus
  submissionId: string | null
  pollUrl: string | null
  attempt: number
  submittedAt: string | null
  lastPolledAt: string | null
  nextPollAt: string
  lastError: string | null
  leaseOwner: string | null
  leasedUntil: string | null
  cancelRequested: boolean
}

export interface JobProgress {
  jobId: string
  totalApps: number
  batchesPending: number
  batchesSubmitted: number
  batchesCompleted: number
  batchesFailed: number
  appsCompleted: number
  appsFailed: number
  cancelRequested: boolean
  startedAt: string
  updatedAt: string
}

function parseAppIds(json: string | null | undefined): string[] {
  if (!json) return []
  try {
    const v = JSON.parse(json)
    return Array.isArray(v) ? v.map(String) : []
  } catch {
    return []
  }
}

function rowToBatch(r: any): ScoringBatch {
  return {
    batchId: String(r.BatchId),
    jobId: r.JobId,
    promptVersionId: r.PromptVersionId,
    applicationIds: parseAppIds(r.ApplicationIdsJson),
    runCount: Number(r.RunCount ?? 1),
    status: r.Status,
    submissionId: r.SubmissionId ?? null,
    pollUrl: r.PollUrl ?? null,
    attempt: Number(r.Attempt ?? 0),
    submittedAt: r.SubmittedAt?.toISOString?.() ?? r.SubmittedAt ?? null,
    lastPolledAt: r.LastPolledAt?.toISOString?.() ?? r.LastPolledAt ?? null,
    nextPollAt: r.NextPollAt?.toISOString?.() ?? r.NextPollAt,
    lastError: r.LastError ?? null,
    leaseOwner: r.LeaseOwner ?? null,
    leasedUntil: r.LeasedUntil?.toISOString?.() ?? r.LeasedUntil ?? null,
    cancelRequested: r.CancelRequested === true || Number(r.CancelRequested ?? 0) === 1,
  }
}

function rowToProgress(r: any): JobProgress {
  return {
    jobId: r.JobId,
    totalApps: Number(r.TotalApps ?? 0),
    batchesPending: Number(r.BatchesPending ?? 0),
    batchesSubmitted: Number(r.BatchesSubmitted ?? 0),
    batchesCompleted: Number(r.BatchesCompleted ?? 0),
    batchesFailed: Number(r.BatchesFailed ?? 0),
    appsCompleted: Number(r.AppsCompleted ?? 0),
    appsFailed: Number(r.AppsFailed ?? 0),
    cancelRequested: r.CancelRequested === true || Number(r.CancelRequested ?? 0) === 1,
    startedAt: r.StartedAt?.toISOString?.() ?? r.StartedAt,
    updatedAt: r.UpdatedAt?.toISOString?.() ?? r.UpdatedAt,
  }
}

export const scoringBatchRepo = {
  async createBatch(input: {
    batchId?: string
    jobId: string
    promptVersionId: string
    applicationIds: string[]
    runCount: number
  }): Promise<ScoringBatch> {
    const batchId = input.batchId ?? randomUUID()
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('jobId', sql.NVarChar, input.jobId)
      .input('pv', sql.NVarChar, input.promptVersionId)
      .input('apps', sql.NVarChar, JSON.stringify(input.applicationIds))
      .input('runs', sql.Int, input.runCount)
      .query(`INSERT INTO ${T('ScoringBatches')}
              (BatchId, JobId, PromptVersionId, ApplicationIdsJson, RunCount, Status, NextPollAt, CreatedAt, UpdatedAt)
              VALUES (@id, @jobId, @pv, @apps, @runs, 'pending', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME())`)
    const created = await this.getById(batchId)
    if (!created) throw new Error(`Failed to create batch ${batchId}`)
    return created
  },

  async getById(batchId: string): Promise<ScoringBatch | null> {
    const pool = await getPool()
    const res = await pool.request()
      .input('id', sql.NVarChar, batchId)
      .query(`SELECT * FROM ${T('ScoringBatches')} WHERE BatchId = @id`)
    return res.recordset[0] ? rowToBatch(res.recordset[0]) : null
  },

  async listByJob(jobId: string): Promise<ScoringBatch[]> {
    const pool = await getPool()
    const res = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('ScoringBatches')} WHERE JobId = @jobId ORDER BY CreatedAt ASC`)
    return res.recordset.map(rowToBatch)
  },

  /**
   * SELECT candidate batches due for work (status in pending/submitted/cancelling,
   * NextPollAt past, lease expired/empty). Returns up to `limit` rows. Does NOT
   * acquire the lease — call acquireLease(batchId) per row.
   */
  async findDueCandidates(limit: number): Promise<ScoringBatch[]> {
    const pool = await getPool()
    // SQL Server uses TOP, SQLite uses LIMIT — branch to keep cross-driver.
    const query = isAzureSql
      ? `SELECT TOP (@lim) * FROM ${T('ScoringBatches')}
         WHERE Status IN ('pending','submitted','cancelling')
           AND NextPollAt <= SYSUTCDATETIME()
           AND (LeaseOwner IS NULL OR LeasedUntil < SYSUTCDATETIME())
         ORDER BY NextPollAt ASC`
      : `SELECT * FROM ${T('ScoringBatches')}
         WHERE Status IN ('pending','submitted','cancelling')
           AND NextPollAt <= SYSUTCDATETIME()
           AND (LeaseOwner IS NULL OR LeasedUntil < SYSUTCDATETIME())
         ORDER BY NextPollAt ASC
         LIMIT @lim`
    const res = await pool.request().input('lim', sql.Int, limit).query(query)
    return res.recordset.map(rowToBatch)
  },

  /**
   * Atomically take the lease on `batchId` for `owner`. Returns true if we
   * won the race. Uses a conditional UPDATE so it's safe across SQLite and
   * Azure SQL.
   */
  async acquireLease(batchId: string, owner: string, leaseSeconds: number): Promise<boolean> {
    const pool = await getPool()
    // Compute the lease expiry in JS so SQLite can store ISO text directly.
    const until = new Date(Date.now() + leaseSeconds * 1000).toISOString()
    const res = await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('owner', sql.NVarChar, owner)
      .input('until', sql.NVarChar, until)
      .query(`UPDATE ${T('ScoringBatches')}
              SET LeaseOwner = @owner, LeasedUntil = @until, UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id
                AND (LeaseOwner IS NULL OR LeasedUntil < SYSUTCDATETIME())`)
    return (res.rowsAffected?.[0] ?? 0) > 0
  },

  async releaseLease(batchId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .query(`UPDATE ${T('ScoringBatches')}
              SET LeaseOwner = NULL, LeasedUntil = NULL, UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async markSubmitted(
    batchId: string,
    submissionId: string,
    pollUrl: string | null,
    nextPollAt: Date,
  ): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('sid', sql.NVarChar, submissionId)
      .input('poll', sql.NVarChar, pollUrl)
      .input('next', sql.NVarChar, nextPollAt.toISOString())
      .query(`UPDATE ${T('ScoringBatches')}
              SET Status = 'submitted', SubmissionId = @sid, PollUrl = @poll,
                  SubmittedAt = SYSUTCDATETIME(), NextPollAt = @next,
                  LastError = NULL, UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async setNextPollAt(batchId: string, nextPollAt: Date): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('next', sql.NVarChar, nextPollAt.toISOString())
      .query(`UPDATE ${T('ScoringBatches')}
              SET NextPollAt = @next, LastPolledAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async markCompleted(batchId: string, resultJson: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('res', sql.NVarChar, resultJson)
      .query(`UPDATE ${T('ScoringBatches')}
              SET Status = 'completed', ResultJson = @res, LastError = NULL,
                  UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async markFailed(batchId: string, error: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('err', sql.NVarChar, error.slice(0, 4000))
      .query(`UPDATE ${T('ScoringBatches')}
              SET Status = 'failed', LastError = @err, UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async markCancelled(batchId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .query(`UPDATE ${T('ScoringBatches')}
              SET Status = 'cancelled', UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async incrementAttempt(batchId: string, error: string, nextPollAt: Date): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('id', sql.NVarChar, batchId)
      .input('err', sql.NVarChar, error.slice(0, 4000))
      .input('next', sql.NVarChar, nextPollAt.toISOString())
      .query(`UPDATE ${T('ScoringBatches')}
              SET Attempt = Attempt + 1, LastError = @err, NextPollAt = @next,
                  UpdatedAt = SYSUTCDATETIME()
              WHERE BatchId = @id`)
  },

  async requestCancelByJob(jobId: string): Promise<number> {
    const pool = await getPool()
    const res = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`UPDATE ${T('ScoringBatches')}
              SET CancelRequested = 1, UpdatedAt = SYSUTCDATETIME()
              WHERE JobId = @jobId AND Status IN ('pending','submitting','submitted')`)
    return res.rowsAffected?.[0] ?? 0
  },

  // ----- progress -----

  async initProgress(jobId: string, totalApps: number, batchesPending: number): Promise<void> {
    const pool = await getPool()
    // UPSERT-ish: try insert; if it exists, reset counters.
    try {
      await pool.request()
        .input('jobId', sql.NVarChar, jobId)
        .input('total', sql.Int, totalApps)
        .input('pending', sql.Int, batchesPending)
        .query(`INSERT INTO ${T('ScoringJobProgress')}
                (JobId, TotalApps, BatchesPending, BatchesSubmitted, BatchesCompleted, BatchesFailed,
                 AppsCompleted, AppsFailed, CancelRequested, StartedAt, UpdatedAt)
                VALUES (@jobId, @total, @pending, 0, 0, 0, 0, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME())`)
    } catch {
      await pool.request()
        .input('jobId', sql.NVarChar, jobId)
        .input('total', sql.Int, totalApps)
        .input('pending', sql.Int, batchesPending)
        .query(`UPDATE ${T('ScoringJobProgress')}
                SET TotalApps = @total, BatchesPending = @pending, BatchesSubmitted = 0,
                    BatchesCompleted = 0, BatchesFailed = 0,
                    AppsCompleted = 0, AppsFailed = 0, CancelRequested = 0,
                    StartedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME()
                WHERE JobId = @jobId`)
    }
  },

  async getProgress(jobId: string): Promise<JobProgress | null> {
    const pool = await getPool()
    const res = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('ScoringJobProgress')} WHERE JobId = @jobId`)
    return res.recordset[0] ? rowToProgress(res.recordset[0]) : null
  },

  async requestCancelProgress(jobId: string): Promise<void> {
    const pool = await getPool()
    await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`UPDATE ${T('ScoringJobProgress')}
              SET CancelRequested = 1, UpdatedAt = SYSUTCDATETIME()
              WHERE JobId = @jobId`)
  },

  /**
   * Apply a batch transition to job progress counters.
   * `from`/`to` are the pre/post status of the batch. Pass null for `from`
   * on initial insert (already counted in initProgress).
   */
  async applyTransition(
    jobId: string,
    from: BatchStatus | null,
    to: BatchStatus | null,
    appDelta: { completed?: number; failed?: number } = {},
  ): Promise<void> {
    const pool = await getPool()
    const set: string[] = []
    if (from === 'pending') set.push('BatchesPending = BatchesPending - 1')
    if (from === 'submitted') set.push('BatchesSubmitted = BatchesSubmitted - 1')
    if (to === 'pending') set.push('BatchesPending = BatchesPending + 1')
    if (to === 'submitted') set.push('BatchesSubmitted = BatchesSubmitted + 1')
    if (to === 'completed') set.push('BatchesCompleted = BatchesCompleted + 1')
    if (to === 'failed') set.push('BatchesFailed = BatchesFailed + 1')
    if (appDelta.completed) set.push(`AppsCompleted = AppsCompleted + ${Number(appDelta.completed)}`)
    if (appDelta.failed) set.push(`AppsFailed = AppsFailed + ${Number(appDelta.failed)}`)
    if (!set.length) return
    set.push('UpdatedAt = SYSUTCDATETIME()')
    await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`UPDATE ${T('ScoringJobProgress')} SET ${set.join(', ')} WHERE JobId = @jobId`)
  },
}
