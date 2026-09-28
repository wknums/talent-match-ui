import { describe, expect, it } from 'vitest'
import { createLegacyConversionProposal, projectDesiredCriteria, projectLegacyRubric, projectMustHaves } from '../../server/services/rubric-conversion'

describe('Stack A rubric conversion', () => {
  it('creates a reviewable legacy conversion proposal', () => {
    const proposal = createLegacyConversionProposal(
      [
        { id: 'legacy-1', name: 'Technical Skills', description: 'Expert SQL experience; Expert Python experience', weight: 0.6 },
        { id: 'legacy-2', name: 'Communication', description: 'Present clearly', weight: 0.4 },
      ],
      [{ id: 'm1', criterion: 'Expert SQL experience', description: '' }, { id: 'm2', criterion: 'Expert Python experience', description: '' }],
      [{ id: 'd1', qualification: 'Present clearly', description: '' }],
      'legacy-v1',
    )

    expect(proposal.schemaVersion).toBe('rubric-v2')
    expect(proposal.legacySourceVersionId).toBe('legacy-v1')
    expect(projectLegacyRubric(proposal)).toHaveLength(2)
    expect(projectMustHaves(proposal)).toHaveLength(2)
    expect(projectDesiredCriteria(proposal)).toHaveLength(1)
  })
})
