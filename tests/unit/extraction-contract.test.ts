import { describe, expect, it } from 'vitest'
import {
  mapExtractionSuccess,
  toRubricEnvelope,
  validateExtractionResponse,
} from '../../server/services/extraction-contract'
import { createLegacyConversionProposal } from '../../server/services/rubric-conversion'
import { loadDynamicRubricFixtureJson } from '../setup'
import type { NormalizedExtractionDocument } from '../../server/services/extraction-contract'
import type { RubricEnvelope } from '@/types'

describe('extraction contract validation', () => {
  it('accepts the valid itemized extraction fixture and normalizes rubric-v2', () => {
    const raw = JSON.stringify(loadDynamicRubricFixtureJson<NormalizedExtractionDocument>('valid-itemized-extraction.json'))
    const expected = loadDynamicRubricFixtureJson<RubricEnvelope>('expected-valid-rubric-v2.json')

    const result = validateExtractionResponse(raw)

    expect(result.isValid).toBe(true)
    expect(result.rubric).toEqual(expected)
    expect(mapExtractionSuccess({
      extractionId: 'extract-1',
      instructionVersionId: 'instruction-v1',
      protectedContractVersion: 'extraction-rubric-v1',
      findings: result.findings,
      rubric: result.rubric!,
      document: result.document!,
    }).validationStatus).toBe('valid')
  })

  it('rejects invalid weight totals with actionable findings', () => {
    const raw = JSON.stringify(loadDynamicRubricFixtureJson('invalid-weight-total.json'))

    const result = validateExtractionResponse(raw)

    expect(result.isValid).toBe(false)
    expect(result.findings.some(finding => finding.code === 'invalid_weight_total')).toBe(true)
  })

  it('creates a deterministic legacy conversion proposal', () => {
    const proposal = createLegacyConversionProposal(
      [
        { id: 'legacy-1', name: 'Technical Platform Skills', weight: 0.6, description: 'Expert SQL experience; Expert Python experience' },
        { id: 'legacy-2', name: 'Collaboration and Communication', weight: 0.4, description: 'Excellent written and verbal communication skills' },
      ],
      [
        { id: 'm1', criterion: 'Expert SQL experience', description: 'Expert SQL experience' },
        { id: 'm2', criterion: 'Expert Python experience', description: 'Expert Python experience' },
        { id: 'm3', criterion: 'Excellent written and verbal communication skills', description: 'Excellent written and verbal communication skills' },
      ],
      [],
      'legacy-config-v1',
    )

    expect(proposal).toEqual(loadDynamicRubricFixtureJson<RubricEnvelope>('expected-legacy-conversion.json'))
    expect(toRubricEnvelope(loadDynamicRubricFixtureJson<NormalizedExtractionDocument>('compound-requirements.json'))).toEqual(
      loadDynamicRubricFixtureJson<RubricEnvelope>('expected-compound-rubric-v2.json'),
    )
  })
})
