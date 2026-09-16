import { randomUUID } from 'node:crypto'
import { Router, type Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { jobRepo, applicationRepo, userRepo, dlqRepo, jobSpecExtractionRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import { ensureCorrelationId, mapAuthorizationError, sendAuthorizationError } from '../services/authorization-errors.js'
import type { AggregatedResult, Job, JobConfigVersion, RubricEnvelope } from '../../src/types/index.js'
import { jobSpecExtractionService } from '../services/job-spec-extraction.js'
import { createLegacyConversionProposal, isRubricV2, projectDesiredCriteria, projectLegacyRubric, projectMustHaves } from '../services/rubric-conversion.js'

type JobAccess = 'read' | 'mutate'

function hasScopedJobAccess(req: AuthenticatedRequest, job: Pick<Job, 'organizationId' | 'departmentId'>, access: JobAccess): boolean {
  const context = req.authorizationContext
  if (!context) return true
  if (context.globalRole === 'admin') return true
  if (!job.organizationId || !job.departmentId) return false

  return context.authorizations.some((authorization) => {
    if (access === 'mutate' && authorization.role === 'business_panel') return false
    if (authorization.organizationId !== job.organizationId) return false
    return authorization.departmentId === null || authorization.departmentId === job.departmentId
  })
}

async function hasValidJobScope(job: Pick<Job, 'organizationId' | 'departmentId'>): Promise<boolean> {
  return Boolean(
    job.organizationId
    && job.departmentId
    && await jobRepo.isValidScope(job.organizationId, job.departmentId)
  )
}

function sendInvalidJobScope(req: AuthenticatedRequest, res: Response, statusCode: 400 | 403) {
  return sendAuthorizationError(req, res, mapAuthorizationError(undefined, {
    code: 'invalid_job_scope',
    statusCode,
    message: 'Job organization and department must be a valid active pair.',
  }))
}

function sendForbidden(req: AuthenticatedRequest, res: Response, message: string) {
  return sendAuthorizationError(req, res, mapAuthorizationError(undefined, {
    code: 'forbidden',
    statusCode: 403,
    message,
  }))
}

function sendApiError(
  req: AuthenticatedRequest,
  res: Response,
  statusCode: number,
  error: 'validation_error' | 'validation_failed' | 'stale_version' | 'not_found' | 'already_converted',
  message: string,
  details?: Record<string, unknown>,
) {
  const correlationId = ensureCorrelationId(req, res)
  return res.status(statusCode).json({
    ...details,
    error,
    message,
    correlationId,
  })
}

async function enforceScopedJobAccess(
  req: AuthenticatedRequest,
  res: Response,
  job: Pick<Job, 'organizationId' | 'departmentId'>,
  access: JobAccess,
): Promise<boolean> {
  if (!await hasValidJobScope(job)) {
    sendInvalidJobScope(req, res, 403)
    return false
  }
  if (!hasScopedJobAccess(req, job, access)) {
    sendForbidden(req, res, `Access denied to ${access} this job`)
    return false
  }
  return true
}

function resolveRubricPayload(rubric: unknown, mustHaves: any[] | undefined, desiredCriteria: any[] | undefined) {
  const rubricEnvelope = isRubricV2(rubric) ? rubric as RubricEnvelope : undefined
  return {
    rubric: rubricEnvelope ? projectLegacyRubric(rubricEnvelope) : Array.isArray(rubric) ? rubric : [],
    rubricEnvelope,
    mustHaves: rubricEnvelope ? projectMustHaves(rubricEnvelope) : (mustHaves || []),
    desiredCriteria: rubricEnvelope ? projectDesiredCriteria(rubricEnvelope) : (desiredCriteria || []),
  }
}

async function buildCreatedByNameLookup() {
  const users = await userRepo.getAll()
  return new Map(
    users.flatMap((user) => {
      const displayName = user.fullName || user.username
      return [
        [user.userId, displayName],
        [user.username, displayName],
      ]
    })
  )
}

export function createJobsRouter() {
  const router = Router()
  const audit = auditService

  // GET /api/jobs - list jobs (department-filtered for recruiters)
  router.get('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      let jobs: Job[]
      const context = req.authorizationContext
      if (context?.globalRole === 'admin') {
        jobs = await jobRepo.getAll()
      } else if (context) {
        const scopes = context.authorizations
          .filter(authorization => authorization.organizationId)
          .map(authorization => ({
            organizationId: authorization.organizationId!,
            departmentId: authorization.departmentId ?? undefined,
          }))
        const scopedJobs = await Promise.all(scopes.map(scope => jobRepo.getByScope(scope.organizationId, scope.departmentId)))
        jobs = [...new Map(scopedJobs.flat().map(job => [job.jobId, job])).values()]
      } else if (req.user?.role === 'recruiter' && req.user.department) {
        jobs = await jobRepo.getByDepartment(req.user.department)
      } else {
        jobs = await jobRepo.getAll()
      }

      if (context) {
        const validatedJobs = await Promise.all(jobs.map(async job =>
          await hasValidJobScope(job) && hasScopedJobAccess(req, job, 'read') ? job : undefined))
        jobs = validatedJobs.filter((job): job is Job => job !== undefined)
      }

      const createdByNameLookup = await buildCreatedByNameLookup()

      // Compute stats for each job (exclude test scoring applications)
      const jobsWithStats = await Promise.all(
        jobs.map(async (job) => {
          const stats = await jobRepo.getJobStats(job.jobId, job.currentVersion)
          return {
            ...job,
            createdByName: createdByNameLookup.get(job.createdBy) || 'Unknown User',
            stats,
          }
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
      const { title, department, organization, organizationId, departmentId, postingDate, rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, specDocumentId, rubricDocumentId, jobCode, jobDescription, rubricSource, rawExtractionResponse, extractionId, extractionInstructionVersionId } = req.body

      if (!title || !department) {
        return sendApiError(req, res, 400, 'validation_error', 'title and department are required')
      }
      if (!organizationId || !departmentId || !await jobRepo.isValidScope(organizationId, departmentId)) {
        return sendInvalidJobScope(req, res, 400)
      }
      if (!hasScopedJobAccess(req, { organizationId, departmentId }, 'mutate')) {
        return sendForbidden(req, res, 'Access denied to this job scope')
      }

      const jobId = randomUUID()
      const versionId = randomUUID()
      const titlePart = title.split(' ').map((w: string) => w[0]).join('').slice(0, 3).toUpperCase()
      const deptPart = department.slice(0, 3).toUpperCase()
      const randomPart = Math.random().toString(36).substring(2, 6).toUpperCase()
      const generatedCode = jobCode || `${titlePart}-${deptPart}-${randomPart}`

      const rubricPayload = resolveRubricPayload(rubric, mustHaves, desiredCriteria)
      const config: JobConfigVersion = {
        versionId,
        jobId,
        rubric: rubricPayload.rubric,
        rubricEnvelope: rubricPayload.rubricEnvelope,
        mustHaves: rubricPayload.mustHaves,
        desiredCriteria: rubricPayload.desiredCriteria,
        runsPerApplication: runsPerApplication || 3,
        aggregationStrategy: aggregationStrategy || 'median',
        longlistThreshold: longlistThreshold || 60,
        shortlistThreshold: shortlistThreshold || 75,
        varianceThreshold: varianceThreshold || 15,
        rubricApprovalStatus: rubricSource === 'manual' ? 'approved' : 'draft',
        rubricSource: rubricSource || 'manual',
        rawExtractionResponse: rawExtractionResponse || undefined,
        extractionId: extractionId || undefined,
        extractionInstructionVersionId: extractionInstructionVersionId || undefined,
        createdAt: new Date().toISOString(),
      }

      const newJob: Job = {
        jobId,
        jobCode: generatedCode,
        title,
        department,
        organization: organization || '',
        organizationId,
        departmentId,
        postingDate: postingDate || new Date().toISOString(),
        createdBy: req.user?.userId || 'unknown',
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

      const jobs = await jobRepo.getAll()
      // Check for duplicate job code
      if (generatedCode && jobs.some(j => j.jobCode === generatedCode)) {
        return res.status(409).json({ error: 'Conflict', message: 'Job code already exists' })
      }

      await jobRepo.create(newJob)
      if (config.extractionId) {
        await jobSpecExtractionRepo.linkToJobConfig(config.extractionId, jobId, versionId)
      }

      await audit.appendEvent(req.user?.username || 'unknown', 'job.created', 'Job', jobId, { title, department })

      res.status(201).json(newJob)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/extract-spec - extract job metadata from uploaded spec document via AWR_SEQ_API_ENDPOINT
  router.post('/extract-spec', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { fileName, content, mimeType } = req.body
      if (!fileName || !content) {
        return sendApiError(req, res, 400, 'validation_error', 'fileName and content are required')
      }

      const allowedExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
      const ext = fileName.toLowerCase().slice(fileName.lastIndexOf('.'))
      if (!allowedExtensions.includes(ext)) {
        return sendApiError(req, res, 400, 'validation_error', `Unsupported file type: ${ext}. Supported: pdf, jpg, md, txt, docx`)
      }

      const outcome = await jobSpecExtractionService.execute({
        fileName,
        contentBase64: content,
        mimeType,
        purpose: 'job_creation',
        actor: req.user,
      })
      if (!outcome.ok) {
        return sendApiError(req, res, 422, 'validation_failed', 'Extraction response failed contract validation.', outcome.result as unknown as Record<string, unknown>)
      }
      res.json(outcome.result)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/extract-rubric - extract rubric from uploaded rubric document via AWR_SEQ_API_ENDPOINT
  router.post('/extract-rubric', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { fileName, content, mimeType } = req.body
      if (!fileName || !content) {
        return sendApiError(req, res, 400, 'validation_error', 'fileName and content are required')
      }

      const allowedExtensions = ['.pdf', '.jpg', '.jpeg', '.md', '.txt', '.docx']
      const ext = fileName.toLowerCase().slice(fileName.lastIndexOf('.'))
      if (!allowedExtensions.includes(ext)) {
        return sendApiError(req, res, 400, 'validation_error', `Unsupported file type: ${ext}. Supported: pdf, jpg, md, txt, docx`)
      }

      const outcome = await jobSpecExtractionService.execute({
        fileName,
        contentBase64: content,
        mimeType,
        purpose: 'job_creation',
        actor: req.user,
      })
      if (!outcome.ok) {
        return sendApiError(req, res, 422, 'validation_failed', 'Extraction response failed contract validation.', outcome.result as unknown as Record<string, unknown>)
      }
      res.json(outcome.result.rubric)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId - get job with computed stats
  router.get('/:jobId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return sendApiError(req, res, 404, 'not_found', 'Job not found')
      }

      if (!await enforceScopedJobAccess(req, res, job, 'read')) return

      // Department check for recruiters
      if (!req.authorizationContext && req.user?.role === 'recruiter' && req.user.department && job.department !== req.user.department) {
        return sendForbidden(req, res, 'Access denied to this job')
      }

      const stats = await jobRepo.getJobStats(jobId, job.currentVersion)
      const createdByNameLookup = await buildCreatedByNameLookup()
      const extraction = job.currentVersion.extractionId
        ? await jobSpecExtractionRepo.getByConfigVersionId(job.currentVersion.versionId)
        : undefined

      res.json({
        ...job,
        currentVersion: {
          ...job.currentVersion,
          extraction,
        },
        createdByName: createdByNameLookup.get(job.createdBy) || 'Unknown User',
        stats,
      })
    } catch (err) {
      next(err)
    }
  })

  // DELETE /api/jobs/:jobId - delete job and cascade related data
  router.delete('/:jobId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return sendApiError(req, res, 404, 'not_found', 'Job not found')
      }
      if (!req.authorizationContext && req.user?.role !== 'admin') {
        return sendForbidden(req, res, 'This action requires the admin role')
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const stats = await jobRepo.getJobStats(jobId, job.currentVersion)
      const deleted = await jobRepo.delete(jobId)
      if (!deleted) {
        return sendApiError(req, res, 404, 'not_found', 'Job not found')
      }

      await audit.appendEvent(req.user?.username || 'unknown', 'job.deleted', 'Job', jobId, {
        title: job.title,
        totalApplications: stats.totalApplications,
      })

      res.status(204).send()
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/jobs/:jobId/config - create new config version
  router.put('/:jobId/config', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, rubricSource, rawExtractionResponse, rubricApprovalStatus, extractionId, extractionInstructionVersionId, expectedConfigVersionId } = req.body

      const job = await jobRepo.getById(jobId)
      if (!job) {
        return sendApiError(req, res, 404, 'not_found', 'Job not found')
      }

      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return
      if (expectedConfigVersionId && expectedConfigVersionId !== job.currentVersion.versionId) {
        return sendApiError(req, res, 409, 'stale_version', 'stale_version: the current configuration has changed and must be reloaded before saving.')
      }

      const rubricPayload = resolveRubricPayload(rubric, mustHaves, desiredCriteria)

      const newVersion: JobConfigVersion = {
        versionId: randomUUID(),
        jobId,
        rubric: rubricPayload.rubric.length ? rubricPayload.rubric : job.currentVersion.rubric,
        rubricEnvelope: rubricPayload.rubricEnvelope,
        mustHaves: rubricPayload.mustHaves.length ? rubricPayload.mustHaves : job.currentVersion.mustHaves,
        desiredCriteria: rubricPayload.desiredCriteria.length ? rubricPayload.desiredCriteria : job.currentVersion.desiredCriteria || [],
        runsPerApplication: runsPerApplication || job.currentVersion.runsPerApplication,
        aggregationStrategy: aggregationStrategy || job.currentVersion.aggregationStrategy,
        longlistThreshold: longlistThreshold ?? job.currentVersion.longlistThreshold,
        shortlistThreshold: shortlistThreshold ?? job.currentVersion.shortlistThreshold,
        varianceThreshold: varianceThreshold ?? job.currentVersion.varianceThreshold,
        rubricApprovalStatus: rubricApprovalStatus || job.currentVersion.rubricApprovalStatus || 'draft',
        rubricSource: rubricSource || 'manual',
        rawExtractionResponse: rawExtractionResponse || undefined,
        extractionId: extractionId || job.currentVersion.extractionId,
        extractionInstructionVersionId: extractionInstructionVersionId || job.currentVersion.extractionInstructionVersionId,
        createdAt: new Date().toISOString(),
      }

      await jobRepo.addConfigVersion(newVersion)
      if (newVersion.extractionId) {
        await jobSpecExtractionRepo.linkToJobConfig(newVersion.extractionId, jobId, newVersion.versionId)
      }

      await audit.appendEvent(req.user?.username || 'unknown', 'job.config-updated', 'Job', jobId, { versionId: newVersion.versionId })

      res.json(newVersion)
    } catch (err) {
      next(err)
    }
  })

  router.post('/:jobId/rubric/convert', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { mode, expectedConfigVersionId, reviewedRubric } = req.body
      const job = await jobRepo.getById(jobId)
      if (!job) return sendApiError(req, res, 404, 'not_found', 'Job not found')
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return
      if (expectedConfigVersionId && expectedConfigVersionId !== job.currentVersion.versionId) {
        return sendApiError(req, res, 409, 'stale_version', 'stale_version: the current configuration has changed and must be reloaded before conversion.')
      }
      if (job.currentVersion.rubricEnvelope) {
        return sendApiError(req, res, 400, 'already_converted', 'The current rubric already uses rubric-v2.')
      }

      const preview = createLegacyConversionProposal(
        job.currentVersion.rubric,
        job.currentVersion.mustHaves,
        job.currentVersion.desiredCriteria || [],
        job.currentVersion.versionId,
      )

      if (mode === 'preview') return res.json(preview)

      const finalRubric = isRubricV2(reviewedRubric) ? reviewedRubric : preview
      const newVersion: JobConfigVersion = {
        versionId: randomUUID(),
        jobId,
        rubric: projectLegacyRubric(finalRubric),
        rubricEnvelope: finalRubric,
        mustHaves: projectMustHaves(finalRubric),
        desiredCriteria: projectDesiredCriteria(finalRubric),
        runsPerApplication: job.currentVersion.runsPerApplication,
        aggregationStrategy: job.currentVersion.aggregationStrategy,
        longlistThreshold: job.currentVersion.longlistThreshold,
        shortlistThreshold: job.currentVersion.shortlistThreshold,
        varianceThreshold: job.currentVersion.varianceThreshold,
        rubricApprovalStatus: job.currentVersion.rubricApprovalStatus,
        rubricSource: job.currentVersion.rubricSource,
        rawExtractionResponse: job.currentVersion.rawExtractionResponse,
        extractionId: job.currentVersion.extractionId,
        extractionInstructionVersionId: job.currentVersion.extractionInstructionVersionId,
        createdAt: new Date().toISOString(),
      }
      await jobRepo.addConfigVersion(newVersion)
      if (newVersion.extractionId) {
        await jobSpecExtractionRepo.linkToJobConfig(newVersion.extractionId, jobId, newVersion.versionId)
      }
      await audit.appendEvent(req.user?.username || 'unknown', 'job.rubric-converted', 'Job', jobId, {
        previousVersionId: job.currentVersion.versionId,
        newVersionId: newVersion.versionId,
        itemCount: finalRubric.items.length,
      })
      res.json(finalRubric)
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/jobs/:jobId/rubric-approval - toggle rubric approval status
  router.put('/:jobId/rubric-approval', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { status } = req.body

      if (status !== 'approved' && status !== 'draft') {
        return sendApiError(req, res, 400, 'validation_error', 'status must be "approved" or "draft"')
      }

      const job = await jobRepo.getById(jobId)
      if (!job) {
        return sendApiError(req, res, 404, 'not_found', 'Job not found')
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const currentVersion = job.currentVersion
      if (currentVersion.rubricApprovalStatus === status) {
        return sendApiError(req, res, 400, 'validation_error', `Rubric is already ${status}`)
      }

      // In-place update (not a new config version per research.md R5)
      await jobRepo.updateConfigVersionField(currentVersion.versionId, 'rubricApprovalStatus', status)

      const auditAction = status === 'approved' ? 'rubric.approved' : 'rubric.reverted-to-draft'
      await audit.appendEvent(
        req.user?.username || 'unknown',
        auditAction,
        'job_config_version',
        currentVersion.versionId,
        { jobId, versionId: currentVersion.versionId, previousStatus: status === 'approved' ? 'draft' : 'approved', newStatus: status }
      )

      res.json({
        versionId: currentVersion.versionId,
        rubricApprovalStatus: status,
        updatedAt: new Date().toISOString(),
      })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/process - trigger pipeline for queued applications
  router.post('/:jobId/process', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const allApps = await applicationRepo.getByJobId(jobId)
      // Only process production applications that are Queued
      const queued = allApps.filter(a => a.status === 'Queued')

      // Import pipeline dynamically to avoid circular deps
      const { createPipelineOrchestrator } = await import('../services/pipeline.js')
      const pipeline = createPipelineOrchestrator()

      // Process in background with concurrency control (AWR_MAX_PARALLEL)
      const appIds = queued.map(a => a.applicationId)
      pipeline.processApplicationsBatch(appIds, jobId).catch(err => {
        console.error(`Pipeline batch error for job ${jobId}:`, err)
      })

      await audit.appendEvent(req.user?.username || 'unknown', 'pipeline.triggered', 'Job', jobId, { queuedCount: queued.length })

      res.json({ message: `Processing started for ${queued.length} applications`, queuedCount: queued.length })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/reaggregate - recompute decisions from existing scoring runs
  router.post('/:jobId/reaggregate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const varianceThreshold = job.currentVersion.varianceThreshold ?? 15
      const longlistThreshold = job.currentVersion.longlistThreshold ?? 70
      const scoredApps = (await applicationRepo.getByJobId(jobId)).filter(
        (app) => (app.status === 'Completed' || app.status === 'NeedsManualReview') && app.finalScore !== undefined,
      )

      let updated = 0
      for (const app of scoredApps) {
        const runs = await applicationRepo.getScoringRuns(app.applicationId)
        if (runs.length === 0) {
          continue
        }

        const scores = runs.map((run) => run.overallScore)
        const meanScore = scores.reduce((sum, value) => sum + value, 0) / scores.length
        const variance = Math.sqrt(
          scores.reduce((sum, value) => sum + Math.pow(value - meanScore, 2), 0) / scores.length,
        )
        const gatePassVotes = runs.filter((run) => run.mustHaveResult?.passed === true).length
        const gateFailVotes = runs.filter((run) => run.mustHaveResult?.passed === false).length
        const gateFailedByAggregation = gateFailVotes > gatePassVotes
        const hasGateVotes = gatePassVotes + gateFailVotes > 0

        let finalDecision: AggregatedResult['finalDecision']
        let nextStatus: typeof app.status
        if (gateFailedByAggregation) {
          finalDecision = 'Excluded'
          nextStatus = 'Completed'
        } else if (variance > varianceThreshold) {
          finalDecision = 'NeedsManualReview'
          nextStatus = 'NeedsManualReview'
        } else if (meanScore >= longlistThreshold) {
          finalDecision = 'Eligible'
          nextStatus = 'Completed'
        } else {
          finalDecision = 'Excluded'
          nextStatus = 'Completed'
        }

        const changed = app.finalDecision !== finalDecision || app.status !== nextStatus
        if (!changed) {
          continue
        }

        const aggregatedResult: AggregatedResult = {
          resultId: randomUUID(),
          applicationId: app.applicationId,
          versionId: runs[0]?.versionId ?? job.currentVersion.versionId,
          finalScore: meanScore,
          finalSubScores: {},
          confidence: 1,
          variance,
          finalDecision,
          rationaleText: gateFailedByAggregation
            ? `Excluded: eligibility gate failed by aggregated votes (passed: ${gatePassVotes}, failed: ${gateFailVotes}). Score: ${meanScore.toFixed(1)} (${scores.length} run(s), variance: ${variance.toFixed(1)}).`
            : hasGateVotes
              ? `Aggregated ${scores.length} scoring run(s). Mean score: ${meanScore.toFixed(1)}, Variance: ${variance.toFixed(1)}. Eligibility votes: passed ${gatePassVotes}, failed ${gateFailVotes}.`
              : `Aggregated ${scores.length} scoring run(s). Mean score: ${meanScore.toFixed(1)}, Variance: ${variance.toFixed(1)}.`,
          recommendationsText: '',
          allRuns: [],
          createdAt: new Date().toISOString(),
        }

        await applicationRepo.setAggregatedResult(aggregatedResult)
        await applicationRepo.updateStatus(app.applicationId, nextStatus, {
          finalScore: meanScore,
          finalDecision,
          variance,
          flagged: nextStatus === 'NeedsManualReview',
        })
        updated++
      }

      await audit.appendEvent(req.user?.username || 'unknown', 'pipeline.reaggregated', 'Job', jobId, {
        updated,
        total: scoredApps.length,
      })

      res.json({ updated, total: scoredApps.length })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/retry-failed - reset failed apps to Queued and re-process
  router.post('/:jobId/retry-failed', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      // Reset failed apps to Queued and get their IDs
      const resetIds = await applicationRepo.bulkResetFailed(jobId)

      if (resetIds.length === 0) {
        return res.json({ message: 'No failed applications to retry', retriedCount: 0 })
      }

      // Remove matching DLQ items
      await dlqRepo.removeByJobAndAppIds(jobId, resetIds)

      // Re-process in background with concurrency control (AWR_MAX_PARALLEL)
      const { createPipelineOrchestrator } = await import('../services/pipeline.js')
      const pipeline = createPipelineOrchestrator()
      pipeline.processApplicationsBatch(resetIds, jobId).catch(err => {
        console.error(`Retry pipeline batch error for job ${jobId}:`, err)
      })

      await audit.appendEvent(req.user?.username || 'unknown', 'pipeline.retry-failed', 'Job', jobId, { retriedCount: resetIds.length })

      res.json({ message: `Retrying ${resetIds.length} failed applications`, retriedCount: resetIds.length })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/applications/:applicationId/rescore - re-score a single application
  router.post('/:jobId/applications/:applicationId/rescore', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, applicationId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const app = await applicationRepo.getById(applicationId)
      if (!app) {
        return res.status(404).json({ error: 'Not Found', message: 'Application not found' })
      }

      // Reset app to Queued, clear previous runs and result
      await applicationRepo.resetForRescore(applicationId)

      // Re-process
      const { createPipelineOrchestrator } = await import('../services/pipeline.js')
      const pipeline = createPipelineOrchestrator()
      pipeline.processApplication(applicationId, jobId).catch(err => {
        console.error(`Rescore pipeline error for ${applicationId}:`, err)
      })

      await audit.appendEvent(req.user?.username || 'unknown', 'application.rescore', 'Application', applicationId, { jobId })

      res.json({ success: true, message: `Application ${applicationId} queued for re-scoring` })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/scoring/cancel - request cancellation of any in-flight
  // platform-mode batches for this job (008-platform-mode-shift §7). Sequential
  // mode is a no-op success: there are no batches and the work is already
  // synchronous from the caller's perspective.
  router.post('/:jobId/scoring/cancel', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const { scoringBatchRepo } = await import('../storage/repos/index.js')
      await scoringBatchRepo.requestCancelProgress(jobId)
      const affected = await scoringBatchRepo.requestCancelByJob(jobId)

      await audit.appendEvent(req.user?.username || 'unknown', 'pipeline.cancel.requested', 'Job', jobId, {
        affectedBatches: affected,
      })

      res.json({ success: true, message: `Cancellation requested for job ${jobId}`, affectedBatches: affected })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/scoring/progress - platform-mode batched scoring progress.
  // Returns null when the job has no batches (sequential mode or never enqueued).
  router.get('/:jobId/scoring/progress', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'read')) return

      const { scoringBatchRepo } = await import('../storage/repos/index.js')
      const progress = await scoringBatchRepo.getProgress(jobId)
      res.json({ progress })
    } catch (err) {
      next(err)
    }
  })

  return router
}
