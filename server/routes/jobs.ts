import { randomUUID } from 'node:crypto'
import { Router, type Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { jobRepo, applicationRepo, userRepo, dlqRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import { getAwrAuthHeaders } from '../services/awr-auth.js'
import { createAwrTimeoutSignal } from '../services/awr-timeout.js'
import { mapAuthorizationError, sendAuthorizationError } from '../services/authorization-errors.js'
import type { AggregatedResult, Job, JobConfigVersion } from '../../src/types/index.js'

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

// FR-065: Spec extraction and rubric extraction ALWAYS use AWR_SEQ_API_ENDPOINT regardless of scoring mode
const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

const EXTRACT_SPEC_SYSTEM_PROMPT = `You are an expert information extraction and evaluation assistant. Read a job specification and produce a single JSON object that captures:

Job Title, Job Description, Department (if present), Organization (if present).
All must-have requirements.
All recommended/desired qualifications.
Experience requirements (years, domains, tools).
A rubric with categories and weights that sum to 1.0:

If a rubric is present in the document, extract its categories and weights (convert to normalized weights summing to 1.0).
If no rubric is present, generate a thoughtful draft rubric and assign 60% of the total weight to the "Must-Have Requirements" (distribute the remaining 40% carefully across other relevant categories).
For generated rubrics, include criteria mappings so each category clearly reflects items drawn from the spec.


Important

Think step-by-step privately. Do not reveal chain-of-thought.
Output only the final JSON object—no additional text.
The JSON must be valid, with proper escaping, no trailing commas, and weights that sum to exactly 1.0 (use rounding and final normalization as needed)
Output JSON Schema (contract)

You must adhere to this structure and field naming. If a field is not present in the document, use null or [] as appropriate.

{
  "job_title": "string | null",
  "job_description": "string | null",
  "department": "string | null",
  "organization": "string | null",
  "must_have_requirements": ["string", "..."],
  "recommended_or_desired": ["string", "..."],
  "experience_requirements": ["string", "..."],
  "rubric": {
    "has_rubric_in_doc": "boolean",
    "categories": [
      {
        "name": "string",
        "weight": 0.0,
        "criteria": ["string", "..."],
        "source": "doc|generated"
      }
    ],
    "weights_sum_to_1_0": "boolean"
  }
}

Extraction Rules & Heuristics


Job Title

Prefer explicit title lines/headings; otherwise infer from earliest explicit role labels.
Normalize casing (Title Case) and trim department/org suffixes unless integral to the title.


Job Description

Use the main narrative of responsibilities/role purpose.
Exclude company boilerplate unless tightly coupled to the role.


Department / Organization

Extract when explicitly present (e.g., "Department: Finance", "Reports to: Head of …" is not department unless clearly labeled).
Organization is the hiring entity or brand named as the employer.



Must-Have vs Recommended/Desired

Must-Have indicators: "must", "required", "minimum", "compulsory", "essential", "non-negotiable", "shall", "strictly required".
Recommended/Desired indicators: "nice to have", "preferred", "advantageous", "beneficial", "plus", "bonus", "good to have".
If ambiguous, default to recommended_or_desired unless the doc uses strong mandatory language.



Experience Requirements

Capture explicit experience statements: years, domains, tools, certifications with "required/mandatory/minimum" → also include the phrases in must-have if marked mandatory.
If experience is optional, keep under recommended_or_desired and still mirror relevant items in experience_requirements to preserve visibility.
De-duplicate across arrays; keep one canonical phrasing.



Deduplication & Normalization

Trim whitespace; singularize plurals if natural; remove trailing punctuation; unify acronyms (first use can include long form).

Rubric Logic
If the document contains a rubric

Extract all categories, their weights (percentages/points to be normalized to weights summing to 1.0), and any explicit criteria mapping.
Preserve original category names (normalize casing).
Set source: "doc".
After conversion and rounding to two decimals, ensure final sum equals 1.00 by adjusting the largest category by the minimal residual (±0.01 as needed).

If the document does NOT contain a rubric

Create a draft rubric with thoughtful categories and weights that sum to 1.00, assigning 0.60 (60%) to "Must-Have Requirements".
Distribute the remaining 0.40 (40%) across categories that make sense for this spec. Use these defaults unless the document strongly suggests alternatives:

Must-Have Requirements: 0.60
Recommended/Desired Qualifications: 0.20
Experience Depth & Relevance: 0.15
Role/Context Fit (Responsibilities, Domain, Soft Skills): 0.05


Tailor the category names to match the document's language (e.g., "Core Competencies", "Technical Proficiency", "Domain Knowledge"), but keep Must-Have at 0.60.
For each category, populate criteria with succinct bullet points derived from the extracted items.
Mark source: "generated" for all categories.
Round weights to two decimals and normalize to ensure the final sum equals 1.00 (adjust the "Must-Have Requirements" category by the minimal residual if needed, while staying as close as possible to 0.60).


Before emitting, silently verify:

All required top-level fields exist; use null or [] if not present.
Arrays contain strings only (no nested objects except rubric.categories).
No duplicate items across arrays; if overlaps are inherent, keep the most appropriate placement and remove duplicates.
rubric.categories non-empty; each has name, weight (number), criteria (array), and source ("doc" or "generated").
Sum of weight values equals 1.00 exactly after rounding and final normalization; set weights_sum_to_1_0: true.
Output is valid JSON with no extra commentary.`

const EXTRACT_RUBRIC_SYSTEM_PROMPT = `You are an expert rubric extraction assistant. Read a rubric or scoring criteria document and produce a single JSON object.

Extract the job title (if present) and all rubric categories with their weights.
Weights must be normalized to sum to exactly 1.0.

Output only valid JSON with no additional text:
{
  "title": "string | null",
  "categories": [
    {
      "name": "string",
      "weight": 0.0,
      "description": "string"
    }
  ]
}`

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
      const { title, department, organization, organizationId, departmentId, postingDate, rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, specDocumentId, rubricDocumentId, jobCode, jobDescription, rubricSource, rawExtractionResponse } = req.body

      if (!title || !department) {
        return res.status(400).json({ error: 'Validation Error', message: 'title and department are required' })
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
        rubricApprovalStatus: rubricSource === 'manual' ? 'approved' : 'draft',
        rubricSource: rubricSource || 'manual',
        rawExtractionResponse: rawExtractionResponse || undefined,
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

      const formData = new FormData()

      // Add the extraction prompt as promptFile (required by /assess/passthrough)
      const promptBlob = new Blob([EXTRACT_SPEC_SYSTEM_PROMPT], { type: 'text/plain' })
      formData.append('promptFile', promptBlob, 'extract-spec-prompt.md')

      // Add the uploaded document as specFile (decoded from base64)
      const docBuffer = Buffer.from(content, 'base64')
      const docBlob = new Blob([docBuffer], { type: mimeType || 'application/octet-stream' })
      formData.append('specFile', docBlob, fileName)

      const awrHeaders = await getAwrAuthHeaders(req.user ? { username: req.user.username, role: req.user.role } : undefined)
      const timeout = createAwrTimeoutSignal()
      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
        method: 'POST',
        headers: awrHeaders,
        body: formData,
        signal: timeout.signal,
      }).finally(() => timeout.dispose())

      if (!extractionResponse.ok) {
        const errorText = await extractionResponse.text()
        return res.status(extractionResponse.status).json({ error: 'Extraction Failed', message: errorText })
      }

      // Passthrough returns the raw output file directly (not wrapped in a response object)
      const responseText = await extractionResponse.text()

      // Parse the LLM JSON response
      let extracted: any
      try {
        extracted = typeof responseText === 'string' ? JSON.parse(responseText) : responseText
      } catch {
        return res.status(502).json({ error: 'Extraction Failed', message: 'Failed to parse extraction response as JSON' })
      }

      // Map the extraction contract to the UI contract
      const mapped = {
        title: extracted.job_title || null,
        jobDescription: extracted.job_description || null,
        department: extracted.department || null,
        organization: extracted.organization || null,
        mustHaves: (extracted.must_have_requirements || []).map((r: string) => ({ criterion: r, description: '' })),
        desiredCriteria: (extracted.recommended_or_desired || []).map((r: string) => ({ qualification: r, description: '' })),
        rubric: extracted.rubric?.categories?.map((c: any) => ({
          name: c.name,
          weight: c.weight,
          description: (c.criteria || []).join('; '),
        })) || [],
        raw: extracted,
      }

      res.json(mapped)
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

      const formData = new FormData()

      // Add the rubric extraction prompt as promptFile (required by /assess/passthrough)
      const promptBlob = new Blob([EXTRACT_RUBRIC_SYSTEM_PROMPT], { type: 'text/plain' })
      formData.append('promptFile', promptBlob, 'extract-rubric-prompt.md')

      // Add the uploaded document as specFile (decoded from base64)
      const docBuffer = Buffer.from(content, 'base64')
      const docBlob = new Blob([docBuffer], { type: mimeType || 'application/octet-stream' })
      formData.append('specFile', docBlob, fileName)

      const awrHeaders = await getAwrAuthHeaders(req.user ? { username: req.user.username, role: req.user.role } : undefined)
      const timeout = createAwrTimeoutSignal()
      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
        method: 'POST',
        headers: awrHeaders,
        body: formData,
        signal: timeout.signal,
      }).finally(() => timeout.dispose())

      if (!extractionResponse.ok) {
        const errorText = await extractionResponse.text()
        return res.status(extractionResponse.status).json({ error: 'Extraction Failed', message: errorText })
      }

      // Passthrough returns the raw output file directly
      const responseText = await extractionResponse.text()

      let extracted: any
      try {
        extracted = typeof responseText === 'string' ? JSON.parse(responseText) : responseText
      } catch {
        return res.status(502).json({ error: 'Extraction Failed', message: 'Failed to parse rubric extraction response as JSON' })
      }

      res.json(extracted)
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
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      if (!await enforceScopedJobAccess(req, res, job, 'read')) return

      // Department check for recruiters
      if (!req.authorizationContext && req.user?.role === 'recruiter' && req.user.department && job.department !== req.user.department) {
        return sendForbidden(req, res, 'Access denied to this job')
      }

      const stats = await jobRepo.getJobStats(jobId, job.currentVersion)
      const createdByNameLookup = await buildCreatedByNameLookup()

      res.json({
        ...job,
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
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!req.authorizationContext && req.user?.role !== 'admin') {
        return sendForbidden(req, res, 'This action requires the admin role')
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const stats = await jobRepo.getJobStats(jobId, job.currentVersion)
      const deleted = await jobRepo.delete(jobId)
      if (!deleted) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
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
      const { rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, rubricSource, rawExtractionResponse, rubricApprovalStatus } = req.body

      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const newVersion: JobConfigVersion = {
        versionId: randomUUID(),
        jobId,
        rubric: rubric || job.currentVersion.rubric,
        mustHaves: mustHaves || job.currentVersion.mustHaves,
        desiredCriteria: desiredCriteria || job.currentVersion.desiredCriteria || [],
        runsPerApplication: runsPerApplication || job.currentVersion.runsPerApplication,
        aggregationStrategy: aggregationStrategy || job.currentVersion.aggregationStrategy,
        longlistThreshold: longlistThreshold ?? job.currentVersion.longlistThreshold,
        shortlistThreshold: shortlistThreshold ?? job.currentVersion.shortlistThreshold,
        varianceThreshold: varianceThreshold ?? job.currentVersion.varianceThreshold,
        rubricApprovalStatus: rubricApprovalStatus || job.currentVersion.rubricApprovalStatus || 'draft',
        rubricSource: rubricSource || 'manual',
        rawExtractionResponse: rawExtractionResponse || undefined,
        createdAt: new Date().toISOString(),
      }

      await jobRepo.addConfigVersion(newVersion)

      await audit.appendEvent(req.user?.username || 'unknown', 'job.config-updated', 'Job', jobId, { versionId: newVersion.versionId })

      res.json(newVersion)
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
        return res.status(400).json({ error: 'Validation Error', message: 'status must be "approved" or "draft"' })
      }

      const job = await jobRepo.getById(jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }
      if (!await enforceScopedJobAccess(req, res, job, 'mutate')) return

      const currentVersion = job.currentVersion
      if (currentVersion.rubricApprovalStatus === status) {
        return res.status(400).json({ error: 'Validation Error', message: `Rubric is already ${status}` })
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
