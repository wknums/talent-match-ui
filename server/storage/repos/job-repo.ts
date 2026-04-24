import { getPool, isAzureSql, sql } from '../db.js'
import { T } from '../table-names.js'
import type { Job, JobConfigVersion, JobStats } from '../../../src/types/index.js'

function parseJson<T>(val: string | null | undefined, fallback: T): T {
  if (!val) return fallback
  try { return JSON.parse(val) } catch { return fallback }
}

function normalizeRubricCategories(raw: unknown): JobConfigVersion['rubric'] {
  if (!Array.isArray(raw)) return []
  return raw.map((item: any, index) => ({
    id: String(item.id ?? item.Id ?? `rubric-${index + 1}`),
    name: item.name ?? item.Name ?? '',
    description: item.description ?? item.Description ?? '',
    weight: Number(item.weight ?? item.Weight ?? 0),
  }))
}

function normalizeMustHaves(raw: unknown): JobConfigVersion['mustHaves'] {
  if (!Array.isArray(raw)) return []
  return raw.map((item: any, index) => {
    if (typeof item === 'string') {
      return {
        id: `must-have-${index + 1}`,
        criterion: item,
        description: '',
      }
    }

    return {
      id: String(item.id ?? item.Id ?? `must-have-${index + 1}`),
      criterion: item.criterion ?? item.Criterion ?? item.name ?? item.Name ?? '',
      description: item.description ?? item.Description ?? '',
    }
  })
}

function normalizeDesiredCriteria(raw: unknown): JobConfigVersion['desiredCriteria'] {
  if (!Array.isArray(raw)) return []
  return raw.map((item: any, index) => {
    if (typeof item === 'string') {
      return {
        id: `desired-${index + 1}`,
        qualification: item,
        description: '',
      }
    }

    return {
      id: String(item.id ?? item.Id ?? `desired-${index + 1}`),
      qualification: item.qualification ?? item.Qualification ?? item.name ?? item.Name ?? '',
      description: item.description ?? item.Description ?? '',
    }
  })
}

function rowToConfigVersion(r: any): JobConfigVersion {
  const rubric = normalizeRubricCategories(parseJson(r.RubricJson, []))
  const parsedMustHaves = parseJson(r.MustHavesJson, [])
  const mustHaves = normalizeMustHaves(
    Array.isArray(parsedMustHaves) && parsedMustHaves.length > 0
      ? parsedMustHaves
      : parseJson(r.MustHaveCriteriaJson, []),
  )
  const desiredCriteria = normalizeDesiredCriteria(parseJson(r.DesiredCriteriaJson, []))

  return {
    versionId: r.Id,
    jobId: r.JobId,
    rubric,
    mustHaves,
    desiredCriteria,
    runsPerApplication: r.RunsPerApplication ?? r.ScoringRunCount ?? 3,
    aggregationStrategy: r.AggregationStrategy,
    longlistThreshold: r.LonglistThreshold,
    shortlistThreshold: r.ShortlistThreshold,
    varianceThreshold: r.VarianceThreshold,
    rubricApprovalStatus: r.RubricApprovalStatus,
    rubricSource: r.RubricSource,
    rawExtractionResponse: r.RawExtractionResponse ?? undefined,
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
  }
}

const legacyMustHaveCriteriaSelectSql = isAzureSql ? '' : ', cv.MustHaveCriteriaJson'

async function getNextVersionNumber(pool: any, jobId: string): Promise<number> {
  const result = await pool.request()
    .input('jobId', sql.NVarChar, jobId)
    .query(`SELECT ISNULL(MAX(VersionNumber), 0) AS maxVersion FROM ${T('JobConfigVersions')} WHERE JobId = @jobId`)
  return (result.recordset[0]?.maxVersion ?? 0) + 1
}

function rowToJob(r: any, configVersion?: JobConfigVersion): Job {
  return {
    jobId: r.Id,
    jobCode: r.JobCode,
    title: r.Title,
    department: r.Department,
    organization: r.Organisation,
    postingDate: r.PostingDate?.toISOString?.() ?? r.PostingDate,
    createdBy: r.CreatedBy ?? '',
    createdAt: r.CreatedAt?.toISOString?.() ?? r.CreatedAt,
    status: r.Status,
    currentVersion: configVersion!,
    jobDescription: r.JobDescription ?? undefined,
    specDocumentId: r.SpecDocumentId ?? undefined,
    rubricDocumentId: r.RubricDocumentId ?? undefined,
  }
}

export const jobRepo = {
  async getAll(): Promise<Job[]> {
    const pool = await getPool()
    const result = await pool.request().query(`
      SELECT j.*, cv.Id AS CvId, cv.JobId AS CvJobId, cv.VersionNumber, cv.RubricJson, cv.MustHavesJson${legacyMustHaveCriteriaSelectSql},
             cv.DesiredCriteriaJson, cv.RunsPerApplication, cv.AggregationStrategy, cv.LonglistThreshold,
             cv.ShortlistThreshold, cv.VarianceThreshold, cv.RubricApprovalStatus, cv.RubricSource,
             cv.RawExtractionResponse, cv.CreatedAt AS CvCreatedAt
      FROM ${T('Jobs')} j
      LEFT JOIN ${T('JobConfigVersions')} cv ON cv.Id = j.CurrentConfigVersionId
      ORDER BY j.CreatedAt DESC
    `)
    return result.recordset.map((r: any) => {
      const cv = r.CvId ? rowToConfigVersion({ ...r, Id: r.CvId, JobId: r.CvJobId, CreatedAt: r.CvCreatedAt }) : createEmptyConfig(r.Id)
      return rowToJob(r, cv)
    })
  },

  async getByDepartment(department: string): Promise<Job[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('dept', sql.NVarChar, department)
      .query(`
        SELECT j.*, cv.Id AS CvId, cv.JobId AS CvJobId, cv.VersionNumber, cv.RubricJson, cv.MustHavesJson${legacyMustHaveCriteriaSelectSql},
               cv.DesiredCriteriaJson, cv.RunsPerApplication, cv.AggregationStrategy, cv.LonglistThreshold,
               cv.ShortlistThreshold, cv.VarianceThreshold, cv.RubricApprovalStatus, cv.RubricSource,
               cv.RawExtractionResponse, cv.CreatedAt AS CvCreatedAt
        FROM ${T('Jobs')} j
        LEFT JOIN ${T('JobConfigVersions')} cv ON cv.Id = j.CurrentConfigVersionId
        WHERE j.Department = @dept
        ORDER BY j.CreatedAt DESC
      `)
    return result.recordset.map((r: any) => {
      const cv = r.CvId ? rowToConfigVersion({ ...r, Id: r.CvId, JobId: r.CvJobId, CreatedAt: r.CvCreatedAt }) : createEmptyConfig(r.Id)
      return rowToJob(r, cv)
    })
  },

  async getById(jobId: string): Promise<Job | undefined> {
    const pool = await getPool()
    const result = await pool.request()
      .input('id', sql.NVarChar, jobId)
      .query(`
        SELECT j.*, cv.Id AS CvId, cv.JobId AS CvJobId, cv.VersionNumber, cv.RubricJson, cv.MustHavesJson${legacyMustHaveCriteriaSelectSql},
               cv.DesiredCriteriaJson, cv.RunsPerApplication, cv.AggregationStrategy, cv.LonglistThreshold,
               cv.ShortlistThreshold, cv.VarianceThreshold, cv.RubricApprovalStatus, cv.RubricSource,
               cv.RawExtractionResponse, cv.CreatedAt AS CvCreatedAt
        FROM ${T('Jobs')} j
        LEFT JOIN ${T('JobConfigVersions')} cv ON cv.Id = j.CurrentConfigVersionId
        WHERE j.Id = @id
      `)
    if (!result.recordset[0]) return undefined
    const r = result.recordset[0]
    const cv = r.CvId ? rowToConfigVersion({ ...r, Id: r.CvId, JobId: r.CvJobId, CreatedAt: r.CvCreatedAt }) : createEmptyConfig(r.Id)
    return rowToJob(r, cv)
  },

  async create(job: Job): Promise<void> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()
    try {
      // Insert config version first
      const cv = job.currentVersion
      const versionNumber = 1
      await txn.request()
        .input('id', sql.NVarChar, cv.versionId)
        .input('jobId', sql.NVarChar, job.jobId)
        .input('versionNumber', sql.Int, versionNumber)
        .input('rubricJson', sql.NVarChar, JSON.stringify(cv.rubric))
        .input('mustHavesJson', sql.NVarChar, JSON.stringify(cv.mustHaves))
        .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(cv.desiredCriteria))
        .input('runsPerApplication', sql.Int, cv.runsPerApplication)
        .input('aggregationStrategy', sql.NVarChar, cv.aggregationStrategy)
        .input('longlistThreshold', sql.Float, cv.longlistThreshold)
        .input('shortlistThreshold', sql.Float, cv.shortlistThreshold)
        .input('varianceThreshold', sql.Float, cv.varianceThreshold)
        .input('rubricApprovalStatus', sql.NVarChar, cv.rubricApprovalStatus)
        .input('rubricSource', sql.NVarChar, cv.rubricSource)
        .input('rawExtractionResponse', sql.NVarChar, cv.rawExtractionResponse ?? null)
        .input('createdAt', sql.DateTime2, new Date(cv.createdAt))
      if (isAzureSql) {
        await txn.request()
          .input('id', sql.NVarChar, cv.versionId)
          .input('jobId', sql.NVarChar, job.jobId)
          .input('versionNumber', sql.Int, versionNumber)
          .input('rubricJson', sql.NVarChar, JSON.stringify(cv.rubric))
          .input('mustHavesJson', sql.NVarChar, JSON.stringify(cv.mustHaves))
          .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(cv.desiredCriteria))
          .input('runsPerApplication', sql.Int, cv.runsPerApplication)
          .input('aggregationStrategy', sql.NVarChar, cv.aggregationStrategy)
          .input('longlistThreshold', sql.Float, cv.longlistThreshold)
          .input('shortlistThreshold', sql.Float, cv.shortlistThreshold)
          .input('varianceThreshold', sql.Float, cv.varianceThreshold)
          .input('rubricApprovalStatus', sql.NVarChar, cv.rubricApprovalStatus)
          .input('rubricSource', sql.NVarChar, cv.rubricSource)
          .input('rawExtractionResponse', sql.NVarChar, cv.rawExtractionResponse ?? null)
          .input('createdAt', sql.DateTime2, new Date(cv.createdAt))
          .query(`INSERT INTO ${T('JobConfigVersions')} (
            Id, JobId, VersionNumber, RubricJson, MustHavesJson, DesiredCriteriaJson,
            RunsPerApplication, AggregationStrategy, LonglistThreshold, ShortlistThreshold,
            VarianceThreshold, RubricApprovalStatus, RubricSource, RawExtractionResponse, CreatedAt)
            VALUES (
            @id, @jobId, @versionNumber, @rubricJson, @mustHavesJson, @desiredCriteriaJson,
            @runsPerApplication, @aggregationStrategy, @longlistThreshold, @shortlistThreshold,
            @varianceThreshold, @rubricApprovalStatus, @rubricSource, @rawExtractionResponse, @createdAt)`)
      } else {
        await txn.request()
          .input('id', sql.NVarChar, cv.versionId)
          .input('jobId', sql.NVarChar, job.jobId)
          .input('versionNumber', sql.Int, versionNumber)
          .input('rubricJson', sql.NVarChar, JSON.stringify(cv.rubric))
          .input('mustHavesJson', sql.NVarChar, JSON.stringify(cv.mustHaves))
          .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(cv.desiredCriteria))
          .input('runsPerApplication', sql.Int, cv.runsPerApplication)
          .input('aggregationStrategy', sql.NVarChar, cv.aggregationStrategy)
          .input('longlistThreshold', sql.Float, cv.longlistThreshold)
          .input('shortlistThreshold', sql.Float, cv.shortlistThreshold)
          .input('varianceThreshold', sql.Float, cv.varianceThreshold)
          .input('rubricApprovalStatus', sql.NVarChar, cv.rubricApprovalStatus)
          .input('rubricSource', sql.NVarChar, cv.rubricSource)
          .input('rawExtractionResponse', sql.NVarChar, cv.rawExtractionResponse ?? null)
          .input('createdAt', sql.DateTime2, new Date(cv.createdAt))
          .query(`INSERT INTO ${T('JobConfigVersions')} (
            Id, JobId, VersionNumber, RubricJson, MustHaveCriteriaJson, MustHavesJson, DesiredCriteriaJson,
            ScoringRunCount, RunsPerApplication, AggregationStrategy, LonglistThreshold, ShortlistThreshold,
            VarianceThreshold, RubricApprovalStatus, RubricSource, RawExtractionResponse, CreatedAt)
            VALUES (
            @id, @jobId, @versionNumber, @rubricJson, @mustHavesJson, @mustHavesJson, @desiredCriteriaJson,
            @runsPerApplication, @runsPerApplication, @aggregationStrategy, @longlistThreshold, @shortlistThreshold,
            @varianceThreshold, @rubricApprovalStatus, @rubricSource, @rawExtractionResponse, @createdAt)`)
      }

      // Insert job
      await txn.request()
        .input('id', sql.NVarChar, job.jobId)
        .input('jobCode', sql.NVarChar, job.jobCode)
        .input('title', sql.NVarChar, job.title)
        .input('department', sql.NVarChar, job.department)
        .input('organisation', sql.NVarChar, job.organization)
        .input('postingDate', sql.DateTime2, new Date(job.postingDate))
        .input('status', sql.NVarChar, job.status)
        .input('jobDescription', sql.NVarChar, job.jobDescription ?? null)
        .input('currentConfigVersionId', sql.NVarChar, cv.versionId)
        .input('createdBy', sql.NVarChar, job.createdBy)
        .input('createdAt', sql.DateTime2, new Date(job.createdAt))
        .input('specDocumentId', sql.NVarChar, job.specDocumentId ?? null)
        .input('rubricDocumentId', sql.NVarChar, job.rubricDocumentId ?? null)
        .query(`INSERT INTO ${T('Jobs')} (Id, JobCode, Title, Department, Organisation, PostingDate, Status,
                JobDescription, CurrentConfigVersionId, CreatedBy, CreatedAt, SpecDocumentId, RubricDocumentId)
                VALUES (@id, @jobCode, @title, @department, @organisation, @postingDate, @status,
                @jobDescription, @currentConfigVersionId, @createdBy, @createdAt, @specDocumentId, @rubricDocumentId)`)

      await txn.commit()
    } catch (err) {
      await txn.rollback()
      throw err
    }
  },

  async addConfigVersion(version: JobConfigVersion): Promise<void> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()
    try {
      const versionNumber = await getNextVersionNumber(pool, version.jobId)
      await txn.request()
        .input('id', sql.NVarChar, version.versionId)
        .input('jobId', sql.NVarChar, version.jobId)
        .input('versionNumber', sql.Int, versionNumber)
        .input('rubricJson', sql.NVarChar, JSON.stringify(version.rubric))
        .input('mustHavesJson', sql.NVarChar, JSON.stringify(version.mustHaves))
        .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(version.desiredCriteria))
        .input('runsPerApplication', sql.Int, version.runsPerApplication)
        .input('aggregationStrategy', sql.NVarChar, version.aggregationStrategy)
        .input('longlistThreshold', sql.Float, version.longlistThreshold)
        .input('shortlistThreshold', sql.Float, version.shortlistThreshold)
        .input('varianceThreshold', sql.Float, version.varianceThreshold)
        .input('rubricApprovalStatus', sql.NVarChar, version.rubricApprovalStatus)
        .input('rubricSource', sql.NVarChar, version.rubricSource)
        .input('rawExtractionResponse', sql.NVarChar, version.rawExtractionResponse ?? null)
        .input('createdAt', sql.DateTime2, new Date(version.createdAt))
      if (isAzureSql) {
        await txn.request()
          .input('id', sql.NVarChar, version.versionId)
          .input('jobId', sql.NVarChar, version.jobId)
          .input('versionNumber', sql.Int, versionNumber)
          .input('rubricJson', sql.NVarChar, JSON.stringify(version.rubric))
          .input('mustHavesJson', sql.NVarChar, JSON.stringify(version.mustHaves))
          .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(version.desiredCriteria))
          .input('runsPerApplication', sql.Int, version.runsPerApplication)
          .input('aggregationStrategy', sql.NVarChar, version.aggregationStrategy)
          .input('longlistThreshold', sql.Float, version.longlistThreshold)
          .input('shortlistThreshold', sql.Float, version.shortlistThreshold)
          .input('varianceThreshold', sql.Float, version.varianceThreshold)
          .input('rubricApprovalStatus', sql.NVarChar, version.rubricApprovalStatus)
          .input('rubricSource', sql.NVarChar, version.rubricSource)
          .input('rawExtractionResponse', sql.NVarChar, version.rawExtractionResponse ?? null)
          .input('createdAt', sql.DateTime2, new Date(version.createdAt))
          .query(`INSERT INTO ${T('JobConfigVersions')} (
            Id, JobId, VersionNumber, RubricJson, MustHavesJson, DesiredCriteriaJson,
            RunsPerApplication, AggregationStrategy, LonglistThreshold, ShortlistThreshold,
            VarianceThreshold, RubricApprovalStatus, RubricSource, RawExtractionResponse, CreatedAt)
            VALUES (
            @id, @jobId, @versionNumber, @rubricJson, @mustHavesJson, @desiredCriteriaJson,
            @runsPerApplication, @aggregationStrategy, @longlistThreshold, @shortlistThreshold,
            @varianceThreshold, @rubricApprovalStatus, @rubricSource, @rawExtractionResponse, @createdAt)`)
      } else {
        await txn.request()
          .input('id', sql.NVarChar, version.versionId)
          .input('jobId', sql.NVarChar, version.jobId)
          .input('versionNumber', sql.Int, versionNumber)
          .input('rubricJson', sql.NVarChar, JSON.stringify(version.rubric))
          .input('mustHavesJson', sql.NVarChar, JSON.stringify(version.mustHaves))
          .input('desiredCriteriaJson', sql.NVarChar, JSON.stringify(version.desiredCriteria))
          .input('runsPerApplication', sql.Int, version.runsPerApplication)
          .input('aggregationStrategy', sql.NVarChar, version.aggregationStrategy)
          .input('longlistThreshold', sql.Float, version.longlistThreshold)
          .input('shortlistThreshold', sql.Float, version.shortlistThreshold)
          .input('varianceThreshold', sql.Float, version.varianceThreshold)
          .input('rubricApprovalStatus', sql.NVarChar, version.rubricApprovalStatus)
          .input('rubricSource', sql.NVarChar, version.rubricSource)
          .input('rawExtractionResponse', sql.NVarChar, version.rawExtractionResponse ?? null)
          .input('createdAt', sql.DateTime2, new Date(version.createdAt))
          .query(`INSERT INTO ${T('JobConfigVersions')} (
            Id, JobId, VersionNumber, RubricJson, MustHaveCriteriaJson, MustHavesJson, DesiredCriteriaJson,
            ScoringRunCount, RunsPerApplication, AggregationStrategy, LonglistThreshold, ShortlistThreshold,
            VarianceThreshold, RubricApprovalStatus, RubricSource, RawExtractionResponse, CreatedAt)
            VALUES (
            @id, @jobId, @versionNumber, @rubricJson, @mustHavesJson, @mustHavesJson, @desiredCriteriaJson,
            @runsPerApplication, @runsPerApplication, @aggregationStrategy, @longlistThreshold, @shortlistThreshold,
            @varianceThreshold, @rubricApprovalStatus, @rubricSource, @rawExtractionResponse, @createdAt)`)
      }

      await txn.request()
        .input('jobId', sql.NVarChar, version.jobId)
        .input('versionId', sql.NVarChar, version.versionId)
        .query(`UPDATE ${T('Jobs')} SET CurrentConfigVersionId = @versionId, UpdatedAt = SYSUTCDATETIME() WHERE Id = @jobId`)

      await txn.commit()
    } catch (err) {
      await txn.rollback()
      throw err
    }
  },

  async getConfigVersions(jobId: string): Promise<JobConfigVersion[]> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .query(`SELECT * FROM ${T('JobConfigVersions')} WHERE JobId = @jobId ORDER BY CreatedAt`)
    return result.recordset.map(rowToConfigVersion)
  },

  async updateConfigVersionField(versionId: string, field: string, value: string): Promise<void> {
    const pool = await getPool()
    // Only allow specific safe fields
    const allowed = ['RubricApprovalStatus']
    if (!allowed.includes(field)) throw new Error(`Cannot update field: ${field}`)
    await pool.request()
      .input('id', sql.NVarChar, versionId)
      .input('val', sql.NVarChar, value)
      .query(`UPDATE ${T('JobConfigVersions')} SET ${field} = @val WHERE Id = @id`)
  },

  async delete(jobId: string): Promise<boolean> {
    const pool = await getPool()
    const txn = pool.transaction()
    await txn.begin()

    try {
      await txn.request()
        .input('jobId', sql.NVarChar, jobId)
        .query(`DELETE FROM ${T('FailureQueueItems')} WHERE JobId = @jobId OR EntityId = @jobId`)

      await txn.request()
        .input('jobId', sql.NVarChar, jobId)
        .query(`DELETE FROM ${T('PromptTestRuns')} WHERE JobId = @jobId`)

      const result = await txn.request()
        .input('jobId', sql.NVarChar, jobId)
        .query(`DELETE FROM ${T('Jobs')} WHERE Id = @jobId`)

      await txn.commit()
      return (result.rowsAffected?.[0] ?? 0) > 0
    } catch (err) {
      await txn.rollback()
      throw err
    }
  },

  /** Compute stats for a job directly via SQL instead of loading all apps in memory */
  async getJobStats(jobId: string, config: JobConfigVersion): Promise<JobStats> {
    const pool = await getPool()
    const result = await pool.request()
      .input('jobId', sql.NVarChar, jobId)
      .input('longlistThreshold', sql.Float, config.longlistThreshold)
      .input('shortlistThreshold', sql.Float, config.shortlistThreshold)
      .query(`
        SELECT
          COUNT(*)                                                           AS totalApplications,
          SUM(CASE WHEN Status = 'Queued' THEN 1 ELSE 0 END)                AS queued,
          SUM(CASE WHEN Status = 'Extracting' THEN 1 ELSE 0 END)            AS extracting,
          SUM(CASE WHEN Status = 'Scoring' THEN 1 ELSE 0 END)               AS scoring,
          SUM(CASE WHEN Status = 'Completed' THEN 1 ELSE 0 END)             AS completed,
          SUM(CASE WHEN Status IN ('ExtractionFailed','ScoringFailed') THEN 1 ELSE 0 END) AS failed,
          SUM(CASE WHEN Status = 'NeedsManualReview' THEN 1 ELSE 0 END)     AS needsManualReview,
          SUM(CASE WHEN FinalDecision = 'Eligible' AND FinalScore >= @longlistThreshold THEN 1 ELSE 0 END)  AS longlistCount,
          SUM(CASE WHEN FinalDecision = 'Eligible' AND FinalScore >= @shortlistThreshold THEN 1 ELSE 0 END) AS shortlistCount,
          SUM(CASE WHEN FinalDecision = 'Excluded' THEN 1 ELSE 0 END)       AS excludedCount
        FROM ${T('Applications')}
        WHERE JobId = @jobId AND TestRunId IS NULL
      `)
    const r = result.recordset[0]
    return {
      totalApplications: r.totalApplications ?? 0,
      queued: r.queued ?? 0,
      extracting: r.extracting ?? 0,
      scoring: r.scoring ?? 0,
      completed: r.completed ?? 0,
      failed: r.failed ?? 0,
      needsManualReview: r.needsManualReview ?? 0,
      longlistCount: r.longlistCount ?? 0,
      shortlistCount: r.shortlistCount ?? 0,
      excludedCount: r.excludedCount ?? 0,
    }
  },
}

function createEmptyConfig(jobId: string): JobConfigVersion {
  return {
    versionId: '', jobId, rubric: [], mustHaves: [], desiredCriteria: [],
    runsPerApplication: 3, aggregationStrategy: 'median',
    longlistThreshold: 70, shortlistThreshold: 85, varianceThreshold: 15,
    rubricApprovalStatus: 'draft', rubricSource: 'manual', createdAt: new Date().toISOString(),
  }
}
