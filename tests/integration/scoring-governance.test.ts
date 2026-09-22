import { mkdtempSync, rmSync } from 'node:fs'
import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import express from 'express'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PromptGenerationInstruction, ScoringPrompt } from '../../src/types'
import { scoringPrompt, scoringTestRun } from '../scoring-governance-fixtures'

const directory = mkdtempSync(join(tmpdir(), 'talentmatch-scoring-governance-'))
let database: typeof import('../../server/storage/db')
let repos: typeof import('../../server/storage/repos')
let resolveInstruction: typeof import('../../server/services/prompt-generation-instructions').resolvePromptGenerationInstruction
let productionPromptId: typeof import('../../server/services/prompt-helpers').getProductionApprovedPromptId
let server: Server | undefined
let baseUrl: string

beforeAll(async () => {
  vi.stubEnv('STORAGE_PROVIDER', 'local')
  vi.stubEnv('SQLITE_DB_PATH', join(directory, 'scoring.db'))
  vi.stubEnv('AWR_SEQ_API_ENDPOINT', '')
  vi.stubEnv('AWR_PLATFORM_API_ENDPOINT', '')
  database = await import('../../server/storage/db')
  await database.initializeDatabase()
  repos = await import('../../server/storage/repos')
  resolveInstruction = (await import('../../server/services/prompt-generation-instructions')).resolvePromptGenerationInstruction
  productionPromptId = (await import('../../server/services/prompt-helpers')).getProductionApprovedPromptId
  const { auditService } = await import('../../server/services/audit')
  vi.spyOn(auditService, 'appendEvent').mockResolvedValue()
  const { createPromptsRouter } = await import('../../server/routes/prompts')
  const { createPromptGenerationInstructionsRouter } = await import('../../server/routes/prompt-generation-instructions')
  const { errorHandler } = await import('../../server/middleware/error-handler')
  const { createAuthMiddleware } = await import('../../server/middleware/auth')
  const { setCurrentUser } = await import('../../server/session')
  setCurrentUser({
    userId: 'admin', username: 'admin', role: 'admin',
    fullName: 'Administrator', createdAt: '2026-09-17T00:00:00Z',
  })
  const app = express()
  app.use(express.json())
  app.use(createAuthMiddleware({ mode: 'simple' }))
  app.use('/api/jobs', createPromptsRouter())
  app.use('/api/admin/prompt-generation-instructions', createPromptGenerationInstructionsRouter())
  app.use(errorHandler)
  await new Promise<void>(resolve => {
    server = app.listen(0, '127.0.0.1', () => {
      baseUrl = `http://127.0.0.1:${(server!.address() as AddressInfo).port}`
      resolve()
    })
  })
})

afterAll(async () => {
  if (server) await new Promise<void>((resolve, reject) => server!.close(error => error ? reject(error) : resolve()))
  if (database) await (await database.getPool()).close()
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
  rmSync(directory, { recursive: true, force: true })
})

beforeEach(async () => {
  vi.stubEnv('AWR_MODEL_ID', 'model-current')
  vi.stubEnv('AWR_REASONING_LEVEL', 'high')
  const pool = await database.getPool()
  for (const table of ['PromptTestRuns', 'ScoringPrompts', 'PromptGenerationInstructions', 'Applications', 'JobConfigVersions', 'Jobs']) {
    await pool.request().query(`DELETE FROM ${table}`)
  }
  await pool.request().query(`INSERT INTO Jobs (Id, Title, Department, PostingDate)
    VALUES ('job-1', 'Engineer', 'Engineering', '2026-09-17')`)
  await repos.promptRepo.create(scoringPrompt())
  await repos.promptRepo.createTestRun(scoringTestRun())
  await repos.promptRepo.updateTestRun('test-1', {
    approvedModelId: 'model-current', approvedReasoningLevel: 'high',
  })
})

function request(path: string, method = 'GET', body?: unknown) {
  return fetch(`${baseUrl}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

describe('Stack A scoring governance routes and persistence', () => {
  it('creates and activates system/job instruction versions with override precedence', async () => {
    const systemPath = '/api/admin/prompt-generation-instructions'
    const jobPath = '/api/jobs/job-1/prompt-generation-instructions'
    const systemResponse = await request(systemPath, 'POST', { instructionText: 'System default' })
    expect(systemResponse.status).toBe(201)
    const system: PromptGenerationInstruction = await systemResponse.json()
    expect((await request(`${systemPath}/${system.id}/activate`, 'POST')).status).toBe(200)
    expect(await resolveInstruction('job-1')).toMatchObject({ scope: 'system', instruction: { id: system.id } })

    const override: PromptGenerationInstruction = await (await request(jobPath, 'POST', { instructionText: 'Job override' })).json()
    expect(await resolveInstruction('job-1')).toMatchObject({ scope: 'system' })
    expect((await request(`${jobPath}/${override.id}/activate`, 'POST')).status).toBe(200)
    expect(await resolveInstruction('job-1')).toMatchObject({ scope: 'job', instruction: { id: override.id } })
    expect(await resolveInstruction('other-job')).toMatchObject({ scope: 'system' })

    const revision: PromptGenerationInstruction = await (await request(jobPath, 'POST', { instructionText: 'Revised override' })).json()
    expect(revision.versionNumber).toBe(2)
    await request(`${jobPath}/${revision.id}/activate`, 'POST')
    expect(await repos.promptRepo.getInstructionById(override.id)).toMatchObject({ status: 'inactive', instructionText: 'Job override' })
    expect(await resolveInstruction('job-1')).toMatchObject({ instruction: { id: revision.id } })
  })

  it('fails explicitly when no active system instruction exists', async () => {
    await expect(resolveInstruction('job-1')).rejects.toThrow('No active system')
  })

  it('edits into a new version, preserving the prior text and approval', async () => {
    const original = await repos.promptRepo.getById('prompt-1')
    vi.stubEnv('AWR_MODEL_ID', 'model-new')
    const response = await request('/api/jobs/job-1/prompts/prompt-1', 'PUT', { promptText: 'Revised text' })
    expect(response.status).toBe(201)
    const revision: ScoringPrompt = await response.json()
    expect(revision).toMatchObject({ versionNumber: 5, status: 'draft', modelId: 'model-new', reasoningLevel: 'high' })
    expect(revision.promptId).not.toBe('prompt-1')
    expect(revision.approvedTestRunId).toBeUndefined()
    expect(await repos.promptRepo.getById('prompt-1')).toEqual(original)
  })

  it('routes profile and test-list requests without generic-route shadowing', async () => {
    const response = await request('/api/jobs/job-1/prompts/prompt-1/profile')
    expect(response.status).toBe(200)
    expect(await response.json()).toMatchObject({ isMatch: true, approvedTestRunId: 'test-1' })
    expect(await (await request('/api/jobs/job-1/prompts/prompt-1/test-runs')).json()).toHaveLength(1)
    expect(await productionPromptId('job-1')).toBe('prompt-1')
  })

  it.each(['AWR_MODEL_ID', 'AWR_REASONING_LEVEL'])('keeps stored prompt approval valid after %s changes', async setting => {
    vi.stubEnv(setting, 'changed')
    const profile = await request('/api/jobs/job-1/prompts/prompt-1/profile')
    expect(profile.status).toBe(200)
    expect(await profile.json()).toMatchObject({
      isMatch: true,
      currentModelId: 'model-current',
      currentReasoningLevel: 'high',
    })
    await expect(productionPromptId('job-1')).resolves.toBe('prompt-1')
  })

  it('persists exact test approval and demotes the prior production version', async () => {
    await repos.promptRepo.create(scoringPrompt({ promptId: 'prompt-old', versionNumber: 3 }))
    await repos.promptRepo.updateTestRun('test-1', { status: 'pending_review' })
    await repos.applicationRepo.create({
      applicationId: 'app-1', jobId: 'job-1', candidateRef: 'test-candidate',
      status: 'Completed', createdAt: '2026-09-17T00:00:00Z', documents: [], testRunId: 'test-1',
    })
    expect((await request('/api/jobs/job-1/prompts/prompt-1/test-runs/test-1/approve', 'POST', {})).status).toBe(200)
    expect(await repos.promptRepo.getTestRun('test-1')).toMatchObject({
      status: 'approved', approvedModelId: 'model-current', approvedReasoningLevel: 'high',
    })
    expect((await request('/api/jobs/job-1/prompts/prompt-1/approve-production', 'POST', {})).status).toBe(200)
    expect(await repos.promptRepo.getById('prompt-old')).toMatchObject({ status: 'inactive' })
    expect(await repos.promptRepo.getById('prompt-1')).toMatchObject({ approvedTestRunId: 'test-1' })
  })

  it('rejects empty test evidence and evidence from another prompt', async () => {
    expect((await request('/api/jobs/job-1/prompts/prompt-1/test-runs/test-1/approve', 'POST', {})).status).toBe(400)
    await repos.promptRepo.create(scoringPrompt({ promptId: 'another', versionNumber: 5 }))
    expect((await request('/api/jobs/job-1/prompts/another/test-runs/test-1/approve', 'POST', {})).status).toBe(404)
  })

  it.each([
    { modelId: 'older-model' },
    { reasoningLevel: 'low' },
  ])('rejects a current approval stamp on stale execution evidence: %j', async profile => {
    await repos.promptRepo.createTestRun(scoringTestRun({ ...profile, testRunId: 'stale-test' }))
    await repos.promptRepo.updateTestRun('test-1', { status: 'rejected' })
    await repos.promptRepo.updateTestRun('stale-test', {
      approvedModelId: 'model-current', approvedReasoningLevel: 'high',
    })
    expect((await request('/api/jobs/job-1/prompts/prompt-1/approve-production', 'POST', {})).status).toBe(400)
    await expect(productionPromptId('job-1')).rejects.toMatchObject({ statusCode: 409 })
  })

  it('retains numeric thousandths through persistence and ordering', async () => {
    for (const [applicationId, score] of [['higher', 91.235], ['lower', 91.234]] as const) {
      await repos.applicationRepo.create({
        applicationId, jobId: 'job-1', candidateRef: applicationId,
        status: 'Completed', createdAt: '2026-09-17T00:00:00Z', documents: [],
      })
      await repos.applicationRepo.updateStatus(applicationId, 'Completed', { finalScore: score })
    }
    const result = await (await database.getPool()).request()
      .query('SELECT Id, FinalScore FROM Applications ORDER BY FinalScore DESC')
    expect(result.recordset).toEqual([
      { Id: 'higher', FinalScore: 91.235 }, { Id: 'lower', FinalScore: 91.234 },
    ])
  })

  it('does not edit, test or approve a prompt belonging to another job', async () => {
    for (const [suffix, method] of [['', 'PUT'], ['/test-runs', 'POST'], ['/approve-production', 'POST']]) {
      expect((await request(`/api/jobs/wrong-job/prompts/prompt-1${suffix}`, method, { promptText: 'Text' })).status).toBe(404)
    }
  })
})
