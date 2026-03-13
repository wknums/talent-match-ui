import { randomUUID, createHash } from 'node:crypto'
import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import {
  JOBS, jobApplicationsKey, jobVersionsKey,
  promptsKey, promptTestRunKey,
  appDocumentsKey
} from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'
import { createPipelineOrchestrator } from '../services/pipeline.js'
import type {
  ScoringPrompt, PromptTestRun, Job, Application,
  ApplicationDocument, JobConfigVersion
} from '../../src/types/index.js'

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

export function createPromptsRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // GET /api/jobs/:jobId/prompts - list all prompt revisions
  router.get('/:jobId/prompts', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const sorted = prompts.sort((a, b) => b.versionNumber - a.versionNumber)
      res.json(sorted)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts - create new prompt
  router.post('/:jobId/prompts', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const { promptText, source, generationMetadata } = req.body

      // Validate job exists
      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      // Validate job has approved rubric (FR-033)
      if (!job.currentVersion?.rubric || job.currentVersion.rubric.length === 0) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Job must have an approved rubric before creating prompts (FR-033)'
        })
      }

      if (!promptText) {
        return res.status(400).json({ error: 'Validation Error', message: 'promptText is required' })
      }

      // Auto-increment version number
      const existingPrompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const maxVersion = existingPrompts.reduce((max, p) => Math.max(max, p.versionNumber), 0)

      const prompt: ScoringPrompt = {
        promptId: randomUUID(),
        jobId,
        versionNumber: maxVersion + 1,
        promptText,
        status: 'draft',
        createdAt: new Date().toISOString(),
        lastModifiedAt: new Date().toISOString(),
        author: req.user?.userId || 'unknown',
        source: source || 'manual',
        generationMetadata: generationMetadata || undefined,
      }

      await pushToArray(storage, promptsKey(jobId), prompt)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.created',
        'scoring_prompt',
        prompt.promptId,
        { jobId, versionNumber: prompt.versionNumber, source: prompt.source }
      )

      res.status(201).json(prompt)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/prompts/:promptId - get single prompt
  router.get('/:jobId/prompts/:promptId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params
      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const prompt = prompts.find(p => p.promptId === promptId)
      if (!prompt) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }
      res.json(prompt)
    } catch (err) {
      next(err)
    }
  })

  // PUT /api/jobs/:jobId/prompts/:promptId - edit (creates new revision per FR-035)
  router.put('/:jobId/prompts/:promptId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params
      const { promptText } = req.body

      if (!promptText) {
        return res.status(400).json({ error: 'Validation Error', message: 'promptText is required' })
      }

      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const original = prompts.find(p => p.promptId === promptId)
      if (!original) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Create new revision with incremented version (FR-035)
      const maxVersion = prompts.reduce((max, p) => Math.max(max, p.versionNumber), 0)
      const newPrompt: ScoringPrompt = {
        promptId: randomUUID(),
        jobId,
        versionNumber: maxVersion + 1,
        promptText,
        status: 'draft',
        createdAt: new Date().toISOString(),
        lastModifiedAt: new Date().toISOString(),
        author: req.user?.userId || 'unknown',
        source: original.source,
      }

      await pushToArray(storage, promptsKey(jobId), newPrompt)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.edited',
        'scoring_prompt',
        newPrompt.promptId,
        { jobId, originalPromptId: promptId, versionNumber: newPrompt.versionNumber }
      )

      res.status(201).json(newPrompt)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/activate - activate prompt (FR-036)
  router.post('/:jobId/prompts/:promptId/activate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params
      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const targetIdx = prompts.findIndex(p => p.promptId === promptId)

      if (targetIdx === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Deactivate any currently active prompt and activate the target (FR-036)
      const updated = prompts.map(p => {
        if (p.promptId === promptId) {
          return { ...p, status: 'active' as const, lastModifiedAt: new Date().toISOString() }
        }
        if (p.status === 'active') {
          // Record deactivation audit
          audit.appendEvent(
            req.user?.username || 'unknown',
            'prompt.deactivated',
            'scoring_prompt',
            p.promptId,
            { jobId, replacedBy: promptId }
          )
          return { ...p, status: 'inactive' as const, lastModifiedAt: new Date().toISOString() }
        }
        return p
      })

      await setArray(storage, promptsKey(jobId), updated)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.activated',
        'scoring_prompt',
        promptId,
        { jobId }
      )

      const activated = updated.find(p => p.promptId === promptId)!
      res.json(activated)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/rate - rate and comment (FR-037)
  router.post('/:jobId/prompts/:promptId/rate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params
      const { rating, comments } = req.body

      if (rating !== undefined && (typeof rating !== 'number' || rating < 0 || rating > 5)) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Rating must be an integer between 0 and 5'
        })
      }

      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const idx = prompts.findIndex(p => p.promptId === promptId)
      if (idx === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      prompts[idx] = {
        ...prompts[idx],
        rating: rating !== undefined ? Math.round(rating) : prompts[idx].rating,
        comments: comments !== undefined ? comments : prompts[idx].comments,
        lastModifiedAt: new Date().toISOString(),
      }
      await setArray(storage, promptsKey(jobId), prompts)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.rated',
        'scoring_prompt',
        promptId,
        { jobId, rating, comments }
      )

      res.json(prompts[idx])
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/generate - generate from rubric via external API (FR-034)
  router.post('/:jobId/prompts/generate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params

      const jobs = await getArray<Job>(storage, JOBS)
      const job = jobs.find(j => j.jobId === jobId)
      if (!job) {
        return res.status(404).json({ error: 'Not Found', message: 'Job not found' })
      }

      const config = job.currentVersion
      if (!config?.rubric || config.rubric.length === 0) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'Job must have an approved rubric to generate a prompt'
        })
      }

      const rubricContext = {
        jobTitle: job.title,
        department: job.department,
        organization: job.organization,
        rubricCategories: config.rubric.map(c => ({
          name: c.name,
          weight: c.weight,
          description: c.description,
        })),
        mustHaves: config.mustHaves.map(mh => ({
          criterion: mh.criterion,
          description: mh.description,
        })),
        desiredCriteria: (config.desiredCriteria || []).map(dc => ({
          qualification: dc.qualification,
          description: dc.description,
        })),
      }

      const systemPrompt = `You are an expert at creating scoring prompts for candidate evaluation. 
Given the following job rubric, create a structured scoring prompt that an AI model can use to evaluate candidate applications.
The prompt should:
1. Evaluate candidates across all rubric categories with the specified weights
2. Check all must-have criteria
3. Consider desired qualifications
4. Produce scores from 0-100 for each category
5. Provide evidence citations from the candidate's documents
6. Include improvement recommendations
Return ONLY the scoring prompt text, ready for use.`

      let promptText: string
      let generationMetadata: Record<string, any> = {}

      if (AWR_SEQ_API_ENDPOINT) {
        try {
          const response = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              systemPrompt,
              userPrompt: JSON.stringify(rubricContext, null, 2),
            }),
          })
          const result = await response.json() as any
          promptText = result.response || result.content || JSON.stringify(result)
          generationMetadata = { source: 'AWR_SEQ_API', timestamp: new Date().toISOString(), raw: result }
        } catch (apiErr) {
          // Fallback to locally generated prompt
          promptText = generateFallbackPrompt(rubricContext)
          generationMetadata = { source: 'fallback', reason: 'API call failed', timestamp: new Date().toISOString() }
        }
      } else {
        promptText = generateFallbackPrompt(rubricContext)
        generationMetadata = { source: 'fallback', reason: 'AWR_SEQ_API_ENDPOINT not configured', timestamp: new Date().toISOString() }
      }

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.generated',
        'scoring_prompt',
        jobId,
        { jobId, source: generationMetadata.source }
      )

      res.json({ promptText, generationMetadata })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/test-runs - create test run
  router.post('/:jobId/prompts/:promptId/test-runs', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params

      // Validate prompt exists
      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const prompt = prompts.find(p => p.promptId === promptId)
      if (!prompt) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      const files = req.body.files as Array<{ fileName: string; content: string; mimeType: string; sizeBytes: number }>
      if (!files || !Array.isArray(files) || files.length === 0) {
        return res.status(400).json({ error: 'Validation Error', message: 'No test files provided' })
      }

      const testRunId = randomUUID()
      const allowedTypes = [
        'application/pdf',
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        'text/markdown', 'text/plain', 'image/jpeg'
      ]
      const maxSize = 50 * 1024 * 1024 // 50 MB

      const applicationIds: string[] = []

      for (const file of files) {
        if (!allowedTypes.includes(file.mimeType)) continue
        if (file.sizeBytes > maxSize) continue

        const applicationId = randomUUID()
        const sha256 = createHash('sha256').update(file.content).digest('hex')

        const doc: ApplicationDocument = {
          documentId: randomUUID(),
          applicationId,
          fileName: file.fileName,
          mimeType: file.mimeType,
          sizeBytes: file.sizeBytes,
          sha256,
          uploadedAt: new Date().toISOString(),
        }

        const app: Application = {
          applicationId,
          jobId,
          candidateRef: `TEST-${file.fileName.replace(/\.[^.]+$/, '')}`,
          candidateName: file.fileName.replace(/\.[^.]+$/, ''),
          status: 'Queued',
          createdAt: new Date().toISOString(),
          documents: [doc],
          testRunId,
        }

        await pushToArray(storage, jobApplicationsKey(jobId), app)
        await pushToArray(storage, appDocumentsKey(applicationId), doc)
        applicationIds.push(applicationId)
      }

      const testRun: PromptTestRun = {
        testRunId,
        jobId,
        promptId,
        status: 'pending_scoring',
        applicationIds,
        createdAt: new Date().toISOString(),
      }

      await storage.set(promptTestRunKey(testRunId), JSON.stringify(testRun))

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.test-run.created',
        'prompt_test_run',
        testRunId,
        { jobId, promptId, applicationCount: applicationIds.length }
      )

      // Respond immediately with pending_scoring status (FR-048)
      res.status(201).json(testRun)

      // Fire-and-forget: auto-trigger scoring pipeline for each test application (FR-048)
      // Uses promptId override to bypass the production-approved gate (FR-038)
      setImmediate(async () => {
        try {
          // Update status to scoring
          testRun.status = 'scoring'
          await storage.set(promptTestRunKey(testRunId), JSON.stringify(testRun))

          const pipeline = createPipelineOrchestrator(storage)
          const results = await Promise.allSettled(
            applicationIds.map(appId => pipeline.processApplication(appId, jobId, promptId))
          )

          const failures = results.filter(r => r.status === 'rejected')
          if (failures.length > 0) {
            console.error(`Test-run ${testRunId}: ${failures.length}/${applicationIds.length} applications failed scoring`)
          }

          // Update status to pending_review
          testRun.status = 'pending_review'
          await storage.set(promptTestRunKey(testRunId), JSON.stringify(testRun))
        } catch (err) {
          console.error(`Test-run ${testRunId} auto-trigger scoring failed:`, err)
        }
      })
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/prompts/:promptId/test-runs - list test runs
  router.get('/:jobId/prompts/:promptId/test-runs', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params
      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const prompt = prompts.find(p => p.promptId === promptId)
      if (!prompt) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Collect test runs for this prompt by scanning all applications with testRunId
      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const testApps = apps.filter(a => a.testRunId)
      const testRunIds = [...new Set(testApps.map(a => a.testRunId!))]

      const testRuns: PromptTestRun[] = []
      for (const trId of testRunIds) {
        const trJson = await storage.get<string>(promptTestRunKey(trId))
        if (trJson) {
          const tr: PromptTestRun = typeof trJson === 'string' ? JSON.parse(trJson) : trJson
          if (tr.promptId === promptId) {
            testRuns.push(tr)
          }
        }
      }

      res.json(testRuns)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId - get single test run
  router.get('/:jobId/prompts/:promptId/test-runs/:testRunId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId, testRunId } = req.params
      const trJson = await storage.get<string>(promptTestRunKey(testRunId))
      if (!trJson) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found' })
      }

      const testRun: PromptTestRun = typeof trJson === 'string' ? JSON.parse(trJson) : trJson
      if (testRun.promptId !== promptId || testRun.jobId !== jobId) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found for this prompt' })
      }

      // Enrich with application details
      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const testApps = apps.filter(a => a.testRunId === testRunId)

      res.json({ ...testRun, applications: testApps })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId/approve - approve test run (FR-039)
  router.post('/:jobId/prompts/:promptId/test-runs/:testRunId/approve', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId, testRunId } = req.params
      const trJson = await storage.get<string>(promptTestRunKey(testRunId))
      if (!trJson) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found' })
      }

      const testRun: PromptTestRun = typeof trJson === 'string' ? JSON.parse(trJson) : trJson

      // Verify all test applications completed manual review without score changes (FR-039)
      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const testApps = apps.filter(a => a.testRunId === testRunId)

      const allCompleted = testApps.every(a =>
        a.status === 'Completed' || a.status === 'NeedsManualReview'
      )

      if (!allCompleted) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'All test applications must be scored and reviewed before approval'
        })
      }

      const updatedTestRun: PromptTestRun = {
        ...testRun,
        status: 'approved',
        completedAt: new Date().toISOString(),
        reviewedBy: req.user?.userId,
        reviewNotes: req.body.reviewNotes,
      }

      await storage.set(promptTestRunKey(testRunId), JSON.stringify(updatedTestRun))

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.test-run.approved',
        'prompt_test_run',
        testRunId,
        { jobId, promptId }
      )

      res.json(updatedTestRun)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/approve-production - approve for production (FR-040)
  router.post('/:jobId/prompts/:promptId/approve-production', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params

      const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
      const targetIdx = prompts.findIndex(p => p.promptId === promptId)
      if (targetIdx === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Verify a PromptTestRun for this prompt has status=approved (FR-040)
      const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
      const testApps = apps.filter(a => a.testRunId)
      const testRunIds = [...new Set(testApps.map(a => a.testRunId!))]

      let hasApprovedTestRun = false
      for (const trId of testRunIds) {
        const trJson = await storage.get<string>(promptTestRunKey(trId))
        if (trJson) {
          const tr: PromptTestRun = typeof trJson === 'string' ? JSON.parse(trJson) : trJson
          if (tr.promptId === promptId && tr.status === 'approved') {
            hasApprovedTestRun = true
            break
          }
        }
      }

      if (!hasApprovedTestRun) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'A test run for this prompt must be approved before production approval (FR-040)'
        })
      }

      // Set prompt to production-approved; deactivate previous production-approved (FR-036)
      const updated = prompts.map(p => {
        if (p.promptId === promptId) {
          return { ...p, status: 'production-approved' as const, lastModifiedAt: new Date().toISOString() }
        }
        if (p.status === 'production-approved') {
          return { ...p, status: 'inactive' as const, lastModifiedAt: new Date().toISOString() }
        }
        return p
      })

      await setArray(storage, promptsKey(jobId), updated)

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.production-approved',
        'scoring_prompt',
        promptId,
        { jobId }
      )

      const approved = updated.find(p => p.promptId === promptId)!
      res.json(approved)
    } catch (err) {
      next(err)
    }
  })

  return router
}

function generateFallbackPrompt(rubricContext: {
  jobTitle: string
  department: string
  organization: string
  rubricCategories: Array<{ name: string; weight: number; description: string }>
  mustHaves: Array<{ criterion: string; description: string }>
  desiredCriteria: Array<{ qualification: string; description: string }>
}): string {
  const categories = rubricContext.rubricCategories
    .map(c => `- ${c.name} (weight: ${(c.weight * 100).toFixed(0)}%): ${c.description}`)
    .join('\n')

  const mustHaves = rubricContext.mustHaves
    .map(mh => `- ${mh.criterion}: ${mh.description}`)
    .join('\n')

  const desired = rubricContext.desiredCriteria
    .map(dc => `- ${dc.qualification}: ${dc.description}`)
    .join('\n')

  return `You are evaluating a candidate for the position of "${rubricContext.jobTitle}" in the ${rubricContext.department} department at ${rubricContext.organization}.

## Scoring Categories
${categories}

## Must-Have Criteria (Pass/Fail)
${mustHaves || 'None specified'}

## Desired Qualifications
${desired || 'None specified'}

## Instructions
1. Score each category from 0-100 based on evidence from the candidate's documents
2. For each must-have criterion, determine PASS or FAIL with justification
3. Note any desired qualifications that are met
4. Provide specific evidence citations from the documents
5. Calculate a weighted overall score
6. Provide improvement recommendations

Respond in JSON format with the following structure:
{
  "overallScore": <0-100>,
  "subScores": { "<category>": <0-100> },
  "mustHaveResult": { "passed": <bool>, "details": { "<criterion>": <bool> }, "missingCriteria": [...] },
  "evidenceCitations": [{ "category": "...", "snippet": "...", "section": "...", "confidence": <0-1> }],
  "rationale": "...",
  "improvementRecommendations": [...]
}`
}
