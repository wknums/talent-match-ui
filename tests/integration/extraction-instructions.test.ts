import type { AddressInfo } from 'node:net'
import type { Server } from 'node:http'
import express from 'express'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  canonicalJson,
  ensureDynamicRubricArtifactExists,
  loadDynamicRubricContractJson,
  loadDynamicRubricFixtureJson,
  loadDynamicRubricFixtureText,
} from '../setup'
import { createAuthMiddleware } from '../../server/middleware/auth.js'
import { createExtractionInstructionsRouter } from '../../server/routes/extraction-instructions.js'
import { auditService } from '../../server/services/audit.js'
import { extractionInstructionRepo } from '../../server/storage/repos/extraction-instruction-repo.js'
import { jobSpecExtractionService } from '../../server/services/job-spec-extraction.js'
import { setCurrentUser } from '../../server/session.js'

describe('dynamic rubric feature fixtures', () => {
  it('loads the protected extraction schema and representative fixtures', () => {
    ensureDynamicRubricArtifactExists('contracts/extraction-rubric.schema.json')
    ensureDynamicRubricArtifactExists('contracts/rubric-config.schema.json')
    ensureDynamicRubricArtifactExists('contracts/fixtures/valid-itemized-extraction.json')
    ensureDynamicRubricArtifactExists('contracts/fixtures/expected-valid-rubric-v2.json')

    const extractionSchema = loadDynamicRubricContractJson<Record<string, unknown>>('extraction-rubric.schema.json')
    const rubricSchema = loadDynamicRubricContractJson<Record<string, unknown>>('rubric-config.schema.json')
    const extractionFixture = loadDynamicRubricFixtureJson<Record<string, unknown>>('valid-itemized-extraction.json')
    const rubricFixture = loadDynamicRubricFixtureJson<Record<string, unknown>>('expected-valid-rubric-v2.json')

    expect(extractionSchema.title).toBe('Job specification extraction result')
    expect(rubricSchema.title).toBe('Editable rubric configuration')
    expect(extractionFixture.requirements).toBeInstanceOf(Array)
    expect(rubricFixture.schemaVersion).toBe('rubric-v2')
  })

  it('keeps invalid fixtures intentionally distinct from valid normalized outputs', () => {
    const invalidFixture = loadDynamicRubricFixtureJson<Record<string, unknown>>('invalid-weight-total.json')
    const validRubric = loadDynamicRubricFixtureJson<Record<string, unknown>>('expected-valid-rubric-v2.json')

    expect(canonicalJson(invalidFixture)).not.toBe(canonicalJson(validRubric))
    expect((invalidFixture.rubric as { weights_sum_to_1_0?: boolean }).weights_sum_to_1_0).toBe(false)
  })
})

describe('stack A extraction instruction routes', () => {
  let server: Server
  let baseUrl: string

  beforeAll(async () => {
    const app = express()
    app.use(express.json())
    app.use('/api/admin/extraction-instructions', createAuthMiddleware({ mode: 'simple' }), createExtractionInstructionsRouter())

    await new Promise<void>((resolve) => {
      server = app.listen(0, '127.0.0.1', () => {
        const address = server.address() as AddressInfo
        baseUrl = `http://127.0.0.1:${address.port}`
        resolve()
      })
    })
  })

  afterAll(async () => {
    await new Promise<void>((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve())
    })
  })

  beforeEach(() => {
    vi.restoreAllMocks()
    vi.spyOn(extractionInstructionRepo, 'list').mockResolvedValue([{
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract requirements individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    }])
    vi.spyOn(extractionInstructionRepo, 'getById').mockResolvedValue({
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract requirements individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    })
    vi.spyOn(extractionInstructionRepo, 'getNextVersionNumber').mockResolvedValue(2)
    vi.spyOn(extractionInstructionRepo, 'create').mockResolvedValue()
    vi.spyOn(extractionInstructionRepo, 'update').mockResolvedValue()
    vi.spyOn(extractionInstructionRepo, 'getActive').mockResolvedValue({
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract requirements individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    })
    vi.spyOn(extractionInstructionRepo, 'activate').mockResolvedValue()
    vi.spyOn(jobSpecExtractionService, 'execute').mockResolvedValue({
      ok: true,
      instruction: {
        id: 'instruction-v1',
        versionNumber: 1,
        instructionText: 'Extract requirements individually.',
        protectedContractVersion: 'extraction-rubric-v1',
        status: 'active',
        validationStatus: 'valid',
        validationFindings: [],
        createdAt: new Date().toISOString(),
        createdBy: 'seed',
        concurrencyVersion: 1,
      },
      record: {
        id: 'extract-1',
        purpose: 'instruction_validation',
        instructionVersionId: 'instruction-v1',
        protectedContractVersion: 'extraction-rubric-v1',
        sourceFileName: 'sample-spec.md',
        sourceMimeType: 'text/markdown',
        sourceSha256: 'hash',
        rawResponse: '{}',
        normalizedResponseJson: '{}',
        validationStatus: 'valid',
        validationFindings: [],
        createdAt: new Date().toISOString(),
        createdBy: 'admin',
        completedAt: new Date().toISOString(),
        correlationId: 'corr-1',
      },
      result: {
        extractionId: 'extract-1',
        instructionVersionId: 'instruction-v1',
        protectedContractVersion: 'extraction-rubric-v1',
        validationStatus: 'valid',
        validationFindings: [],
        title: 'Senior Data Platform Engineer',
        department: 'Engineering',
        organization: 'Northwind',
        jobDescription: 'Role',
        rubric: loadDynamicRubricFixtureJson('expected-valid-rubric-v2.json'),
      },
    } as any)
    vi.spyOn(auditService, 'appendEvent').mockResolvedValue()
  })

  it('allows admins to create and validate drafts', async () => {
    setCurrentUser({
      userId: 'admin-1',
      username: 'admin',
      role: 'admin',
      fullName: 'Administrator',
      createdAt: new Date().toISOString(),
    })

    const createResponse = await fetch(`${baseUrl}/api/admin/extraction-instructions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ instructionText: 'New draft', changeNote: 'Improve splitting' }),
    })
    expect(createResponse.status).toBe(201)
    expect(auditService.appendEvent).toHaveBeenCalledWith(
      'admin',
      'extraction-instruction.created',
      'ExtractionInstructionVersion',
      expect.any(String),
      expect.not.objectContaining({ instructionText: expect.anything() }),
    )

    const validateResponse = await fetch(`${baseUrl}/api/admin/extraction-instructions/instruction-v1/validate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileName: 'sample-spec.md', content: Buffer.from(loadDynamicRubricFixtureText('sample-spec.md')).toString('base64'), mimeType: 'text/markdown' }),
    })
    expect(validateResponse.status).toBe(200)
  })

  it('forbids non-admin access', async () => {
    setCurrentUser({
      userId: 'rec-1',
      username: 'recruiter',
      role: 'recruiter',
      fullName: 'Recruiter',
      createdAt: new Date().toISOString(),
    })

    const response = await fetch(`${baseUrl}/api/admin/extraction-instructions`)
    expect(response.status).toBe(403)
    await expect(response.json()).resolves.toMatchObject({
      error: 'forbidden',
      correlationId: response.headers.get('x-correlation-id'),
    })
  })

  it('returns correlated stale-version conflicts on activation', async () => {
    setCurrentUser({
      userId: 'admin-1',
      username: 'admin',
      role: 'admin',
      fullName: 'Administrator',
      createdAt: new Date().toISOString(),
    })

    const response = await fetch(`${baseUrl}/api/admin/extraction-instructions/instruction-v1/activate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Correlation-ID': 'instruction-conflict-1' },
      body: JSON.stringify({ expectedConcurrencyVersion: 99 }),
    })

    expect(response.status).toBe(409)
    await expect(response.json()).resolves.toMatchObject({
      error: 'stale_version',
      correlationId: 'instruction-conflict-1',
    })
  })
})
