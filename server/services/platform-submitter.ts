// Platform-mode batch submitter — used exclusively by the reconciler worker.
// Implements §4 of specs/008-platform-mode-shift/platform-contract.md.
//
// Sequential mode never imports this module at runtime: the reconciler is the
// only caller, and the reconciler itself is only started when
// detectScoringMode() === 'platform' (see server/workers/reconciler.ts).

import { Buffer } from 'node:buffer'
import { applicationRepo, jobRepo, promptRepo, scoringBatchRepo } from '../storage/repos/index.js'
import type { ScoringBatch } from '../storage/repos/scoring-batch-repo.js'
import { getBlobStore } from './blob-store.js'
import { getAwrAuthHeaders } from './awr-auth.js'
import { createAwrTimeoutSignal } from './awr-timeout.js'
import { auditService } from './audit.js'
import { finalizeApplicationFromScoringResult, buildScoringRunFromParsedResponse, interpretAggregatedResult } from './pipeline.js'
import type { ScoringRun } from '../../src/types/index.js'

const AWR_PLATFORM_API_ENDPOINT = process.env.AWR_PLATFORM_API_ENDPOINT || ''
const PLATFORM_SUBMIT_TIMEOUT_S = Number(process.env.AWR_PLATFORM_SUBMIT_TIMEOUT_S || 60)
const PLATFORM_POLL_TIMEOUT_S = Number(process.env.AWR_PLATFORM_POLL_TIMEOUT_S || 30)

export interface SubmitOutcome {
  status: 'submitted' | 'idempotent-hit' | 'transient' | 'permanent-failure' | 'cancelled'
  submissionId?: string
  pollUrl?: string
  retryAfterMs?: number
  error?: string
}

export interface PollOutcome {
  status: 'still-running' | 'completed' | 'failed' | 'cancelled'
  retryAfterMs?: number
  error?: string
}

function platformUrl(path: string): string {
  if (!AWR_PLATFORM_API_ENDPOINT) throw new Error('AWR_PLATFORM_API_ENDPOINT is not set')
  if (path.startsWith('http://') || path.startsWith('https://')) return path
  return `${AWR_PLATFORM_API_ENDPOINT}${path.startsWith('/') ? '' : '/'}${path}`
}

function decodeBlobBytes(raw: string): Buffer {
  // Storage layer returns either base64-encoded bytes or raw text. Heuristic
  // matches the previous in-pipeline check; safe because PDFs start with "%PDF".
  const isBase64 = /^[A-Za-z0-9+/\r\n]+=*$/.test(raw.slice(0, 256)) && !raw.startsWith('%PDF')
  return isBase64 ? Buffer.from(raw, 'base64') : Buffer.from(raw)
}

function defaultPollDelayMs(): number {
  return Math.max(5000, Number(process.env.AWR_PLATFORM_DEFAULT_POLL_DELAY_MS || 10000))
}

/**
 * Submit a batch to POST /assess/batch. On success the batch row is updated
 * to status='submitted' with submissionId+pollUrl+nextPollAt. On transient
 * failure the caller schedules a retry via NextPollAt.
 */
export async function submitBatch(batch: ScoringBatch): Promise<SubmitOutcome> {
  if (!AWR_PLATFORM_API_ENDPOINT) {
    return { status: 'permanent-failure', error: 'AWR_PLATFORM_API_ENDPOINT is not set' }
  }
  if (batch.cancelRequested) {
    await scoringBatchRepo.markCancelled(batch.batchId)
    await scoringBatchRepo.applyTransition(batch.jobId, 'pending', null, { failed: batch.applicationIds.length })
    return { status: 'cancelled' }
  }

  const blobStore = getBlobStore()
  if (!blobStore.isRemote) {
    return {
      status: 'permanent-failure',
      error: 'Platform mode requires AWR_BLOB_STORAGE_ACCOUNT to upload CVs by reference (contract §2.1).',
    }
  }

  const job = await jobRepo.getById(batch.jobId)
  if (!job) return { status: 'permanent-failure', error: `Job ${batch.jobId} not found` }
  const prompt = await promptRepo.getById(batch.promptVersionId)
  if (!prompt) return { status: 'permanent-failure', error: `Prompt ${batch.promptVersionId} not found` }

  const jobDescriptionText = job.jobDescription || job.title
  const resolvedPrompt = prompt.promptText.replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)

  const cvs: Array<Record<string, string>> = []
  for (const applicationId of batch.applicationIds) {
    const documents = await applicationRepo.getDocuments(applicationId)
    if (!documents.length) {
      return { status: 'permanent-failure', error: `No documents for application ${applicationId}` }
    }
    const doc = documents[0]
    const raw = await applicationRepo.getBlob(doc.documentId)
    if (!raw) return { status: 'permanent-failure', error: `No document blob for application ${applicationId}` }
    const bytes = decodeBlobBytes(raw)

    const upload = await blobStore.put({
      jobId: batch.jobId,
      applicationId,
      documentId: doc.documentId,
      fileName: doc.fileName,
      mimeType: doc.mimeType,
      bytes,
    })
    if (!upload.blobUri) {
      return { status: 'permanent-failure', error: 'Blob store did not return a blobUri in platform mode' }
    }
    await applicationRepo.setDocumentBlobReference(doc.documentId, upload.blobUri, upload.sha256)
    cvs.push({
      applicationId,
      documentId: doc.documentId,
      fileName: doc.fileName,
      mimeType: doc.mimeType,
      blobUri: upload.blobUri,
      sha256: upload.sha256,
    })
  }

  const body = {
    batchId: batch.batchId,
    jobId: batch.jobId,
    promptVersionId: batch.promptVersionId,
    runCount: batch.runCount,
    prompt: { kind: 'inline' as const, text: resolvedPrompt },
    cvs,
    callbackUrl: null,
  }

  const headers: Record<string, string> = {
    ...(await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })),
    'Content-Type': 'application/json',
    'Idempotency-Key': batch.batchId,
  }

  const submitTimeout = createAwrTimeoutSignal(PLATFORM_SUBMIT_TIMEOUT_S * 1000)
  let response: Response
  try {
    response = await fetch(platformUrl('/assess/batch'), {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      signal: submitTimeout.signal,
    })
  } catch (err) {
    return { status: 'transient', error: err instanceof Error ? err.message : String(err) }
  } finally {
    submitTimeout.dispose()
  }

  if (response.status === 429) {
    const retryAfter = Number(response.headers.get('retry-after') ?? 0)
    return { status: 'transient', retryAfterMs: Math.max(retryAfter * 1000, 5000) }
  }
  if (response.status >= 500) {
    return { status: 'transient', error: `Platform ${response.status}` }
  }
  if (response.status === 400 || response.status === 401 || response.status === 403 || response.status === 409) {
    const errText = await response.text().catch(() => response.statusText)
    return { status: 'permanent-failure', error: `Platform ${response.status}: ${errText}` }
  }
  if (!response.ok && response.status !== 200 && response.status !== 202) {
    const errText = await response.text().catch(() => response.statusText)
    return { status: 'transient', error: `Platform ${response.status}: ${errText}` }
  }

  const submission = await response.json() as {
    submissionId: string
    status: string
    pollUrl?: string
    estimatedCompletionSeconds?: number
  }
  const pollUrl = submission.pollUrl || `/assess/batch/${submission.submissionId}/status`
  const nextDelayMs = Math.max(
    5000,
    ((submission.estimatedCompletionSeconds || 30) / 2) * 1000,
  )
  const nextPollAt = new Date(Date.now() + nextDelayMs)
  await scoringBatchRepo.markSubmitted(batch.batchId, submission.submissionId, pollUrl, nextPollAt)
  await scoringBatchRepo.applyTransition(batch.jobId, 'pending', 'submitted')
  await auditService.appendEvent('system', 'pipeline.platform.submitted', 'Job', batch.jobId, {
    batchId: batch.batchId, submissionId: submission.submissionId, cvs: cvs.length,
  }, batch.batchId)

  return {
    status: response.status === 200 ? 'idempotent-hit' : 'submitted',
    submissionId: submission.submissionId,
    pollUrl,
  }
}

interface PerCvResult {
  applicationId: string
  runs?: any[]
  aggregated?: Record<string, unknown>
}

function runsFromCvBlock(applicationId: string, versionId: string, promptVersionId: string, durationMs: number, cv: PerCvResult): ScoringRun[] {
  if (!Array.isArray(cv.runs)) return []
  return cv.runs.map((run: any, idx: number) => buildScoringRunFromParsedResponse({
    applicationId,
    versionId,
    runIndex: run?.runIndex || idx + 1,
    promptVersionId,
    durationMs,
    rawParsedResponse: (run && typeof run === 'object') ? run as Record<string, unknown> : { value: run },
    rawResponseText: JSON.stringify(run ?? {}),
    modelDeploymentId: 'platform-llm',
  }))
}

/**
 * Poll a submitted batch and process terminal transitions.
 */
export async function pollBatch(batch: ScoringBatch): Promise<PollOutcome> {
  if (!batch.pollUrl) return { status: 'failed', error: 'Batch has no pollUrl' }

  const headers = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })
  const t = createAwrTimeoutSignal(PLATFORM_POLL_TIMEOUT_S * 1000)
  let response: Response
  try {
    response = await fetch(platformUrl(batch.pollUrl), { headers, signal: t.signal })
  } catch (err) {
    return { status: 'still-running', retryAfterMs: defaultPollDelayMs(), error: err instanceof Error ? err.message : String(err) }
  } finally {
    t.dispose()
  }

  if (response.status === 429 || response.status >= 500) {
    const retryAfter = Number(response.headers.get('retry-after') ?? 0)
    return { status: 'still-running', retryAfterMs: Math.max(retryAfter * 1000, defaultPollDelayMs()) }
  }
  if (!response.ok) {
    return { status: 'failed', error: `Poll ${response.status}` }
  }

  const payload = await response.json() as {
    submissionId: string
    status: 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'
    progress?: { cvsCompleted?: number; cvsTotal?: number }
    estimatedCompletionSeconds?: number
    retryAfterSeconds?: number
    result?: { cvs?: PerCvResult[] }
    error?: { code?: string; message?: string }
  }

  if (payload.status === 'queued' || payload.status === 'running') {
    const retryAfterMs = (payload.retryAfterSeconds && payload.retryAfterSeconds > 0)
      ? payload.retryAfterSeconds * 1000
      : defaultPollDelayMs()
    return { status: 'still-running', retryAfterMs }
  }

  if (payload.status === 'cancelled') {
    await scoringBatchRepo.markCancelled(batch.batchId)
    await scoringBatchRepo.applyTransition(batch.jobId, 'submitted', null, { failed: batch.applicationIds.length })
    return { status: 'cancelled' }
  }

  if (payload.status === 'failed') {
    const msg = payload.error?.message || payload.error?.code || 'Unknown platform failure'
    await scoringBatchRepo.markFailed(batch.batchId, msg)
    await scoringBatchRepo.applyTransition(batch.jobId, 'submitted', 'failed', { failed: batch.applicationIds.length })
    // Best-effort: mark every application in the batch ScoringFailed.
    for (const appId of batch.applicationIds) {
      try { await applicationRepo.updateStatus(appId, 'ScoringFailed') } catch { /* noop */ }
    }
    return { status: 'failed', error: msg }
  }

  // status === 'completed'
  const cvs = Array.isArray(payload.result?.cvs) ? payload.result!.cvs! : []
  const cvById = new Map<string, PerCvResult>(cvs.map(c => [c.applicationId, c]))
  const job = await jobRepo.getById(batch.jobId)
  const versionId = job?.currentVersion?.versionId || 'unknown'

  await scoringBatchRepo.markCompleted(batch.batchId, JSON.stringify(payload))

  let appsCompleted = 0
  let appsFailed = 0
  const durationMs = batch.submittedAt ? Date.now() - new Date(batch.submittedAt).getTime() : 0
  for (const applicationId of batch.applicationIds) {
    const cv = cvById.get(applicationId)
    if (!cv) {
      appsFailed++
      try { await applicationRepo.updateStatus(applicationId, 'ScoringFailed') } catch { /* noop */ }
      continue
    }
    try {
      const runs = runsFromCvBlock(applicationId, versionId, batch.promptVersionId, durationMs, cv)
      const aggregated = cv.aggregated && typeof cv.aggregated === 'object'
        ? interpretAggregatedResult(cv.aggregated as Record<string, unknown>)
        : undefined
      await finalizeApplicationFromScoringResult(applicationId, batch.jobId, { runs, aggregated }, batch.batchId, 'platform')
      appsCompleted++
    } catch (err) {
      appsFailed++
      console.error(`[Platform reconciler] Finalize failed for application ${applicationId}:`, err)
      try { await applicationRepo.updateStatus(applicationId, 'ScoringFailed') } catch { /* noop */ }
    }
  }
  await scoringBatchRepo.applyTransition(batch.jobId, 'submitted', 'completed', { completed: appsCompleted, failed: appsFailed })

  return { status: 'completed' }
}

export async function cancelSubmission(batch: ScoringBatch): Promise<void> {
  if (!batch.submissionId) return
  try {
    const headers = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })
    const t = createAwrTimeoutSignal(15_000)
    await fetch(platformUrl(`/assess/batch/${batch.submissionId}/cancel`), {
      method: 'POST', headers, signal: t.signal,
    }).finally(() => t.dispose())
  } catch (err) {
    console.warn(`[Platform reconciler] Cancel HTTP failed for ${batch.submissionId}:`, err)
  }
}
