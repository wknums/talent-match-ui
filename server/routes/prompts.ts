import { randomUUID, createHash } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { jobRepo, applicationRepo, promptRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import { getAwrAuthHeaders } from '../services/awr-auth.js'
import { createAwrTimeoutSignal } from '../services/awr-timeout.js'
import { createPipelineOrchestrator } from '../services/pipeline.js'
import type {
  ScoringPrompt, PromptTestRun, Application,
  ApplicationDocument, ScoringRun, AggregatedResult, ManualReviewData
} from '../../src/types/index.js'

// FR-065: Prompt generation ALWAYS uses AWR_SEQ_API_ENDPOINT regardless of scoring mode
const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

export function createPromptsRouter() {
  const router = Router()
  const audit = auditService

  // GET /api/jobs/:jobId/prompts - list all prompt revisions
  router.get('/:jobId/prompts', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params
      const prompts = await promptRepo.getByJobId(jobId)
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
      const job = await jobRepo.getById(jobId)
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
      const maxVersion = await promptRepo.getMaxVersion(jobId)

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

      await promptRepo.create(prompt)

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
      const prompts = await promptRepo.getByJobId(jobId)
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

      const original = await promptRepo.getById(promptId)
      if (!original) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Create new revision with incremented version (FR-035)
      const maxVersion = await promptRepo.getMaxVersion(jobId)
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

      await promptRepo.create(newPrompt)

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
      const prompts = await promptRepo.getByJobId(jobId)
      const target = prompts.find(p => p.promptId === promptId)

      if (!target) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Deactivate any currently active prompt and activate the target (FR-036)
      await promptRepo.deactivateAllForJob(jobId)
      await promptRepo.updateStatus(promptId, 'active')

      // Audit deactivated prompts
      for (const p of prompts) {
        if (p.status === 'active' && p.promptId !== promptId) {
          audit.appendEvent(
            req.user?.username || 'unknown',
            'prompt.deactivated',
            'scoring_prompt',
            p.promptId,
            { jobId, replacedBy: promptId }
          )
        }
      }

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.activated',
        'scoring_prompt',
        promptId,
        { jobId }
      )

      const activated = await promptRepo.getById(promptId)
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

      const prompts = await promptRepo.getByJobId(jobId)
      const idx = prompts.findIndex(p => p.promptId === promptId)
      if (idx === -1) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      await promptRepo.updateRating(
        promptId,
        rating !== undefined ? Math.round(rating) : 0,
        comments !== undefined ? comments : undefined
      )

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.rated',
        'scoring_prompt',
        promptId,
        { jobId, rating, comments }
      )

      const updated = await promptRepo.getById(promptId)
      res.json(updated)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/generate - generate from rubric via external API (FR-034)
  router.post('/:jobId/prompts/generate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId } = req.params

      const job = await jobRepo.getById(jobId)
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
7. Include all eligibility gate details regardless if they are met or not.
Return ONLY the scoring prompt text, ready for use.
 At the end of the prompt, include the instruction to return all the output as valid json.`

      let promptText: string
      let generationMetadata: Record<string, any> = {}

      if (AWR_SEQ_API_ENDPOINT) {
        try {
          const awrHeaders = await getAwrAuthHeaders(req.user ? { username: req.user.username, role: req.user.role } : undefined)

          // /assess/passthrough expects multipart FormData with promptFile + specFile
          const combinedPrompt = `${systemPrompt}\n\n${JSON.stringify(rubricContext, null, 2)}`
          const formData = new FormData()
          formData.append('promptFile', new Blob([combinedPrompt], { type: 'text/plain' }), 'generate-prompt.md')
          formData.append('specFile', new Blob([JSON.stringify(rubricContext, null, 2)], { type: 'text/plain' }), 'context.md')

          const timeout = createAwrTimeoutSignal()
          const response = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
            method: 'POST',
            headers: awrHeaders,
            body: formData,
            signal: timeout.signal,
          }).finally(() => timeout.dispose())
          if (!response.ok) {
            const errorText = await response.text()
            throw new Error(`Passthrough API error (${response.status}): ${errorText}`)
          }

          // Passthrough returns the raw output text directly
          const responseText = await response.text()
          promptText = responseText
          generationMetadata = { source: 'AWR_SEQ_API', timestamp: new Date().toISOString() }
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
      const prompt = await promptRepo.getById(promptId)
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
        'text/markdown', 'text/plain', 'image/jpeg', 'image/png', 'image/webp', 'image/gif'
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
          rawContent: file.content,
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

        await applicationRepo.create(app)
        await applicationRepo.createDocument(doc)
        await applicationRepo.storeBlob(doc.documentId, file.content)
        applicationIds.push(applicationId)
      }

      if (applicationIds.length === 0) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'No supported test files were provided',
        })
      }

      const testRun: PromptTestRun = {
        testRunId,
        jobId,
        promptId,
        status: 'pending_scoring',
        applicationIds,
        createdAt: new Date().toISOString(),
      }

      await promptRepo.createTestRun(testRun)

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
          await promptRepo.updateTestRun(testRunId, { status: 'scoring' })

          const pipeline = createPipelineOrchestrator()
          const results = await pipeline.processApplicationsBatch(applicationIds, jobId, promptId)

          const failures = results.filter(r => r.status === 'rejected')
          if (failures.length > 0) {
            console.error(`Test-run ${testRunId}: ${failures.length}/${applicationIds.length} applications failed scoring`)
          }

          // Update status to pending_review
          await promptRepo.updateTestRun(testRunId, { status: 'pending_review', completedAt: new Date().toISOString() })
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
      const prompts = await promptRepo.getByJobId(jobId)
      const prompt = prompts.find(p => p.promptId === promptId)
      if (!prompt) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      const testRuns = await promptRepo.getTestRunsByPrompt(promptId)

      res.json(testRuns)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId - get single test run
  router.get('/:jobId/prompts/:promptId/test-runs/:testRunId', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId, testRunId } = req.params
      const testRun = await promptRepo.getTestRun(testRunId)
      if (!testRun || testRun.promptId !== promptId || testRun.jobId !== jobId) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found for this prompt' })
      }

      // Enrich with application details and scoring runs
      const testApps = await applicationRepo.getByTestRunId(testRunId)
      const applications = await Promise.all(
        testApps.map(async application => {
          const scoringRuns = await applicationRepo.getScoringRuns(application.applicationId)
          const aggregatedResult = await applicationRepo.getAggregatedResult(application.applicationId)
          const manualReview = await applicationRepo.getManualReview(application.applicationId)
          return {
            application,
            scoringRuns,
            aggregatedResult,
            manualReview,
          }
        })
      )

      res.json({ ...testRun, applications })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId/rescore - re-score all applications in a test run
  router.post('/:jobId/prompts/:promptId/test-runs/:testRunId/rescore', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId, testRunId } = req.params
      const testRun = await promptRepo.getTestRun(testRunId)
      if (!testRun || testRun.promptId !== promptId || testRun.jobId !== jobId) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found for this prompt' })
      }

      // Only allow re-scoring for pending_review or scoring_failed runs
      if (!['pending_review', 'scoring_failed', 'scoring'].includes(testRun.status)) {
        return res.status(400).json({
          error: 'Validation Error',
          message: `Cannot re-score a test run with status "${testRun.status}". Only pending_review or scoring_failed runs can be re-scored.`
        })
      }

      // Get all applications belonging to this test run
      const testApps = await applicationRepo.getByTestRunId(testRunId)
      const applicationIds = testApps.map(a => a.applicationId)

      if (applicationIds.length === 0) {
        return res.status(400).json({ error: 'Validation Error', message: 'No applications found for this test run' })
      }

      // Clear existing scoring data and reset application statuses
      for (const appId of applicationIds) {
        await applicationRepo.resetForRescore(appId)
      }

      // Update test run status to scoring
      await promptRepo.updateTestRun(testRunId, { status: 'scoring', completedAt: undefined })

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.test-run.rescore',
        'prompt_test_run',
        testRunId,
        { jobId, promptId, applicationCount: applicationIds.length }
      )

      res.json({ message: 'Re-scoring started', applicationCount: applicationIds.length })

      // Fire-and-forget: re-trigger scoring pipeline
      setImmediate(async () => {
        try {
          const pipeline = createPipelineOrchestrator()
          const results = await pipeline.processApplicationsBatch(applicationIds, jobId, promptId)

          const failures = results.filter(r => r.status === 'rejected')
          if (failures.length > 0) {
            console.error(`Re-score test-run ${testRunId}: ${failures.length}/${applicationIds.length} applications failed`)
          }

          await promptRepo.updateTestRun(testRunId, { status: 'pending_review', completedAt: new Date().toISOString() })
        } catch (err) {
          console.error(`Re-score test-run ${testRunId} failed:`, err)
        }
      })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/test-runs/:testRunId/approve - approve test run (FR-039)
  router.post('/:jobId/prompts/:promptId/test-runs/:testRunId/approve', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId, testRunId } = req.params
      const testRun = await promptRepo.getTestRun(testRunId)
      if (!testRun) {
        return res.status(404).json({ error: 'Not Found', message: 'Test run not found' })
      }

      // Verify all test applications completed manual review without score changes (FR-039)
      const testApps = await applicationRepo.getByTestRunId(testRunId)

      const allCompleted = testApps.every(a =>
        a.status === 'Completed' || a.status === 'NeedsManualReview'
      )

      if (!allCompleted) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'All test applications must be scored and reviewed before approval'
        })
      }

      await promptRepo.updateTestRun(testRunId, {
        status: 'approved',
        completedAt: new Date().toISOString(),
        reviewedBy: req.user?.userId,
        reviewNotes: req.body.reviewNotes,
      })

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.test-run.approved',
        'prompt_test_run',
        testRunId,
        { jobId, promptId }
      )

      const updatedTestRun = await promptRepo.getTestRun(testRunId)
      res.json(updatedTestRun)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/jobs/:jobId/prompts/:promptId/approve-production - approve for production (FR-040)
  router.post('/:jobId/prompts/:promptId/approve-production', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { jobId, promptId } = req.params

      const prompt = await promptRepo.getById(promptId)
      if (!prompt) {
        return res.status(404).json({ error: 'Not Found', message: 'Prompt not found' })
      }

      // Verify a PromptTestRun for this prompt has status=approved (FR-040)
      const testRuns = await promptRepo.getTestRunsByPrompt(promptId)
      const hasApprovedTestRun = testRuns.some(tr => tr.status === 'approved')

      if (!hasApprovedTestRun) {
        return res.status(400).json({
          error: 'Validation Error',
          message: 'A test run for this prompt must be approved before production approval (FR-040)'
        })
      }

      // Set prompt to production-approved; deactivate previous production-approved (FR-036)
      await promptRepo.deactivateAllForJob(jobId)
      await promptRepo.updateStatus(promptId, 'production-approved')

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'prompt.production-approved',
        'scoring_prompt',
        promptId,
        { jobId }
      )

      const approved = await promptRepo.getById(promptId)
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
