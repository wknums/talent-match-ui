import { createHash, randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { jobRepo, applicationRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import type {
  Application, ApplicationDocument, ExtractionArtifact,
  ScoringRun, AggregatedResult, ManualReviewData, ManualReviewAuditEntry
} from '../../src/types/index.js'

function normalizeCategoryName(name: string): string {
  return name
    .toLowerCase()
    .replace(/[_\-()&/,;:]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
}

function findStoredRubricEntry(
  rubricScores: Record<string, { score?: number; points?: number; maxPoints?: number; comment?: string }>,
  categoryId: string,
  categoryName: string,
): { score?: number; points?: number; maxPoints?: number; comment?: string } | undefined {
  if (rubricScores[categoryId]) {
    return rubricScores[categoryId]
  }

  if (rubricScores[categoryName]) {
    return rubricScores[categoryName]
  }

  const normalizedCategoryName = normalizeCategoryName(categoryName)
  return Object.entries(rubricScores).find(([key]) => normalizeCategoryName(key) === normalizedCategoryName)?.[1]
}

function normalizeDecision(value: unknown): 'Eligible' | 'Excluded' | 'NeedsManualReview' | null {
  if (value === 'Eligible' || value === 'Excluded' || value === 'NeedsManualReview') {
    return value
  }
  return null
}

export function createApplicationsRouter() {
  const router = Router()
  const audit = auditService

  // POST /api/jobs/:jobId/applications/upload - bulk upload
  router.post('/jobs/:jobId/applications/upload', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
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
        const duplicate = await applicationRepo.findDuplicateFingerprint(jobId, fingerprint)
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
          rawContent: file.content,
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

        await applicationRepo.create(application)
        await applicationRepo.createDocument(doc)
        // Persist raw file content as base64 blob (FR-056)
        await applicationRepo.storeBlob(documentId, file.content)

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
      const { status, list, sortField, sortOrder, varianceMin, page, pageSize, applicantName } = req.query

      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const includeTestCases = req.query.includeTestCases === 'true'
      let apps = includeTestCases
        ? await applicationRepo.getByJobIdAll(jobId)
        : await applicationRepo.getByJobId(jobId)

      let filtered = apps

      // Filter by status
      if (status) {
        filtered = filtered.filter(a => a.status === status)
      }

      // Case-insensitive applicant-name search for recruiter/admin workflows.
      if (typeof applicantName === 'string' && applicantName.trim().length > 0) {
        const needle = applicantName.trim().toLocaleLowerCase()
        filtered = filtered.filter((a) => (a.candidateName || '').toLocaleLowerCase().includes(needle))
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
      const foundApp = await applicationRepo.getById(applicationId)

      if (!foundApp) {
        return res.status(404).json({ error: 'Not Found', message: 'Application not found' })
      }

      const documents = await applicationRepo.getDocuments(applicationId)
      const extraction = await applicationRepo.getExtraction(applicationId)
      const runs = await applicationRepo.getScoringRuns(applicationId)
      const result = await applicationRepo.getAggregatedResult(applicationId)

      res.json({
        ...foundApp,
        documents,
        extraction,
        scoringRuns: runs,
        aggregatedResult: result,
      })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/runs
  router.get('/applications/:applicationId/runs', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const runs = await applicationRepo.getScoringRuns(applicationId)
      res.json(runs)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/result
  router.get('/applications/:applicationId/result', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const result = await applicationRepo.getAggregatedResult(applicationId)
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
      const extraction = await applicationRepo.getExtraction(applicationId)
      if (!extraction) {
        return res.status(404).json({ error: 'Not Found', message: 'No extraction artifact yet' })
      }
      res.json(extraction)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/documents/:documentId/content - raw document bytes (FR-057)
  router.get('/applications/:applicationId/documents/:documentId/content', async (req, res, next) => {
    try {
      const { applicationId, documentId } = req.params

      // Find the document metadata for MIME type
      const documents = await applicationRepo.getDocuments(applicationId)
      const doc = documents.find(d => d.documentId === documentId)
      if (!doc) {
        return res.status(404).json({ error: 'Not Found', message: 'Document not found' })
      }

      // Load raw content from blob storage
      const rawContent = await applicationRepo.getBlob(documentId)
      if (!rawContent) {
        return res.status(404).json({ error: 'Not Found', message: 'Document content not available' })
      }

      // Detect whether stored content is base64 or raw text (legacy uploads used file.text())
      const isBase64 = /^[A-Za-z0-9+/\r\n]+=*$/.test(rawContent.slice(0, 256)) && !rawContent.startsWith('%PDF')
      const buffer = isBase64 ? Buffer.from(rawContent, 'base64') : Buffer.from(rawContent)
      res.set('Content-Type', doc.mimeType)
      res.set('Content-Disposition', `inline; filename="${doc.fileName}"`)
      res.set('Content-Length', String(buffer.length))
      res.send(buffer)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/applications/:applicationId/manual-review
  router.get('/applications/:applicationId/manual-review', async (req, res, next) => {
    try {
      const { applicationId } = req.params
      const review = await applicationRepo.getManualReview(applicationId)
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
      const { rubricScores, overallComment, adjustedFinalScore, humanEdited, finalDecision } = req.body
      const normalizedAdjustedFinalScore = Number.isFinite(adjustedFinalScore) ? Number(adjustedFinalScore) : undefined
      const manualDecisionOverride = normalizeDecision(finalDecision)

      const existing = await applicationRepo.getManualReview(applicationId)
      const auditTrail = [...(existing?.auditTrail || [])]
      const effectiveJobId = existing?.jobId || req.body.jobId || ''
      const job = effectiveJobId ? await jobRepo.getById(effectiveJobId) : null
      const rubricCategories = job?.currentVersion.rubric ?? []
      const categoryNameById = new Map(job?.currentVersion.rubric.map(category => [category.id, category.name]) ?? [])
      const existingRubricScores = (existing?.rubricScores || {}) as Record<string, { score?: number; points?: number; maxPoints?: number; comment?: string }>
      const incomingRubricScores = (rubricScores || {}) as Record<string, { score?: number; points?: number; maxPoints?: number; comment?: string }>
      const responseRubricScores: Record<string, { score: number; points: number; maxPoints: number; comment: string }> = {}
      const storedRubricScores: Record<string, { score: number; points: number; maxPoints: number; comment: string }> = {}
      let detectedHumanEdit = false

      for (const rubricCategory of rubricCategories) {
        const categoryId = rubricCategory.id
        const categoryName = rubricCategory.name
        const curr = findStoredRubricEntry(incomingRubricScores, categoryId, categoryName)
        if (!curr) {
          continue
        }

        const prev = findStoredRubricEntry(existingRubricScores, categoryId, categoryName)
        const maxPoints = Number.isFinite(curr.maxPoints) ? Number(curr.maxPoints) : Math.round(rubricCategory.weight * 100)
        const points = Number.isFinite(curr.points)
          ? Number(curr.points)
          : (Number.isFinite(curr.score) ? Math.round((Number(curr.score) / 100) * maxPoints) : 0)
        const score = Number.isFinite(curr.score)
          ? Number(curr.score)
          : (maxPoints > 0 ? (points / maxPoints) * 100 : 0)
        const normalizedEntry = {
          score,
          points,
          maxPoints,
          comment: curr.comment || '',
        }

        responseRubricScores[categoryId] = normalizedEntry
        storedRubricScores[categoryName] = normalizedEntry

        const previousPoints = Number.isFinite(prev?.points)
          ? Number(prev?.points)
          : (Number.isFinite(prev?.score) && maxPoints > 0 ? Math.round((Number(prev?.score) / 100) * maxPoints) : 0)
        const previousComment = prev?.comment || ''

        if (!prev || previousPoints !== points) {
          if (existing) {
            detectedHumanEdit = true
          }
          const entry: ManualReviewAuditEntry = {
            entryId: randomUUID(),
            applicationId,
            reviewerId: req.user?.userId || 'unknown',
            reviewerName: req.user?.fullName || 'Unknown',
            timestamp: new Date().toISOString(),
            changeType: 'score_adjustment',
            categoryId,
            categoryName,
            previousValue: previousPoints,
            newValue: points,
          }
          auditTrail.push(entry)
        }

        if (previousComment.trim() !== normalizedEntry.comment.trim()) {
          if (existing) {
            detectedHumanEdit = true
          }
          auditTrail.push({
            entryId: randomUUID(),
            applicationId,
            reviewerId: req.user?.userId || 'unknown',
            reviewerName: req.user?.fullName || 'Unknown',
            timestamp: new Date().toISOString(),
            changeType: 'comment_added',
            categoryId,
            categoryName,
            previousValue: previousComment,
            newValue: normalizedEntry.comment,
            comment: normalizedEntry.comment,
          })
        }
      }

      // Add comment audit entry if comment changed
      if ((existing?.overallComment || '') !== (overallComment || '')) {
        if (existing) {
          detectedHumanEdit = true
        }
        auditTrail.push({
          entryId: randomUUID(),
          applicationId,
          reviewerId: req.user?.userId || 'unknown',
          reviewerName: req.user?.fullName || 'Unknown',
          timestamp: new Date().toISOString(),
          changeType: 'comment_added',
          categoryName: 'overall',
          previousValue: existing?.overallComment,
          newValue: overallComment,
          comment: overallComment,
        })
      }

      const currentApp = await applicationRepo.getById(applicationId)
      if (!currentApp) {
        return res.status(404).json({ error: 'Not Found', message: 'Application not found' })
      }

      if (manualDecisionOverride && currentApp.finalDecision !== manualDecisionOverride) {
        auditTrail.push({
          entryId: randomUUID(),
          applicationId,
          reviewerId: req.user?.userId || 'unknown',
          reviewerName: req.user?.fullName || 'Unknown',
          timestamp: new Date().toISOString(),
          changeType: 'decision_override',
          categoryName: 'final decision',
          previousValue: currentApp.finalDecision ?? 'Unknown',
          newValue: manualDecisionOverride,
        })
      }

      const nextHumanEdited = existing?.humanEdited === true || humanEdited === true || detectedHumanEdit

      const reviewData: ManualReviewData = {
        applicationId,
        jobId: effectiveJobId,
        rubricScores: storedRubricScores,
        overallComment: overallComment || existing?.overallComment || '',
        adjustedFinalScore: normalizedAdjustedFinalScore,
        finalDecision: manualDecisionOverride ?? currentApp.finalDecision,
        auditTrail,
        humanEdited: nextHumanEdited,
        lastModifiedAt: new Date().toISOString(),
        lastModifiedBy: req.user?.username || 'unknown',
      }

      const responseReviewData: ManualReviewData = {
        ...reviewData,
        rubricScores: responseRubricScores,
      }

      await applicationRepo.setManualReview(reviewData)

      const targetDecision = manualDecisionOverride ?? currentApp.finalDecision
      const nextStatus = targetDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed'
      await applicationRepo.updateStatus(applicationId, nextStatus, {
        finalScore: normalizedAdjustedFinalScore ?? currentApp.finalScore,
        finalDecision: targetDecision,
        variance: currentApp.variance,
        flagged: targetDecision === 'NeedsManualReview',
      })

      const existingAggregated = await applicationRepo.getAggregatedResult(applicationId)
      if (existingAggregated) {
        await applicationRepo.setAggregatedResult({
          ...existingAggregated,
          finalDecision: targetDecision ?? existingAggregated.finalDecision,
          finalScore: normalizedAdjustedFinalScore ?? existingAggregated.finalScore,
        })
      }

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'manual-review.saved',
        'Application', applicationId,
        { adjustedFinalScore: normalizedAdjustedFinalScore }
      )

      res.json(responseReviewData)
    } catch (err) {
      next(err)
    }
  })

  return router
}
