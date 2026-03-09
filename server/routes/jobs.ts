import { randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { JOBS, jobVersionsKey, jobApplicationsKey } from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'
import type { Job, JobConfigVersion, Application } from '../../src/types/index.js'

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

export function createJobsRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // GET /api/jobs - list jobs (department-filtered for recruiters)
  router.get('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      const jobs = await getArray<Job>(storage, JOBS)
      let filtered = jobs

      if (req.user?.role === 'recruiter' && req.user.department) {
        filtered = jobs.filter(j => j.department === req.user!.department)
      }

      // Compute stats for each job
      const jobsWithStats = await Promise.all(
        filtered.map(async (job) => {
          const apps = await getArray<Application>(storage, jobApplicationsKey(job.jobId))
          const config = job.currentVersion
          const stats = {
            totalApplications: apps.length,
            queued: apps.filter(a => a.status === 'Queued').length,
            extracting: apps.filter(a => a.status === 'Extracting').length,
            scoring: apps.filter(a => a.status === 'Scoring').length,
            completed: apps.filter(a => a.status === 'Completed').length,
            failed: apps.filter(a => a.status === 'ExtractionFailed' || a.status === 'ScoringFailed').length,
            needsManualReview: apps.filter(a => a.status === 'NeedsManualReview').length,
            longlistCount: apps.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.longlistThreshold).length,
            shortlistCount: apps.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.shortlistThreshold).length,
            excludedCount: apps.filter(a => a.finalDecision === 'Excluded').length,
          }
          return { ...job, stats }
        })
      )

      res.json(jobsWithStats)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs - create job
  router.post('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { title, department, organization, postingDate, rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, specDocumentId, rubricDocumentId, jobCode, jobDescription } = req.body

      if (!title || !department) {
        return res.status(400).json({ error: 'Validation Error', message: 'title and department are required' })
      }

      const jobId = randomUUID()
      const versionId = randomUUID()
      const titlePart = title.split(' ').map((w: string) => w[0]).join('').slice(0, 3).toUpperCase()
      const deptPart = department.slice(0, 3).toUpperCase()
      const randomPart = Math.random().toString(36).substring(2, 6).toUpperCase()
      const generatedCode = jobCode || `${titlePart}-${deptPart}-${randomPart}`

      const config: JobConfigVersion = {
        versionId,
        jobId,
        rubric: rubric || [],
        mustHaves: mustHaves || [],
        desiredCriteria: desiredCriteria || [],
        runsPerApplication: runsPerApplication || 3,
        aggregationStrategy: aggregationStrategy || 'median',
        longlistThreshold: longlistThreshold || 60,
        shortlistThreshold: shortlistThreshold || 75,
        varianceThreshold: varianceThreshold || 15,
        createdAt: new Date().toISOString(),
      }

      const newJob: Job = {
        jobId,
        jobCode: generatedCode,
        title,
        department,
        organization: organization || '',
        postingDate: postingDate || new Date().toISOString(),
        createdBy: req.user?.username || 'unknown',
        createdAt: new Date().toISOString(),
        status: 'Active',
        currentVersion: config,
        jobDescription: jobDescription || undefined,
        specDocumentId,
        rubricDocumentId,
        stats: {
          totalApplications: 0, queued: 0, extracting: 0, scoring: 0,
          completed: 0, failed: 0, needsManualReview: 0,
          longlistCount: 0, shortlistCount: 0, excludedCount: 0,
        },
      }

      const jobs = await getArray<Job>(storage, JOBS)
      jobs.push(newJob)
      await setArray(storage, JOBS, jobs)
      await storage.set(jobVersionsKey(jobId), [config])

      await audit.appendEvent(req.user?.username || 'unknown', 'job.created', 'Job', jobId, { title, department })

      res.status(201).json(newJob)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/extract-spec - extract job metadata from uploaded spec document via AWR_SEQ_API_ENDPOINT
  router.post('/extract-spec', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!AWR_SEQ_API_ENDPOINT) {
        return res.status(503).json({ error: 'Configuration Error', message: 'AWR_SEQ_API_ENDPOINT is not configured' })
      }

      const { fileName, content, mimeType } = req.body
      if (!fileName || !content) {
        return res.status(400).json({ error: 'Validation Error', message: 'fileName and content are required' })
      }

      const allowedExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
      const ext = fileName.toLowerCase().slice(fileName.lastIndexOf('.'))
      if (!allowedExtensions.includes(ext)) {
        return res.status(400).json({ error: 'Validation Error', message: `Unsupported file type: ${ext}. Supported: pdf, jpg, md, txt, docx` })
      }

      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/extract-spec`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileName, content, mimeType }),
      })

      if (!extractionResponse.ok) {
        const errorText = await extractionResponse.text()
        return res.status(extractionResponse.status).json({ error: 'Extraction Failed', message: errorText })
      }

      const extracted = await extractionResponse.json()
      res.json(extracted)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/extract-rubric - extract rubric from uploaded rubric document via AWR_SEQ_API_ENDPOINT
  router.post('/extract-rubric', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!AWR_SEQ_API_ENDPOINT) {
        return res.status(503).json({ error: 'Configuration Error', message: 'AWR_SEQ_API_ENDPOINT is not configured' })
      }

      const { fileName, content, mimeType } = req.body
      if (!fileName || !content) {
        return res.status(400).json({ error: 'Validation Error', message: 'fileName and content are required' })
      }

      const allowedExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
      const ext = fileName.toLowerCase().slice(fileName.lastIndexOf('.'))
      if (!allowedExtensions.includes(ext)) {
        return res.status(400).json({ error: 'Validation Error', message: `Unsupported file type: ${ext}. Supported: pdf, jpg, md, txt, docx` })
      }

      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/extract-rubric`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileName, content, mimeType }),
      })

      if (!extractionResponse.ok) {
        const errorText = await extractionResponse.text()
        return res.status(extractionResponse.status).json({ error: 'Extraction Failed', message: errorText })
      }

      const extracted = await extractionResponse.json()
      res.json(extracted)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId - get job with computed stats
  router.get('/:jobId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      // Department check for recruiters
      if (req.user?.role === 'recruiter' && req.user.department && job.department !== req.user.department) {
        return res.status(403).json({ error: 'Forbidden', message: 'Access denied to this job' })
      }

      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const config = job.currentVersion
      const stats = {
        totalApplications: apps.length,
        queued: apps.filter(a => a.status === 'Queued').length,
        extracting: apps.filter(a => a.status === 'Extracting').length,
        scoring: apps.filter(a => a.status === 'Scoring').length,
        completed: apps.filter(a => a.status === 'Completed').length,
        failed: apps.filter(a => a.status === 'ExtractionFailed' || a.status === 'ScoringFailed').length,
        needsManualReview: apps.filter(a => a.status === 'NeedsManualReview').length,
        longlistCount: apps.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.longlistThreshold).length,
        shortlistCount: apps.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.shortlistThreshold).length,
        excludedCount: apps.filter(a => a.finalDecision === 'Excluded').length,
      }

      res.json({ ...job, stats })
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/jobs/:jobId/config - create new config version
  router.put('/:jobId/config', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold } = req.body

      const jobs = await getArray<Job>(storage, JOBS)
      const jobIndex = jobs.findIndex(j => j.jobId === jobId)
      if (jobIndex === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const newVersion: JobConfigVersion = {
        versionId: randomUUID(),
        jobId,
        rubric: rubric || jobs[jobIndex].currentVersion.rubric,
        mustHaves: mustHaves || jobs[jobIndex].currentVersion.mustHaves,
        desiredCriteria: desiredCriteria || jobs[jobIndex].currentVersion.desiredCriteria || [],
        runsPerApplication: runsPerApplication || jobs[jobIndex].currentVersion.runsPerApplication,
        aggregationStrategy: aggregationStrategy || jobs[jobIndex].currentVersion.aggregationStrategy,
        longlistThreshold: longlistThreshold ?? jobs[jobIndex].currentVersion.longlistThreshold,
        shortlistThreshold: shortlistThreshold ?? jobs[jobIndex].currentVersion.shortlistThreshold,
        varianceThreshold: varianceThreshold ?? jobs[jobIndex].currentVersion.varianceThreshold,
        createdAt: new Date().toISOString(),
      }

      await pushToArray(storage, jobVersionsKey(jobId), newVersion)
      jobs[jobIndex] = { ...jobs[jobIndex], currentVersion: newVersion }
      await setArray(storage, JOBS, jobs)

      await audit.appendEvent(req.user?.username || 'unknown', 'job.config-updated', 'Job', jobId, { versionId: newVersion.versionId })

      res.json(newVersion)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/process - trigger pipeline for queued applications
  router.post('/:jobId/process', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const queued = apps.filter(a => a.status === 'Queued')

      // Import pipeline dynamically to avoid circular deps
      const { createPipelineOrchestrator } = await import('../services/pipeline.js')
      const pipeline = createPipelineOrchestrator(storage)

      // Process in background
      for (const app of queued) {
        pipeline.processApplication(app.applicationId, jobId).catch(err => {
          console.error(`Pipeline error for ${app.applicationId}:`, err)
        })
      }

      await audit.appendEvent(req.user?.username || 'unknown', 'pipeline.triggered', 'Job', jobId, { queuedCount: queued.length })

      res.json({ message: `Processing started for ${queued.length} applications`, queuedCount: queued.length })
    } catch (err) {
      next(err)
    }
  })

  return router
}
