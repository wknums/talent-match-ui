import { createHash, randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import {
  JOBS, jobApplicationsKey, appDocumentsKey, appExtractionKey,
  appRunsKey, appResultKey, appManualReviewKey
} from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'
import type {
  Application, ApplicationDocument, Job, ExtractionArtifact,
  ScoringRun, AggregatedResult, ManualReviewData, ManualReviewAuditEntry
} from '../../src/types/index.js'

export function createApplicationsRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // POST /api/jobs/:jobId/applications/upload - bulk upload
  router.post('/jobs/:jobId/applications/upload', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const files = req.body.files as Array<{ fileName: string; content: string; mimeType: string; sizeBytes: number }>
      if (!files || !Array.isArray(files) || files.length === 0) {
        return res.status(400).json({ error: 'Validation Error', message: 'No files provided' })
      }

      const allowedTypes = ['application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'text/markdown', 'text/plain']
      const maxSize = 2 * 1024 * 1024 // 2 MB

      const applicationIds: string[] = []
      const warnings: string[] = []
      const existingApps = await getArray<Application>(storage, jobApplicationsKey(jobId))

      for (const file of files) {
        // Type validation
        if (!allowedTypes.includes(file.mimeType)) {
          warnings.push(`${file.fileName}: invalid file type ${file.mimeType}`)
          continue
        }
        // Size validation
        if (file.sizeBytes > maxSize) {
          warnings.push(`${file.fileName}: exceeds 2 MB limit`)
          continue
        }

        const fingerprint = createHash('sha256').update(file.content || '').digest('hex')

        // Duplicate detection
        const allDocs: ApplicationDocument[] = []
        for (const app of existingApps) {
          const docs = await getArray<ApplicationDocument>(storage, appDocumentsKey(app.applicationId))
          allDocs.push(...docs)
        }
        const duplicate = allDocs.find(d => d.sha256 === fingerprint)
        if (duplicate) {
          warnings.push(`${file.fileName}: duplicate detected (matches ${duplicate.fileName})`)
        }

        const applicationId = randomUUID()
        const documentId = randomUUID()

        const doc: ApplicationDocument = {
          documentId,
          applicationId,
          fileName: file.fileName,
          mimeType: file.mimeType,
          sizeBytes: file.sizeBytes,
          sha256: fingerprint,
          uploadedAt: new Date().toISOString(),
        }

        const application: Application = {
          applicationId,
          jobId,
          candidateRef: `candidate-${applicationId.slice(0, 8)}`,
          candidateName: file.fileName.replace(/\.[^.]+$/, '').replace(/[_-]/g, ' '),
          status: 'Queued',
          createdAt: new Date().toISOString(),
          documents: [doc],
        }

        await pushToArray(storage, jobApplicationsKey(jobId), application)
        await storage.set(appDocumentsKey(applicationId), [doc])

        applicationIds.push(applicationId)
      }

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'applications.uploaded',
        'Job', jobId,
        { count: applicationIds.length, warnings }
      )

      res.status(201).json({ applicationIds, warnings })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/applications - list with filters
  router.get('/jobs/:jobId/applications', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { status, list, sortField, sortOrder, varianceMin, page, pageSize } = req.query

      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      let apps = await getArray<Application>(storage, jobApplicationsKey(jobId))

      // Enrich apps with scoring data
      const enrichedApps = await Promise.all(apps.map(async (app) => {
        const result = await storage.get<AggregatedResult>(appResultKey(app.applicationId))
        if (result) {
          return {
            ...app,
            finalScore: result.finalScore,
            finalDecision: result.finalDecision,
            variance: result.variance,
          }
        }
        return app
      }))

      let filtered = enrichedApps

      // Filter by status
      if (status) {
        filtered = filtered.filter(a => a.status === status)
      }

      // Filter by list type using thresholds
      if (list) {
        const config = job.currentVersion
        if (list === 'longlist') {
          filtered = filtered.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.longlistThreshold)
        } else if (list === 'shortlist') {
          filtered = filtered.filter(a => a.finalDecision === 'Eligible' && a.finalScore != null && a.finalScore >= config.shortlistThreshold)
        } else if (list === 'excluded') {
          filtered = filtered.filter(a => a.finalDecision === 'Excluded')
        }
      }

      // Variance filter
      if (varianceMin) {
        const minVar = parseFloat(varianceMin as string)
        filtered = filtered.filter(a => a.variance != null && a.variance >= minVar)
      }

      // Sorting
      if (sortField) {
        const order = sortOrder === 'asc' ? 1 : -1
        filtered.sort((a, b) => {
          if (sortField === 'score') {
            return ((a.finalScore || 0) - (b.finalScore || 0)) * order
          }
          if (sortField === 'name') {
            return (a.candidateName || '').localeCompare(b.candidateName || '') * order
          }
          if (sortField === 'date') {
            return (new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()) * order
          }
          return 0
        })
      }

      // Pagination
      const p = parseInt(page as string) || 1
      const ps = parseInt(pageSize as string) || 50
      const start = (p - 1) * ps
      const paged = filtered.slice(start, start + ps)

      res.json({
        applications: paged,
        total: filtered.length,
        page: p,
        pageSize: ps,
      })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId - full application detail
  router.get('/applications/:applicationId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { applicationId } = req.params

      // Find application across all jobs
      const jobs = await getArray<Job>(storage, JOBS)
      let foundApp: Application | null = null
      for (const job of jobs) {
        const apps = await getArray<Application>(storage, jobApplicationsKey(job.jobId))
        const app = apps.find(a => a.applicationId === applicationId)
        if (app) {
          foundApp = app
          break
        }
      }

      if (!foundApp) {
        return res.status(404).json({ error: 'Not Found', message: 'Application not found' })
      }

      const documents = await getArray<ApplicationDocument>(storage, appDocumentsKey(applicationId))
      const extraction = await storage.get<ExtractionArtifact>(appExtractionKey(applicationId))
      const runs = await getArray<ScoringRun>(storage, appRunsKey(applicationId))
      const result = await storage.get<AggregatedResult>(appResultKey(applicationId))

      res.json({
        ...foundApp,
        documents,
        extraction,
        scoringRuns: runs,
        aggregatedResult: result,
        finalScore: result?.finalScore ?? foundApp.finalScore,
        finalDecision: result?.finalDecision ?? foundApp.finalDecision,
        variance: result?.variance ?? foundApp.variance,
      })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/runs
  router.get('/applications/:applicationId/runs', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const runs = await getArray<ScoringRun>(storage, appRunsKey(applicationId))
      res.json(runs)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/result
  router.get('/applications/:applicationId/result', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const result = await storage.get<AggregatedResult>(appResultKey(applicationId))
      if (!result) {
        return res.status(404).json({ error: 'Not Found', message: 'No aggregated result yet' })
      }
      res.json(result)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/extraction
  router.get('/applications/:applicationId/extraction', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const extraction = await storage.get<ExtractionArtifact>(appExtractionKey(applicationId))
      if (!extraction) {
        return res.status(404).json({ error: 'Not Found', message: 'No extraction artifact yet' })
      }
      res.json(extraction)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/manual-review
  router.get('/applications/:applicationId/manual-review', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const review = await storage.get<ManualReviewData>(appManualReviewKey(applicationId))
      if (!review) {
        return res.json(null)
      }
      res.json(review)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/applications/:applicationId/manual-review
  router.post('/applications/:applicationId/manual-review', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { applicationId } = req.params
      const { rubricScores, overallComment, adjustedFinalScore } = req.body

      const existing = await storage.get<ManualReviewData>(appManualReviewKey(applicationId))
      const auditTrail = existing?.auditTrail || []

      // Generate audit entries for changed scores
      if (existing?.rubricScores && rubricScores) {
        for (const [categoryId, newScore] of Object.entries(rubricScores)) {
          const prev = existing.rubricScores[categoryId]
          const curr = newScore as { points: number; maxPoints: number; comment: string }
          if (!prev || prev.points !== curr.points) {
            const entry: ManualReviewAuditEntry = {
              entryId: randomUUID(),
              applicationId,
              reviewerId: req.user?.userId || 'unknown',
              reviewerName: req.user?.fullName || 'Unknown',
              timestamp: new Date().toISOString(),
              changeType: 'score_adjustment',
              categoryId,
              categoryName: categoryId,
              previousValue: prev?.points,
              newValue: curr.points,
            }
            auditTrail.push(entry)
          }
        }
      }

      // Add comment audit entry if comment changed
      if (existing?.overallComment !== overallComment && overallComment) {
        auditTrail.push({
          entryId: randomUUID(),
          applicationId,
          reviewerId: req.user?.userId || 'unknown',
          reviewerName: req.user?.fullName || 'Unknown',
          timestamp: new Date().toISOString(),
          changeType: 'comment_added',
          previousValue: existing?.overallComment,
          newValue: overallComment,
        })
      }

      const reviewData: ManualReviewData = {
        applicationId,
        jobId: existing?.jobId || req.body.jobId || '',
        rubricScores: rubricScores || existing?.rubricScores || {},
        overallComment: overallComment || existing?.overallComment || '',
        adjustedFinalScore,
        auditTrail,
        lastModifiedAt: new Date().toISOString(),
        lastModifiedBy: req.user?.username || 'unknown',
      }

      await storage.set(appManualReviewKey(applicationId), reviewData)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'manual-review.saved',
        'Application', applicationId,
        { adjustedFinalScore }
      )

      res.json(reviewData)
    } catch (err) {
      next(err)
    }
  })

  return router
}
