import { randomUUID } from 'node:crypto'
import { applicationRepo, jobRepo, promptRepo, dlqRepo, scoringBatchRepo } from '../storage/repos/index.js'
import { auditService } from './audit.js'
import { runScoring } from '../workers/scoring.js'
import type { ScoringResult } from '../workers/scoring.js'
import { buildScoringRunFromParsedResponse, interpretAggregatedResult } from '../workers/scoring.js'
import { findBestRubricMatch } from '../workers/aggregation.js'
import { getProductionApprovedPromptId } from './prompt-helpers.js'
import { createAwrTimeoutSignal } from './awr-timeout.js'
import type { Application, DLQItem, AggregatedResult } from '../../src/types/index.js'

const MAX_RETRIES = 3
const BACKOFF_MS = 1000

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''
const AWR_PLATFORM_API_ENDPOINT = process.env.AWR_PLATFORM_API_ENDPOINT || ''
const AWR_MAX_PARALLEL = Math.max(1, parseInt(process.env.AWR_MAX_PARALLEL || '1', 10) || 1)
const AWR_PLATFORM_BATCH_SIZE = Math.max(1, parseInt(process.env.AWR_PLATFORM_BATCH_SIZE || '2', 10) || 2)

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

// T179: Platform mode submission is handled by ScoringBatches + the reconciler
// worker (see server/workers/reconciler.ts and server/services/platform-submitter.ts).
// The inline submit-and-poll function previously lived here; it was removed in
// 008-platform-mode-shift because the client process must NOT block on platform
// scoring. Each application is enqueued into ScoringBatches and finalized
// asynchronously via `finalizeApplicationFromScoringResult` below.

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

        // FR-065: Test scoring (promptVersionIdOverride) always uses sequential mode.
        // FR-066: Platform mode without override delegates to the reconciler by
        // inserting a one-application batch and returning immediately.
        const useSequential = promptVersionIdOverride || resolvedScoringMode === 'sequential'

        if (!useSequential) {
          await audit.appendEvent('system', 'pipeline.platform.enqueued', 'Application', applicationId, {
            jobId, runCount, promptVersionId, source: 'processApplication',
          }, correlationId)
          await scoringBatchRepo.createBatch({
            jobId, promptVersionId, applicationIds: [applicationId], runCount,
          })
          await scoringBatchRepo.initProgress(jobId, 1, 1)
          await applicationRepo.updateStatus(applicationId, 'Scoring')
          return
        }

        await audit.appendEvent('system', 'pipeline.scoring.started', 'Application', applicationId, {
          jobId, runCount, promptVersionId, scoringMode: 'sequential',
        }, correlationId)

        // T176: Sequential mode - single engine call with runs param via AWR_SEQ_API_ENDPOINT
        const scoringResult = await withRetry(() =>
          runScoring(applicationId, jobId, runCount, promptVersionId, AWR_SEQ_API_ENDPOINT || undefined)
        )

        await audit.appendEvent('system', 'pipeline.scoring.completed', 'Application', applicationId, {
          jobId, runsReturned: scoringResult.runs.length,
        }, correlationId)

        await finalizeApplicationFromScoringResult(applicationId, jobId, scoringResult, correlationId, 'sequential')

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
      // Platform mode without test override: enqueue ScoringBatches and return.
      // The reconciler will submit + poll + finalize. The web process does NOT
      // block on platform work.
      if (!promptVersionIdOverride && resolvedScoringMode === 'platform' && applicationIds.length > 0) {
        const job = await jobRepo.getById(jobId)
        const config = job?.currentVersion
        const runCount = config?.runsPerApplication || 3
        const approvedId = await getProductionApprovedPromptId(jobId)
        if (!approvedId) {
          const err = new Error('No production-approved prompt exists for this job. Approve a prompt before scoring (FR-032).')
          return applicationIds.map(() => ({ status: 'rejected' as const, reason: err }))
        }

        const chunks: string[][] = []
        for (let i = 0; i < applicationIds.length; i += AWR_PLATFORM_BATCH_SIZE) {
          chunks.push(applicationIds.slice(i, i + AWR_PLATFORM_BATCH_SIZE))
        }
        await scoringBatchRepo.initProgress(jobId, applicationIds.length, chunks.length)
        for (const appIds of chunks) {
          await scoringBatchRepo.createBatch({
            jobId, promptVersionId: approvedId, applicationIds: appIds, runCount,
          })
        }
        // Move every application to Scoring so the UI reflects the queued state.
        for (const appId of applicationIds) {
          await applicationRepo.updateStatus(appId, 'Scoring')
        }
        console.log(`[Pipeline] Platform mode: enqueued ${chunks.length} batch(es) for ${applicationIds.length} application(s) on job ${jobId}`)
        return applicationIds.map(() => ({ status: 'fulfilled' as const, value: undefined }))
      }

      // Sequential mode (or test/override) - existing in-process worker pool.
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

/**
 * Persist a `ScoringResult` for one application: writes the aggregated row and
 * updates application status. Shared between sequential mode (in `processApplication`)
 * and platform mode (called per-CV by the reconciler).
 *
 * Throws on failure; callers decide whether to push to DLQ / mark `ScoringFailed`.
 */
export async function finalizeApplicationFromScoringResult(
  applicationId: string,
  jobId: string,
  scoringResult: ScoringResult,
  correlationId: string,
  scoringMode: 'sequential' | 'platform',
): Promise<{ finalDecision: 'Eligible' | 'Excluded' | 'NeedsManualReview'; finalScore: number; variance: number }> {
  const audit = auditService
  const job = await jobRepo.getById(jobId)
  const config = job?.currentVersion
  const varianceThreshold = config?.varianceThreshold || 15
  const longlistThreshold = config?.longlistThreshold || 60

  let finalScore: number, variance: number, confidence: number
  let finalDecision: 'Eligible' | 'Excluded' | 'NeedsManualReview'
  let rationaleText: string, recommendationsText: string
  let finalSubScores: Record<string, number>

  const gatePassVotes = scoringResult.runs.filter(r => r.mustHaveResult?.passed === true).length
  const gateFailVotes = scoringResult.runs.filter(r => r.mustHaveResult?.passed === false).length
  const gateFailedByAggregation = gateFailVotes > gatePassVotes
  const hasGateVotes = gatePassVotes + gateFailVotes > 0

  if (scoringResult.aggregated) {
    finalScore = scoringResult.aggregated.finalScore
    variance = scoringResult.aggregated.variance
    confidence = scoringResult.aggregated.confidence
    finalSubScores = remapSubScoresToRubric(scoringResult.aggregated.subScoreAverages, config?.rubric)
    if (gateFailedByAggregation) finalDecision = 'Excluded'
    else if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
    else if (scoringResult.aggregated.finalDecision === 'NeedsManualReview') finalDecision = 'NeedsManualReview'
    else if (finalScore >= longlistThreshold) finalDecision = 'Eligible'
    else finalDecision = 'Excluded'
    rationaleText = scoringResult.aggregated.consolidatedRationale
    recommendationsText = scoringResult.runs[0]?.improvementRecommendations?.join('; ') || ''
  } else {
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
          if (cat.name in r.subScores) return r.subScores[cat.name]
          const match = findBestRubricMatch(cat.name, Object.keys(r.subScores))
          return match ? r.subScores[match] : 0
        })
        finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
      }
    }
    if (gateFailedByAggregation) finalDecision = 'Excluded'
    else if (variance > varianceThreshold) finalDecision = 'NeedsManualReview'
    else if (finalScore >= longlistThreshold) finalDecision = 'Eligible'
    else finalDecision = 'Excluded'
    rationaleText = gateFailedByAggregation
      ? `Excluded: eligibility gate failed by aggregated votes (passed: ${gatePassVotes}, failed: ${gateFailVotes}). Score: ${finalScore.toFixed(1)} (${scoringResult.runs.length} runs).`
      : hasGateVotes
        ? `Aggregated ${scoringResult.runs.length} scoring runs. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}. Eligibility votes: passed ${gatePassVotes}, failed ${gateFailVotes}.`
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

  const newStatus = finalDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed'
  await applicationRepo.updateStatus(applicationId, newStatus, {
    finalScore, finalDecision, variance, flagged: finalDecision === 'NeedsManualReview',
  })

  await audit.appendEvent('system', 'pipeline.completed', 'Application', applicationId, {
    jobId, finalScore, finalDecision, variance, scoringMode,
  }, correlationId)

  return { finalDecision, finalScore, variance }
}

// Re-export for the reconciler.
export { buildScoringRunFromParsedResponse, interpretAggregatedResult }