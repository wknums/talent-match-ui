import { describe, expect, it } from 'vitest'
import { createExtractionRecord } from '../../server/services/extraction-contract'

describe('job spec extraction record factory', () => {
  it('creates a persisted diagnostics record envelope', () => {
    const record = createExtractionRecord({
      id: 'extract-1',
      purpose: 'job_creation',
      instructionVersionId: 'instruction-v1',
      protectedContractVersion: 'extraction-rubric-v1',
      sourceFileName: 'sample-spec.md',
      sourceMimeType: 'text/markdown',
      sourceSha256: 'hash',
      rawResponse: '{}',
      normalizedResponseJson: '{}',
      validationStatus: 'valid',
      validationFindings: [{ code: 'needs_review', severity: 'warning', path: '$.requirements[0]', message: 'Review it' }],
      createdBy: 'admin',
      correlationId: 'corr-1',
    })

    expect(record.completedAt).toBeTruthy()
    expect(record.validationFindings[0].code).toBe('needs_review')
  })
})
