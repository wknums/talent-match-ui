import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Job } from '../../src/types'
import type { ScoringBatch } from '../../server/storage/repos/scoring-batch-repo'
import { scoringPrompt, scoringTestRun } from '../scoring-governance-fixtures'

vi.mock('../../server/services/awr-auth', () => ({
  getAwrAuthHeaders: vi.fn().mockResolvedValue({}),
}))
vi.mock('../../server/services/blob-store', () => ({
  getBlobStore: () => ({
    isRemote: true,
    put: async () => ({ blobUri: 'https://storage.invalid/candidate.txt', sha256: 'hash' }),
  }),
}))

const job: Job = {
  jobId: 'job-1', jobCode: 'TEST', title: 'Engineer', department: 'Engineering',
  organization: 'Test', postingDate: '2026-09-17', createdBy: 'admin',
  createdAt: '2026-09-17T00:00:00Z', status: 'Active',
  currentVersion: {
    versionId: 'config-1', jobId: 'job-1',
    rubric: [{ id: 'technical', name: 'Technical', weight: 1, description: 'Technical skills' }],
    mustHaves: [], desiredCriteria: [], runsPerApplication: 1, aggregationStrategy: 'median',
    longlistThreshold: 60, shortlistThreshold: 80, varianceThreshold: 15,
    rubricApprovalStatus: 'approved', rubricSource: 'manual', createdAt: '2026-09-17T00:00:00Z',
  },
}
const batch: ScoringBatch = {
  batchId: 'batch-1', jobId: 'job-1', promptVersionId: 'prompt-1',
  applicationIds: ['app-1'], runCount: 1, status: 'pending',
  submissionId: null, pollUrl: '/assess/batch/sub-1/status',
  attempt: 0, submittedAt: null, lastPolledAt: null,
  nextPollAt: '2026-09-17T00:00:00Z', lastError: null,
  leaseOwner: null, leasedUntil: null, cancelRequested: false,
}
const payload = {
  overall_score: 91.23456,
  categories: [{ category: 'Technical', score: 91.23456 }],
  eligibility_gate: { passed: true },
}
let repos: typeof import('../../server/storage/repos')
let submitter: typeof import('../../server/services/platform-submitter')
let scorer: typeof import('../../server/workers/scoring')
let pipeline: typeof import('../../server/services/pipeline')
let fetchMock: ReturnType<typeof vi.fn<typeof fetch>>

beforeAll(async () => {
  vi.stubEnv('AWR_PLATFORM_API_ENDPOINT', 'https://platform.invalid')
  vi.stubEnv('AWR_SEQ_API_ENDPOINT', 'https://sequential.invalid')
  repos = await import('../../server/storage/repos')
  submitter = await import('../../server/services/platform-submitter')
  scorer = await import('../../server/workers/scoring')
  pipeline = await import('../../server/services/pipeline')
})

beforeEach(async () => {
  vi.stubEnv('AWR_MODEL_ID', 'model-current')
  vi.stubEnv('AWR_REASONING_LEVEL', 'high')
  fetchMock = vi.fn<typeof fetch>()
  vi.stubGlobal('fetch', fetchMock)
  vi.spyOn(repos.jobRepo, 'getById').mockResolvedValue(job)
  vi.spyOn(repos.promptRepo, 'getById').mockResolvedValue(scoringPrompt())
  vi.spyOn(repos.promptRepo, 'getByJobId').mockResolvedValue([scoringPrompt()])
  vi.spyOn(repos.promptRepo, 'getTestRunsByPrompt').mockResolvedValue([scoringTestRun()])
  vi.spyOn(repos.applicationRepo, 'getDocuments').mockResolvedValue([{
    applicationId: 'app-1', documentId: 'doc-1', fileName: 'candidate.txt',
    mimeType: 'text/plain', sizeBytes: 12, sha256: 'hash', uploadedAt: '2026-09-17T00:00:00Z',
  }])
  vi.spyOn(repos.applicationRepo, 'getBlob').mockResolvedValue('Candidate CV.')
  vi.spyOn(repos.applicationRepo, 'setDocumentBlobReference').mockResolvedValue()
  vi.spyOn(repos.applicationRepo, 'addScoringRun').mockResolvedValue()
  vi.spyOn(repos.applicationRepo, 'setAggregatedResult').mockResolvedValue()
  vi.spyOn(repos.applicationRepo, 'updateStatus').mockResolvedValue()
  vi.spyOn(repos.scoringBatchRepo, 'markSubmitted').mockResolvedValue()
  vi.spyOn(repos.scoringBatchRepo, 'markFailed').mockResolvedValue()
  vi.spyOn(repos.scoringBatchRepo, 'markCompleted').mockResolvedValue()
  vi.spyOn(repos.scoringBatchRepo, 'applyTransition').mockResolvedValue()
  const { auditService } = await import('../../server/services/audit')
  vi.spyOn(auditService, 'appendEvent').mockResolvedValue()
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})
afterAll(() => vi.unstubAllEnvs())

describe('Stack A scoring profile forwarding and precision', () => {
  it('submits the approved model and reasoning in the platform batch body', async () => {
    fetchMock.mockResolvedValue(Response.json({ submissionId: 'sub-1', status: 'queued' }, { status: 202 }))
    expect(await submitter.submitBatch(batch)).toMatchObject({ status: 'submitted' })
    const [url, options] = fetchMock.mock.calls[0]
    expect(url).toBe('https://platform.invalid/assess/batch')
    expect(JSON.parse(String(options?.body))).toMatchObject({
      model: 'model-current', reasoning: 'high', promptVersionId: 'prompt-1',
      prompt: { kind: 'inline', text: 'Score Engineer.' },
    })
  })

  it.each(['AWR_MODEL_ID', 'AWR_REASONING_LEVEL'])('keeps HTTP scoring per prompt but leaves queue profile enforcement unchanged after %s changes', async setting => {
    vi.stubEnv(setting, 'changed')
    expect(await submitter.submitBatch(batch)).toMatchObject({
      status: 'permanent-failure',
      error: expect.stringContaining('Queue-worker profile'),
    })
    fetchMock.mockResolvedValueOnce(Response.json(payload))
    await expect(scorer.runScoring('app-1', 'job-1', 1)).resolves.toBeDefined()
  })

  it('forwards and records the sequential profile while retaining thousandths', async () => {
    fetchMock.mockResolvedValue(Response.json(payload))
    const result = await scorer.runScoring('app-1', 'job-1', 1)
    const form = fetchMock.mock.calls[0][1]?.body
    expect(form).toBeInstanceOf(FormData)
    if (!(form instanceof FormData)) throw new Error('Expected a multipart scoring request')
    expect(form.get('reasoningModel')).toBe('model-current')
    expect(form.get('reasoningEffort')).toBe('high')
    expect(result.runs[0]).toMatchObject({
      overallScore: 91.235, subScores: { Technical: 91.235 },
      modelDeploymentId: 'model-current', reasoningLevel: 'high', promptVersionId: 'prompt-1',
    })
    expect(repos.applicationRepo.addScoringRun).toHaveBeenCalledWith(result.runs[0])
  })

  it('retains the submitted profile when config changes while a platform batch is running', async () => {
    vi.stubEnv('AWR_MODEL_ID', 'model-new')
    fetchMock.mockResolvedValue(Response.json({
      status: 'completed', result: { cvs: [{ applicationId: 'app-1', runs: [payload] }] },
    }))
    expect(await submitter.pollBatch({ ...batch, status: 'submitted' })).toEqual({ status: 'completed' })
    expect(repos.applicationRepo.setAggregatedResult).toHaveBeenCalledWith(expect.objectContaining({
      finalScore: 91.235,
      allRuns: [expect.objectContaining({
        modelDeploymentId: 'model-current', reasoningLevel: 'high', promptVersionId: 'prompt-1',
      })],
    }))
  })

  it('updates batch and application failure state if the submitted prompt is missing', async () => {
    vi.mocked(repos.promptRepo.getById).mockResolvedValue(undefined)
    fetchMock.mockResolvedValue(Response.json({ status: 'completed', result: { cvs: [] } }))
    expect(await submitter.pollBatch({ ...batch, status: 'submitted' })).toMatchObject({ status: 'failed' })
    expect(repos.scoringBatchRepo.applyTransition).toHaveBeenCalledWith('job-1', 'submitted', 'failed', { failed: 1 })
    expect(repos.applicationRepo.updateStatus).toHaveBeenCalledWith('app-1', 'ScoringFailed')
    expect(repos.scoringBatchRepo.markCompleted).not.toHaveBeenCalled()
  })

  it('rounds locally aggregated scores and category averages before persistence', async () => {
    const runs = [91.234, 91.235].map((score, index) => scorer.buildScoringRunFromParsedResponse({
      applicationId: 'app-1', versionId: 'config-1', promptVersionId: 'prompt-1',
      runIndex: index + 1, durationMs: 10,
      rawParsedResponse: { ...payload, overall_score: score, categories: [{ category: 'Technical', score }] },
    }))
    await pipeline.finalizeApplicationFromScoringResult('app-1', 'job-1', { runs }, 'correlation', 'sequential')
    expect(repos.applicationRepo.setAggregatedResult).toHaveBeenCalledWith(expect.objectContaining({
      finalScore: 91.235, finalSubScores: { Technical: 91.235 },
      rationaleText: expect.stringContaining('91.235'),
    }))
    expect(repos.applicationRepo.updateStatus).toHaveBeenCalledWith('app-1', 'Completed', expect.objectContaining({ finalScore: 91.235 }))
  })

  it('normalizes upstream aggregate scores as well as individual runs', () => {
    expect(scorer.interpretAggregatedResult({
      final_score: 91.23456, variance: 1.23456,
      categories: [{ category: 'Technical', score: 91.23456 }],
    })).toMatchObject({ finalScore: 91.235, variance: 1.235, subScoreAverages: { Technical: 91.235 } })
  })
})
