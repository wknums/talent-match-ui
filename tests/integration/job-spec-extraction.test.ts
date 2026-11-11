import { beforeEach, describe, expect, it, vi } from 'vitest'
import { jobSpecExtractionService } from '../../server/services/job-spec-extraction'
import { extractionInstructionRepo } from '../../server/storage/repos/extraction-instruction-repo'
import { jobSpecExtractionRepo } from '../../server/storage/repos/job-spec-extraction-repo'
import { loadDynamicRubricFixtureText } from '../setup'

describe('job spec extraction service', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    process.env.AWR_SEQ_API_ENDPOINT = 'https://example.invalid/api'
  })

  it('persists and returns a valid rubric-v2 extraction result', async () => {
    vi.spyOn(extractionInstructionRepo, 'getActive').mockResolvedValue({
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract each requirement individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    })
    const createSpy = vi.spyOn(jobSpecExtractionRepo, 'create').mockResolvedValue()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      text: async () => loadDynamicRubricFixtureText('valid-itemized-extraction.json'),
    }))

    const outcome = await jobSpecExtractionService.execute({
      fileName: 'sample-spec.md',
      contentBase64: Buffer.from('# sample').toString('base64'),
      mimeType: 'text/markdown',
      purpose: 'job_creation',
      actor: { userId: 'user-1', username: 'admin', role: 'admin', createdAt: new Date().toISOString(), fullName: 'Admin' },
    })

    expect(outcome.ok).toBe(true)
    if (outcome.ok) {
      expect(outcome.result.rubric.schemaVersion).toBe('rubric-v2')
    }
    expect(createSpy).toHaveBeenCalledOnce()
  })

  it('returns a typed failure when the protected contract is invalid', async () => {
    vi.spyOn(extractionInstructionRepo, 'getActive').mockResolvedValue({
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract each requirement individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    })
    vi.spyOn(jobSpecExtractionRepo, 'create').mockResolvedValue()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      text: async () => loadDynamicRubricFixtureText('invalid-weight-total.json'),
    }))

    const outcome = await jobSpecExtractionService.execute({
      fileName: 'sample-spec.md',
      contentBase64: Buffer.from('# sample').toString('base64'),
      mimeType: 'text/markdown',
      purpose: 'job_creation',
    })

    expect(outcome.ok).toBe(false)
    if (!outcome.ok) {
      expect(outcome.result.validationFindings.some(finding => finding.code === 'invalid_weight_total')).toBe(true)
    }
  })

  it('preserves compound and duplicate requirements across shared fixtures', async () => {
    vi.spyOn(extractionInstructionRepo, 'getActive').mockResolvedValue({
      id: 'instruction-v1',
      versionNumber: 1,
      instructionText: 'Extract each requirement individually.',
      protectedContractVersion: 'extraction-rubric-v1',
      status: 'active',
      validationStatus: 'valid',
      validationFindings: [],
      createdAt: new Date().toISOString(),
      createdBy: 'seed',
      concurrencyVersion: 1,
    })
    vi.spyOn(jobSpecExtractionRepo, 'create').mockResolvedValue()
    const fixtureBodies = [
      loadDynamicRubricFixtureText('compound-requirements.json'),
      loadDynamicRubricFixtureText('true-duplicates.json'),
    ]
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce({ ok: true, text: async () => fixtureBodies[0] })
      .mockResolvedValueOnce({ ok: true, text: async () => fixtureBodies[1] }))

    const compoundOutcome = await jobSpecExtractionService.execute({
      fileName: 'sample-spec.md',
      contentBase64: Buffer.from('# sample').toString('base64'),
      mimeType: 'text/markdown',
      purpose: 'job_creation',
    })
    const duplicateOutcome = await jobSpecExtractionService.execute({
      fileName: 'sample-spec.md',
      contentBase64: Buffer.from('# sample').toString('base64'),
      mimeType: 'text/markdown',
      purpose: 'job_creation',
    })

    expect(compoundOutcome.ok).toBe(true)
    if (compoundOutcome.ok) {
      expect(compoundOutcome.result.rubric.items).toHaveLength(3)
      expect(compoundOutcome.result.rubric.items.map(item => item.text)).toContain('Mentor junior engineers')
    }

    expect(duplicateOutcome.ok).toBe(true)
    if (duplicateOutcome.ok) {
      expect(duplicateOutcome.result.rubric.items).toHaveLength(2)
      expect(duplicateOutcome.result.rubric.items[1]).toMatchObject({
        text: '5+ years of backend or platform engineering experience',
        sourceText: 'Must have 5+ years of backend or platform engineering experience.',
      })
    }
  })
})
