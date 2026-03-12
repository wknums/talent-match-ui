import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import { AUTH_USERS, JOBS, jobApplicationsKey, appResultKey, SYSTEM_STATS } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'
import type { Job, Application, AggregatedResult, SystemStats, RecruiterAnalytics, DepartmentAnalytics } from '../../src/types/index.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { requireRole } from '../middleware/rbac.js'

interface StoredUser {
  userId: string
  username: string
  role: 'admin' | 'recruiter' | 'business_panel'
  department?: string
  fullName: string
}

/** Compute per-recruiter analytics from existing KV data */
export async function computeRecruiterAnalytics(storage: StorageProvider): Promise<RecruiterAnalytics[]> {
  const users = await getArray<StoredUser>(storage, AUTH_USERS)
  const jobs = await getArray<Job>(storage, JOBS)

  // Build user lookup (only recruiters/admins with a department)
  const userMap = new Map<string, StoredUser>()
  for (const u of users) {
    if ((u.role === 'recruiter' || u.role === 'admin') && u.department) {
      userMap.set(u.userId, u)
    }
  }

  // Initialize metrics per recruiter
  const metrics = new Map<string, {
    applicationsInQueue: number
    manualReviewsPerformed: number
    shortlistRecommendations: number
    activeJobs: number
    totalProcessingTime: number
    completedCount: number
  }>()

  for (const [userId] of userMap) {
    metrics.set(userId, {
      applicationsInQueue: 0,
      manualReviewsPerformed: 0,
      shortlistRecommendations: 0,
      activeJobs: 0,
      totalProcessingTime: 0,
      completedCount: 0,
    })
  }

  for (const job of jobs) {
    const recruiterId = job.createdBy
    if (!recruiterId || !userMap.has(recruiterId)) continue

    const m = metrics.get(recruiterId)!

    // Count active jobs
    if (job.status === 'Active' || job.status === 'Processing') {
      m.activeJobs++
    }

    const apps = await getArray<Application>(storage, jobApplicationsKey(job.jobId))
    for (const app of apps) {
      if (app.status === 'Queued') {
        m.applicationsInQueue++
      }
      if (app.status === 'NeedsManualReview' || app.flagged === true) {
        m.manualReviewsPerformed++
      }
      if (app.finalDecision === 'Eligible') {
        m.shortlistRecommendations++
      }

      // Compute processing time from createdAt to aggregated result createdAt
      const result = await storage.get<AggregatedResult>(appResultKey(app.applicationId))
      if (result?.createdAt && app.createdAt) {
        const start = new Date(app.createdAt).getTime()
        const end = new Date(result.createdAt).getTime()
        if (!isNaN(start) && !isNaN(end) && end > start) {
          m.totalProcessingTime += (end - start) / (1000 * 60 * 60) // hours
          m.completedCount++
        }
      }
    }
  }

  const results: RecruiterAnalytics[] = []
  for (const [userId, user] of userMap) {
    const m = metrics.get(userId)!
    results.push({
      recruiterId: userId,
      recruiterName: user.fullName,
      department: user.department!,
      applicationsInQueue: m.applicationsInQueue,
      manualReviewsPerformed: m.manualReviewsPerformed,
      shortlistRecommendations: m.shortlistRecommendations,
      averageProcessingTime: m.completedCount > 0
        ? Math.round((m.totalProcessingTime / m.completedCount) * 100) / 100
        : undefined,
      activeJobs: m.activeJobs,
    })
  }

  return results
}

/** Group recruiter analytics into department-level aggregates */
export function computeDepartmentAnalytics(recruiterData: RecruiterAnalytics[]): DepartmentAnalytics[] {
  const deptMap = new Map<string, RecruiterAnalytics[]>()
  for (const r of recruiterData) {
    const existing = deptMap.get(r.department) ?? []
    existing.push(r)
    deptMap.set(r.department, existing)
  }

  const results: DepartmentAnalytics[] = []
  for (const [department, recruiters] of deptMap) {
    results.push({
      department,
      totalRecruiters: recruiters.length,
      applicationsInQueue: recruiters.reduce((s, r) => s + r.applicationsInQueue, 0),
      manualReviewsPerformed: recruiters.reduce((s, r) => s + r.manualReviewsPerformed, 0),
      shortlistRecommendations: recruiters.reduce((s, r) => s + r.shortlistRecommendations, 0),
      activeJobs: recruiters.reduce((s, r) => s + r.activeJobs, 0),
      recruiters,
    })
  }

  return results
}

export function createStatsRouter(storage: StorageProvider) {
  const router = Router()

  // GET /api/stats - system-wide stats
  router.get('/', async (_req, res, next) => {
    try {
      const jobs = await getArray<Job>(storage, JOBS)
      let totalApplications = 0
      let queued = 0
      let processing = 0
      let completed = 0
      let failed = 0

      for (const job of jobs) {
        const apps = await getArray<Application>(storage, jobApplicationsKey(job.jobId))
        totalApplications += apps.length
        queued += apps.filter(a => a.status === 'Queued').length
        processing += apps.filter(a => ['Extracting', 'Scoring', 'Aggregating'].includes(a.status)).length
        completed += apps.filter(a => a.status === 'Completed' || a.status === 'NeedsManualReview').length
        failed += apps.filter(a => a.status === 'ExtractionFailed' || a.status === 'ScoringFailed').length
      }

      const activeJobs = jobs.filter(j => j.status === 'Active' || j.status === 'Processing').length

      const stats: SystemStats = {
        totalJobs: jobs.length,
        activeJobs,
        totalApplications,
        queuedApplications: queued,
        processingApplications: processing,
        completedApplications: completed,
        failedApplications: failed,
        averageThroughputPerHour: completed > 0 ? Math.round(completed / Math.max(1, jobs.length)) : 0,
      }

      // Cache stats
      await storage.set(SYSTEM_STATS, stats)

      res.json(stats)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/stats/recruiters - per-recruiter analytics (admin/recruiter only)
  router.get('/recruiters', requireRole('admin', 'recruiter'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const allRecruiters = await computeRecruiterAnalytics(storage)

      // Scope by role: admin sees all, recruiter sees own department
      const scoped = req.user!.role === 'admin'
        ? allRecruiters
        : allRecruiters.filter(r => r.department === req.user!.department)

      res.json(scoped)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/stats/departments - per-department analytics (admin/recruiter only)
  router.get('/departments', requireRole('admin', 'recruiter'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const allRecruiters = await computeRecruiterAnalytics(storage)

      // Scope by role before grouping
      const scoped = req.user!.role === 'admin'
        ? allRecruiters
        : allRecruiters.filter(r => r.department === req.user!.department)

      const departments = computeDepartmentAnalytics(scoped)
      res.json(departments)
    } catch (err) {
      next(err)
    }
  })

  return router
}
