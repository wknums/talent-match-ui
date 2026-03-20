import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { DLQ, JOBS, jobApplicationsKey, appResultKey } from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from './audit.js'
import { runScoring } from '../workers/scoring.js'
import type { ScoringResult } from '../workers/scoring.js'
import { getProductionApprovedPromptId } from './prompt-helpers.js'
import type { Application, Job, DLQItem, AggregatedResult } from '../../src/types/index.js'

const MAX_RETRIES = 3
const BACKOFF_MS = 1000

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''
const AWR_PLATFORM_API_ENDPOINT = process.env.AWR_PLATFORM_API_ENDPOINT || ''

// T174: Scoring mode detection (FR-061, FR-066)
export type ScoringMode = 'sequential' | 'platform'

export function detectScoringMode(): ScoringMode {
  if (!AWR_PLATFORM_API_ENDPOINT || AWR_PLATFORM_API_ENDPOINT === AWR_SEQ_API_ENDPOINT) {
    return 'sequential'
  }
  return 'platform'
}

const resolvedScoringMode = detectScoringMode()
console.log(`[Pipeline] Scoring mode resolved: ${resolvedScoringMode} (SEQ=${AWR_SEQ_API_ENDPOINT || '(unset)'}, PLATFORM=${AWR_PLATFORM_API_ENDPOINT || '(unset)'})`)

async function withRetry<T>(
  fn: () => Promise<T>,
  retries: number = MAX_RETRIES,
): Promise<T> {
  for (let attempt = 1; attempt <= retries; attempt++) {
    try {
      return await fn()
    } catch (err) {
      if (attempt === retries) throw err
      await new Promise(r => setTimeout(r, BACKOFF_MS * Math.pow(2, attempt - 1)))
    }
  }
  throw new Error('Max retries exceeded')
}

// T179: Platform mode scoring - async submission + polling
async function scorePlatformMode(
  storage: StorageProvider,
  applicationId: string,
  jobId: string,
  runCount: number,
  promptVersionId: string,
): Promise<ScoringResult> {
  const { getAwrAuthHeaders } = await import('../services/awr-auth.js')
  const { appDocumentsKey, appDocBlobKey, promptsKey, JOBS: JOBS_KEY } = await import('../storage/kv-keys.js')

  const jobs = await getArray<Job>(storage, JOBS_KEY)
  const job = jobs.find(j => j.jobId === jobId)
  if (!job) throw new Error(`Job ${jobId} not found`)

  const prompts = await getArray<import('../../src/types/index.js').ScoringPrompt>(storage, promptsKey(jobId))
  const prompt = prompts.find(p => p.promptId === promptVersionId)
  if (!prompt) throw new Error(`Prompt ${promptVersionId} not found`)

  const documents = await getArray<import('../../src/types/index.js').ApplicationDocument>(storage, appDocumentsKey(applicationId))
  if (!documents.length) throw new Error(`No documents for application ${applicationId}`)
  const primaryDoc = documents[0]
  const rawContent = await storage.get<string>(appDocBlobKey(applicationId, primaryDoc.documentId))
  if (!rawContent) throw new Error(`No document blob for application ${applicationId}`)

  const jobDescriptionText = job.jobDescription || job.title
  const resolvedPrompt = prompt.promptText.replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)

  const docBuffer = Buffer.from(rawContent, 'base64')
  const formData = new FormData()
  formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
  formData.append('specFile', new Blob([docBuffer], { type: primaryDoc.mimeType }), primaryDoc.fileName)
  formData.append('runs', String(runCount))

  const awrHeaders = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })

  // Submit to platform endpoint (FR-063)
  const submitResponse = await fetch(`${AWR_PLATFORM_API_ENDPOINT}/assess/batch`, {
    method: 'POST', headers: awrHeaders, body: formData,
  })

  if (!submitResponse.ok) {
    const errorText = await submitResponse.text().catch(() => submitResponse.statusText)
    throw new Error(`Platform API submission error (${submitResponse.status}): ${errorText}`)
  }

  const submission = await submitResponse.json() as {
    submissionId: string; status: string; estimatedCompletionSeconds?: number; pollUrl?: string
  }

  // Poll for results with exponential backoff
  const pollUrl = submission.pollUrl || `/assess/batch/${submission.submissionId}/status`
  const maxPollDuration = 15 * 60 * 1000
  let pollDelay = Math.max(5000, ((submission.estimatedCompletionSeconds || 30) / 2) * 1000)
  const pollStart = Date.now()

  while (Date.now() - pollStart < maxPollDuration) {
    await new Promise(r => setTimeout(r, pollDelay))
    const pollResponse = await fetch(`${AWR_PLATFORM_API_ENDPOINT}${pollUrl}`, { headers: awrHeaders })
    if (!pollResponse.ok) throw new Error(`Platform API poll error (${pollResponse.status})`)

    const pollResult = await pollResponse.json() as any
    if (pollResult.status === 'completed' && pollResult.result) {
      // Parse platform response - same format as sequential combined response
      const result = pollResult.result
      const runs: import('../../src/types/index.js').ScoringRun[] = (result.runs || []).map((r: any, idx: number) => ({
        runId: randomUUID(), applicationId, versionId: job.currentVersion.versionId,
        runIndex: r.runIndex || idx + 1, modelDeploymentId: 'platform-llm',
        promptVersionId, overallScore: r.composite_score || 0, subScores: {},
        mustHaveResult: r.eligibility_gate || { passed: false, missingCriteria: [], details: {} },
        evidenceCitations: [], rationale: r.notes || '',
        improvementRecommendations: r.improvement_recommendations || [],
        createdAt: new Date().toISOString(), durationMs: Date.now() - pollStart,
        tokenUsage: undefined, status: 'Success' as const,
      }))

      const aggregated = result.aggregated ? {
        finalScore: result.aggregated.final_score || 0,
        variance: result.aggregated.variance || 0,
        confidence: result.aggregated.confidence || 0,
        finalDecision: result.aggregated.final_decision || 'Excluded',
        consolidatedRationale: result.aggregated.consolidated_rationale || '',
        subScoreAverages: result.aggregated.sub_score_averages || {},
      } : undefined

      return { runs, aggregated }
    }

    if (pollResult.status === 'failed') {
      throw new Error(`Platform scoring failed: ${pollResult.error || 'Unknown error'}`)
    }

    pollDelay = Math.min(pollDelay * 2, 30000)
  }

  throw new Error('Platform scoring timed out after 15 minutes')
}

export function createPipelineOrchestrator(storage: StorageProvider) {
  const audit = createAuditService(storage)

  return {
    async processApplication(applicationId: string, jobId: string, promptVersionIdOverride?: string): Promise<void> {
      const correlationId = randomUUID()

      try {
        const jobs = await getArray<Job>(storage, JOBS)
        const job = jobs.find(j => j.jobId === jobId)
        const config = job?.currentVersion
        const runCount = config?.runsPerApplication || 3

        let promptVersionId: string
        if (promptVersionIdOverride) {
          promptVersionId = promptVersionIdOverride
        } else {
          const approvedId = await getProductionApprovedPromptId(storage, jobId)
          if (!approvedId) {
            throw new Error('No production-approved prompt exists for this job. Approve a prompt before scoring (FR-032).')
          }
          promptVersionId = approvedId
        }

        await audit.appendEvent('system', 'pipeline.scoring.started', 'Application', applicationId, {
          jobId, runCount, promptVersionId, scoringMode: resolvedScoringMode,
        }, correlationId)

        // T172/T176/T179: Route scoring based on mode
        // FR-065: Test scoring (promptVersionIdOverride) always uses sequential mode
        let scoringResult: ScoringResult
        const useSequential = promptVersionIdOverride || resolvedScoringMode === 'sequential'

        if (useSequential) {
          // T176: Sequential mode - single engine call with runs param via AWR_SEQ_API_ENDPOINT
          scoringResult = await withRetry(() =>
            runScoring(storage, applicationId, jobId, runCount, promptVersionId, AWR_SEQ_API_ENDPOINT || undefined)
          )
        } else {
          // T179: Platform mode - async submission + polling via AWR_PLATFORM_API_ENDPOINT
          scoringResult = await withRetry(() =>
            scorePlatformMode(storage, applicationId, jobId, runCount, promptVersionId)
          )
        }

        await audit.appendEvent('system', 'pipeline.scoring.completed', 'Application', applicationId, {
          jobId, runsReturned: scoringResult.runs.length,
        }, correlationId)

        // T172: Store engine-provided aggregated result directly (FR-011, FR-059)
        const varianceThreshold = config?.varianceThreshold || 15
        const longlistThreshold = config?.longlistThreshold || 60
        let finalScore: number, variance: number, confidence: number
        let finalDecision: 'Eligible' | 'Excluded' | 'NeedsManualReview'
        let rationaleText: string, recommendationsText: string
        let finalSubScores: Record<string, number>

        if (scoringResult.aggregated) {
          finalScore = scoringResult.aggregated.finalScore
          variance = scoringResult.aggregated.variance
          confidence = scoringResult.aggregated.confidence
          finalSubScores = scoringResult.aggregated.subScoreAverages
          if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
          else if (scoringResult.aggregated.finalDecision === 'NeedsManualReview') finalDecision = 'NeedsManualReview'
          else if (finalScore >= longlistThreshold) finalDecision = 'Eligible'
          else finalDecision = 'Excluded'
          rationaleText = scoringResult.aggregated.consolidatedRationale
          recommendationsText = scoringResult.runs[0]?.improvementRecommendations?.join('; ') || ''
        } else {
          // Fallback: local aggregation for single-run or backward-compatible responses
          const scores = scoringResult.runs.map(r => r.overallScore)
          const sorted = [...scores].sort((a, b) => a - b)
          const mid = Math.floor(sorted.length / 2)
          finalScore = sorted.length % 2 !== 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
          variance = Math.sqrt(scores.reduce((sum, s) => Math.pow(s - finalScore, 2) + sum, 0) / scores.length)
          confidence = 1 - (variance / 100)
          finalSubScores = {}
          if (config?.rubric) {
            for (const cat of config.rubric) {
              const catScores = scoringResult.runs.map(r => r.subScores[cat.name] || 0)
              finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
            }
          }
          if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
          else if (finalScore >= longlistThreshold) finalDecision = 'Eligible'
          else finalDecision = 'Excluded'
          rationaleText = `Aggregated ${scoringResult.runs.length} scoring runs. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}.`
          recommendationsText = scoringResult.runs[0]?.improvementRecommendations?.join('; ') || ''
        }

        const result: AggregatedResult = {
          resultId: randomUUID(), applicationId, versionId: config?.versionId || 'unknown',
          finalScore, finalSubScores, confidence, variance, finalDecision,
          rationaleText, recommendationsText, allRuns: scoringResult.runs,
          createdAt: new Date().toISOString(),
        }
        await storage.set(appResultKey(applicationId), result)

        // Update application status
        const updatedApps = await getArray<Application>(storage, jobApplicationsKey(jobId))
        const idx = updatedApps.findIndex(a => a.applicationId === applicationId)
        if (idx !== -1) {
          updatedApps[idx] = {
            ...updatedApps[idx],
            status: finalDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed',
            finalScore, finalDecision, variance, flagged: finalDecision === 'NeedsManualReview',
          }
          await setArray(storage, jobApplicationsKey(jobId), updatedApps)
        }

        await audit.appendEvent('system', 'pipeline.completed', 'Application', applicationId, {
          jobId, finalScore, finalDecision, variance, scoringMode: resolvedScoringMode,
        }, correlationId)

      } catch (err) {
        console.error(`Pipeline failed for application ${applicationId}:`, err)
        const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
        const updatedApps = apps.map(a =>
          a.applicationId === applicationId
            ? { ...a, status: 'ScoringFailed' as Application['status'] }
            : a
        )
        await setArray(storage, jobApplicationsKey(jobId), updatedApps)

        const dlqItem: DLQItem = {
          itemId: randomUUID(), applicationId, jobId, failureType: 'Scoring',
          failureReason: err instanceof Error ? err.message : String(err),
          attemptCount: MAX_RETRIES, firstFailedAt: new Date().toISOString(),
          lastAttemptedAt: new Date().toISOString(), canRetry: true,
        }
        await pushToArray(storage, DLQ, dlqItem)

        await audit.appendEvent('system', 'pipeline.failed', 'Application', applicationId, {
          jobId, failureType: 'Scoring', error: dlqItem.failureReason,
        }, correlationId)
      }
    },
  }
}