// Platform-mode reconciler: a single in-process loop that processes
// ScoringBatches rows. See specs/008-platform-mode-shift/platform-contract.md §6.
//
// Lifecycle:
//   • Started from server/index.ts only when detectScoringMode() === 'platform'.
//   • Each tick: pull up to MAX_INFLIGHT due rows, race for the row-lease, then
//     dispatch by status. Crash-safe via the lease expiry.
//
// Sequential mode never starts this worker, so the platform-submitter (which
// loads the blob SDK) is never imported in that deployment.

import { hostname } from 'node:os'
import { scoringBatchRepo } from '../storage/repos/index.js'
import type { ScoringBatch } from '../storage/repos/scoring-batch-repo.js'
import { submitBatch, pollBatch, cancelSubmission } from '../services/platform-submitter.js'
import { detectScoringMode } from '../services/pipeline.js'

const TICK_MS = Math.max(2000, Number(process.env.AWR_PLATFORM_RECONCILE_INTERVAL_MS || 15000))
const MAX_INFLIGHT = Math.max(1, Number(process.env.AWR_PLATFORM_MAX_INFLIGHT_PER_TICK || 50))
const LEASE_SECONDS = Math.max(30, Number(process.env.AWR_PLATFORM_LEASE_SECONDS || 60))
const BACKOFF_BASE_MS = 5000
const BACKOFF_MAX_MS = 60_000

let timer: NodeJS.Timeout | null = null
let running = false
const owner = `${hostname()}-${process.pid}`

function nextBackoff(attempt: number): Date {
  const ms = Math.min(BACKOFF_MAX_MS, BACKOFF_BASE_MS * Math.pow(2, Math.max(0, attempt)))
  return new Date(Date.now() + ms)
}

async function handleOne(batch: ScoringBatch): Promise<void> {
  // Cancellation handling first — applies regardless of status.
  if (batch.cancelRequested && batch.status === 'submitted' && batch.submissionId) {
    await cancelSubmission(batch)
    // Let the next poll observe the cancellation; if platform returns 409
    // (already completed) we still process the result.
    await scoringBatchRepo.setNextPollAt(batch.batchId, new Date(Date.now() + 5000))
    return
  }
  if (batch.cancelRequested && batch.status === 'pending') {
    await scoringBatchRepo.markCancelled(batch.batchId)
    await scoringBatchRepo.applyTransition(batch.jobId, 'pending', null, { failed: batch.applicationIds.length })
    return
  }

  if (batch.status === 'pending') {
    const outcome = await submitBatch(batch)
    if (outcome.status === 'submitted' || outcome.status === 'idempotent-hit') {
      return // markSubmitted already happened inside submitBatch
    }
    if (outcome.status === 'cancelled') return
    if (outcome.status === 'transient') {
      const next = outcome.retryAfterMs ? new Date(Date.now() + outcome.retryAfterMs) : nextBackoff(batch.attempt)
      await scoringBatchRepo.incrementAttempt(batch.batchId, outcome.error || 'transient', next)
      return
    }
    // permanent-failure: mark batch + apps failed
    await scoringBatchRepo.markFailed(batch.batchId, outcome.error || 'submission failed')
    await scoringBatchRepo.applyTransition(batch.jobId, 'pending', 'failed', { failed: batch.applicationIds.length })
    return
  }

  if (batch.status === 'submitted') {
    const outcome = await pollBatch(batch)
    if (outcome.status === 'still-running') {
      const next = outcome.retryAfterMs ? new Date(Date.now() + outcome.retryAfterMs) : nextBackoff(0)
      await scoringBatchRepo.setNextPollAt(batch.batchId, next)
      return
    }
    // completed / failed / cancelled handled inside pollBatch
    return
  }

  // Any other status reaching here (cancelling) — just clear the lease.
}

async function tick(): Promise<void> {
  if (running) return
  running = true
  try {
    const candidates = await scoringBatchRepo.findDueCandidates(MAX_INFLIGHT)
    for (const candidate of candidates) {
      const won = await scoringBatchRepo.acquireLease(candidate.batchId, owner, LEASE_SECONDS)
      if (!won) continue
      try {
        const fresh = await scoringBatchRepo.getById(candidate.batchId)
        if (fresh) await handleOne(fresh)
      } catch (err) {
        console.error(`[Reconciler] Error handling batch ${candidate.batchId}:`, err)
      } finally {
        await scoringBatchRepo.releaseLease(candidate.batchId).catch(() => { /* noop */ })
      }
    }
  } catch (err) {
    console.error('[Reconciler] Tick failed:', err)
  } finally {
    running = false
  }
}

export function startPlatformReconciler(): void {
  if (detectScoringMode() !== 'platform') {
    return
  }
  if (timer) return
  console.log(`[Reconciler] Platform reconciler started (interval=${TICK_MS}ms, maxInflight=${MAX_INFLIGHT}, owner=${owner})`)
  timer = setInterval(() => { tick().catch(() => { /* swallow */ }) }, TICK_MS)
  // Trigger first tick immediately so newly-enqueued batches do not wait a full interval.
  tick().catch(() => { /* swallow */ })
}

export function stopPlatformReconciler(): void {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}
