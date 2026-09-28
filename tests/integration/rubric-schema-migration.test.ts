import { describe, expect, it } from 'vitest'
import { createLegacyConversionProposal } from '../../server/services/rubric-conversion'
import { loadDynamicRubricFixtureJson } from '../setup'
import type { RubricEnvelope } from '@/types'

describe('Stack A rubric schema migration', () => {
  it('matches the expected legacy conversion fixture', () => {
    const expected = loadDynamicRubricFixtureJson<RubricEnvelope>('expected-legacy-conversion.json')
    const actual = createLegacyConversionProposal(
      [
        { id: 'legacy-1', name: 'Technical Platform Skills', description: 'Expert SQL experience; Expert Python experience', weight: 0.6 },
        { id: 'legacy-2', name: 'Collaboration and Communication', description: 'Excellent written and verbal communication skills', weight: 0.4 },
      ],
      [
        { id: 'm1', criterion: 'Expert SQL experience', description: 'Expert SQL experience' },
        { id: 'm2', criterion: 'Expert Python experience', description: 'Expert Python experience' },
        { id: 'm3', criterion: 'Excellent written and verbal communication skills', description: 'Excellent written and verbal communication skills' },
      ],
      [],
      'legacy-config-v1',
    )

    expect(actual).toEqual(expected)
  })
})
