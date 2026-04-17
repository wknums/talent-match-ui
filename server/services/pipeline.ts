import { randomUUID } from 'node:crypto'
import { applicationRepo, jobRepo, promptRepo, dlqRepo } from '../storage/repos/index.js'
import { auditService } from './audit.js'
import { runScoring } from '../workers/scoring.js'
import type { ScoringResult } from '../workers/scoring.js'
import { buildScoringRunFromParsedResponse, interpretAggregatedResult } from '../workers/scoring.js'
import { findBestRubricMatch } from '../workers/aggregation.js'
import { getProductionApprovedPromptId } from './prompt-helpers.js'
import type { Application, DLQItem, AggregatedResult } from '../../src/types/index.js'

const MAX_RETRIES = 3
const BACKOFF_MS = 1000

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''
const AWR_PLATFORM_API_ENDPOINT = process.env.AWR_PLATFORM_API_ENDPOINT || ''
const AWR_MAX_PARALLEL = Math.max(1, parseInt(process.env.AWR_MAX_PARALLEL || '1', 10) || 1)

// T174: Scoring mode detection (FR-061, FR-066)
export type ScoringMode = 'sequential' | 'platform'

export function detectScoringMode(): ScoringMode {
  if (!AWR_PLATFORM_API_ENDPOINT || AWR_PLATFORM_API_ENDPOINT === AWR_SEQ_API_ENDPOINT) {
    return 'sequential'
  }
  return 'platform'
}

const resolvedScoringMode = detectScoringMode()
console.log(`[Pipeline] Scoring mode resolved: ${resolvedScoringMode} (SEQ=${AWR_SEQ_API_ENDPOINT || '(unset)'}, PLATFORM=${AWR_PLATFORM_API_ENDPOINT || '(unset)'}, MAX_PARALLEL=${AWR_MAX_PARALLEL})`)

/**
 * Remap engine/LLM sub-score keys to rubric category names via fuzzy matching.
 */
function remapSubScoresToRubric(
  subScores: Record<string, number>,
  rubric?: Array<{ name: string; weight: number; description?: string }>,
): Record<string, number> {
  if (!rubric?.length || !Object.keys(subScores).length) return subScores
  const remapped: Record<string, number> = {}
  const llmKeys = Object.keys(subScores)
  for (const cat of rubric) {
    // Exact match first
    if (cat.name in subScores) {
      remapped[cat.name] = subScores[cat.name]
      continue
    }
    // Fuzzy match: find the LLM key that best matches this rubric category
    const match = findBestRubricMatch(cat.name, llmKeys)
    if (match) {
      remapped[cat.name] = subScores[match]
    }
  }
  return remapped
}

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
  applicationId: string,
  jobId: string,
  runCount: number,
  promptVersionId: string,
): Promise<ScoringResult> {
  const { getAwrAuthHeaders } = await import('../services/awr-auth.js')

  const job = await jobRepo.getById(jobId)
  if (!job) throw new Error(`Job ${jobId} not found`)

  const prompt = await promptRepo.getById(promptVersionId)
  if (!prompt) throw new Error(`Prompt ${promptVersionId} not found`)

  const documents = await applicationRepo.getDocuments(applicationId)
  if (!documents.length) throw new Error(`No documents for application ${applicationId}`)
  const primaryDoc = documents[0]
  const rawContent = await applicationRepo.getBlob(primaryDoc.documentId)
  if (!rawContent) throw new Error(`No document blob for application ${applicationId}`)

  const jobDescriptionText = job.jobDescription || job.title
  const resolvedPrompt = prompt.promptText.replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)

  const isBase64 = /^[A-Za-z0-9+/\r\n]+=*$/.test(rawContent.slice(0, 256)) && !rawContent.startsWith('%PDF')
  const docBuffer = isBase64 ? Buffer.from(rawContent, 'base64') : Buffer.from(rawContent)
  const formData = new FormData()
  formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
  formData.append('cvFiles[]', new Blob([docBuffer], { type: primaryDoc.mimeType }), primaryDoc.fileName)
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
      const durationMs = Date.now() - pollStart
      const runs: import('../../src/types/index.js').ScoringRun[] = Array.isArray(result.runs)
        ? result.runs.map((run: any, idx: number) => buildScoringRunFromParsedResponse({
            applicationId,
            versionId: job.currentVersion.versionId,
            runIndex: run?.runIndex || idx + 1,
            promptVersionId,
            durationMs,
            rawParsedResponse: (run && typeof run === 'object') ? run as Record<string, unknown> : { value: run },
            rawResponseText: JSON.stringify(run ?? {}),
            modelDeploymentId: 'platform-llm',
          }))
        : []

      const aggregated = result.aggregated && typeof result.aggregated === 'object'
        ? interpretAggregatedResult(result.aggregated as Record<string, unknown>)
        : undefined

      return { runs, aggregated }
    }

    if (pollResult.status === 'failed') {
      throw new Error(`Platform scoring failed: ${pollResult.error || 'Unknown error'}`)
    }

    pollDelay = Math.min(pollDelay * 2, 30000)
  }

  throw new Error('Platform scoring timed out after 15 minutes')
}

export function createPipelineOrchestrator() {
  const audit = auditService

  return {
    async processApplication(applicationId: string, jobId: string, promptVersionIdOverride?: string): Promise<void> {
      const correlationId = randomUUID()

      try {
        const job = await jobRepo.getById(jobId)
        const config = job?.currentVersion
        const runCount = config?.runsPerApplication || 3

        let promptVersionId: string
        if (promptVersionIdOverride) {
          promptVersionId = promptVersionIdOverride
        } else {
          const approvedId = await getProductionApprovedPromptId(jobId)
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
            runScoring(applicationId, jobId, runCount, promptVersionId, AWR_SEQ_API_ENDPOINT || undefined)
          )
        } else {
          // T179: Platform mode - async submission + polling via AWR_PLATFORM_API_ENDPOINT
          scoringResult = await withRetry(() =>
            scorePlatformMode(applicationId, jobId, runCount, promptVersionId)
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

        // Check if any run failed the must-have eligibility gate
        const anyGateFailed = scoringResult.runs.some(r => r.mustHaveResult && r.mustHaveResult.passed === false)

        if (scoringResult.aggregated) {
          finalScore = scoringResult.aggregated.finalScore
          variance = scoringResult.aggregated.variance
          confidence = scoringResult.aggregated.confidence
          // Remap engine sub-score keys to rubric category names via fuzzy matching
          finalSubScores = remapSubScoresToRubric(scoringResult.aggregated.subScoreAverages, config?.rubric)
          if (anyGateFailed) finalDecision = 'Excluded'
          else if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
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
              const catScores = scoringResult.runs.map(r => {
                // Exact match first, then fuzzy match for LLM-derived key names
                if (cat.name in r.subScores) return r.subScores[cat.name]
                const match = findBestRubricMatch(cat.name, Object.keys(r.subScores))
                return match ? r.subScores[match] : 0
              })
              finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
            }
          }
          if (anyGateFailed) finalDecision = 'Excluded'
          else if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
          else if (finalScore >= longlistThreshold) finalDecision = 'Eligible'
          else finalDecision = 'Excluded'
          rationaleText = anyGateFailed
            ? `Excluded: eligibility gate failed. ${scoringResult.runs.filter(r => r.mustHaveResult && !r.mustHaveResult.passed).flatMap(r => r.mustHaveResult.missingCriteria).join('; ')}. Score: ${finalScore.toFixed(1)} (${scoringResult.runs.length} runs).`
            : `Aggregated ${scoringResult.runs.length} scoring runs. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}.`
          recommendationsText = scoringResult.runs[0]?.improvementRecommendations?.join('; ') || ''
        }

        const result: AggregatedResult = {
          resultId: randomUUID(), applicationId, versionId: config?.versionId || 'unknown',
          finalScore, finalSubScores, confidence, variance, finalDecision,
          rationaleText, recommendationsText, allRuns: scoringResult.runs,
          createdAt: new Date().toISOString(),
        }
        await applicationRepo.setAggregatedResult(result)

        // Update application status
        const newStatus = finalDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed'
        await applicationRepo.updateStatus(applicationId, newStatus, {
          finalScore, finalDecision, variance, flagged: finalDecision === 'NeedsManualReview',
        })

        await audit.appendEvent('system', 'pipeline.completed', 'Application', applicationId, {
          jobId, finalScore, finalDecision, variance, scoringMode: resolvedScoringMode,
        }, correlationId)

      } catch (err) {
        console.error(`Pipeline failed for application ${applicationId}:`, err)
        await applicationRepo.updateStatus(applicationId, 'ScoringFailed')

        const dlqItem: DLQItem = {
          itemId: randomUUID(), applicationId, jobId, failureType: 'Scoring',
          failureReason: err instanceof Error ? err.message : String(err),
          attemptCount: MAX_RETRIES, firstFailedAt: new Date().toISOString(),
          lastAttemptedAt: new Date().toISOString(), canRetry: true,
        }
        await dlqRepo.add(dlqItem)

        await audit.appendEvent('system', 'pipeline.failed', 'Application', applicationId, {
          jobId, failureType: 'Scoring', error: dlqItem.failureReason,
        }, correlationId)
      }
    },

    async processApplicationsBatch(
      applicationIds: string[],
      jobId: string,
      promptVersionIdOverride?: string,
    ): Promise<PromiseSettledResult<void>[]> {
      const results: PromiseSettledResult<void>[] = new Array(applicationIds.length)
      let nextIndex = 0

      const worker = async () => {
        while (nextIndex < applicationIds.length) {
          const idx = nextIndex++
          try {
            await this.processApplication(applicationIds[idx], jobId, promptVersionIdOverride)
            results[idx] = { status: 'fulfilled', value: undefined }
          } catch (reason) {
            results[idx] = { status: 'rejected', reason }
          }
        }
      }

      const workerCount = Math.min(AWR_MAX_PARALLEL, applicationIds.length)
      console.log(`[Pipeline] Processing ${applicationIds.length} applications with ${workerCount} concurrent workers`)
      await Promise.all(Array.from({ length: workerCount }, () => worker()))
      return results
    },
  }
}