import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import { JOBS, jobApplicationsKey, SYSTEM_STATS } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'
import type { Job, Application, SystemStats } from '../../src/types/index.js'

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

  return router
}
