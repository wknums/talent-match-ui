import { randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { JOBS, jobVersionsKey, jobApplicationsKey } from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'
import type { Job, JobConfigVersion, Application } from '../../src/types/index.js'

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
      const { title, department, organization, postingDate, rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, specDocumentId, rubricDocumentId, jobCode, jobDescription, rubricSource, rawExtractionResponse } = req.body

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

      const formData = new FormData()

      // Add the extraction prompt as promptFile (required by /assess/passthrough)
      const promptBlob = new Blob([EXTRACT_SPEC_SYSTEM_PROMPT], { type: 'text/plain' })
      formData.append('promptFile', promptBlob, 'extract-spec-prompt.md')

      // Add the uploaded document as specFile (decoded from base64)
      const docBuffer = Buffer.from(content, 'base64')
      const docBlob = new Blob([docBuffer], { type: mimeType || 'application/octet-stream' })
      formData.append('specFile', docBlob, fileName)

      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
        method: 'POST',
        body: formData,
      })

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

      const extractionResponse = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
        method: 'POST',
        body: formData,
      })

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
      const { rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold, rubricSource, rawExtractionResponse } = req.body

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
        rubricApprovalStatus: 'draft',
        rubricSource: rubricSource || 'manual',
        rawExtractionResponse: rawExtractionResponse || undefined,
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

  // PUT /api/jobs/:jobId/rubric-approval - toggle rubric approval status
  router.put('/:jobId/rubric-approval', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { status } = req.body

      if (status !== 'approved' && status !== 'draft') {
        return res.status(400).json({ error: 'Validation Error', message: 'status must be "approved" or "draft"' })
      }

      const jobs = await getArray<Job>(storage, JOBS)
      const jobIndex = jobs.findIndex(j => j.jobId === jobId)
      if (jobIndex === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const currentVersion = jobs[jobIndex].currentVersion
      if (currentVersion.rubricApprovalStatus === status) {
        return res.status(400).json({ error: 'Validation Error', message: `Rubric is already ${status}` })
      }

      // In-place update (not a new config version per research.md R5)
      currentVersion.rubricApprovalStatus = status
      jobs[jobIndex] = { ...jobs[jobIndex], currentVersion }
      await setArray(storage, JOBS, jobs)

      // Also update the versions array
      const versions = await getArray<JobConfigVersion>(storage, jobVersionsKey(jobId))
      const versionIndex = versions.findIndex(v => v.versionId === currentVersion.versionId)
      if (versionIndex !== -1) {
        versions[versionIndex].rubricApprovalStatus = status
        await setArray(storage, jobVersionsKey(jobId), versions)
      }

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
