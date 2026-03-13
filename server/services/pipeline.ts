import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { DLQ, JOBS, jobApplicationsKey } from '../storage/kv-keys.js'
import { getArray, setArray, pushToArray } from '../storage/kv-helpers.js'
import { createAuditService } from './audit.js'
import { runExtraction } from '../workers/extraction.js'
import { runScoring } from '../workers/scoring.js'
import { runAggregation } from '../workers/aggregation.js'
import { getProductionApprovedPromptId } from './prompt-helpers.js'
import type { Application, Job, DLQItem } from '../../src/types/index.js'

const MAX_RETRIES = 3
const BACKOFF_MS = 1000

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

export function createPipelineOrchestrator(storage: StorageProvider) {
  const audit = createAuditService(storage)

  return {
    async processApplication(applicationId: string, jobId: string, promptVersionIdOverride?: string): Promise<void> {
      const correlationId = randomUUID()

      try {
        // Step 1: Extraction
        await audit.appendEvent('system', 'pipeline.extraction.started', 'Application', applicationId, { jobId }, correlationId)
        await withRetry(() => runExtraction(storage, applicationId, jobId))
        await audit.appendEvent('system', 'pipeline.extraction.completed', 'Application', applicationId, { jobId }, correlationId)

        // Step 2: Scoring
        const jobs = await getArray<Job>(storage, JOBS)
        const job = jobs.find(j => j.jobId === jobId)
        const runCount = job?.currentVersion?.runsPerApplication || 3

        // When promptVersionIdOverride is provided (test scoring), bypass the production-approved gate.
        // Otherwise enforce the production-approved prompt gate (FR-032/FR-040).
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

        await audit.appendEvent('system', 'pipeline.scoring.started', 'Application', applicationId, { jobId, runCount, promptVersionId }, correlationId)
        await withRetry(() => runScoring(storage, applicationId, jobId, runCount, promptVersionId))
        await audit.appendEvent('system', 'pipeline.scoring.completed', 'Application', applicationId, { jobId }, correlationId)

        // Step 3: Aggregation
        await audit.appendEvent('system', 'pipeline.aggregation.started', 'Application', applicationId, { jobId }, correlationId)
        const result = await withRetry(() => runAggregation(storage, applicationId, jobId))
        await audit.appendEvent('system', 'pipeline.aggregation.completed', 'Application', applicationId, {
          jobId,
          finalScore: result.finalScore,
          finalDecision: result.finalDecision,
        }, correlationId)

      } catch (err) {
        console.error(`Pipeline failed for application ${applicationId}:`, err)

        // Determine failure type from application status
        const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
        const app = apps.find(a => a.applicationId === applicationId)
        let failureType: 'Extraction' | 'Scoring' | 'Aggregation' = 'Extraction'
        if (app?.status === 'Scoring') failureType = 'Scoring'
        if (app?.status === 'Aggregating') failureType = 'Aggregation'

        // Update application status to failed
        const updatedApps = apps.map(a =>
          a.applicationId === applicationId
            ? { ...a, status: (failureType === 'Extraction' ? 'ExtractionFailed' : 'ScoringFailed') as Application['status'] }
            : a
        )
        await setArray(storage, jobApplicationsKey(jobId), updatedApps)

        // Add to DLQ
        const dlqItem: DLQItem = {
          itemId: randomUUID(),
          applicationId,
          jobId,
          failureType,
          failureReason: err instanceof Error ? err.message : String(err),
          attemptCount: MAX_RETRIES,
          firstFailedAt: new Date().toISOString(),
          lastAttemptedAt: new Date().toISOString(),
          canRetry: true,
        }
        await pushToArray(storage, DLQ, dlqItem)

        await audit.appendEvent('system', 'pipeline.failed', 'Application', applicationId, {
          jobId, failureType, error: dlqItem.failureReason,
        }, correlationId)
      }
    },
  }
}
