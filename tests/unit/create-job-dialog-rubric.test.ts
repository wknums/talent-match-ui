import { describe, expect, it } from 'vitest'
import { requiresLegacyConversionConfirmation } from '@/components/CreateJobDialog'
import type { Job, RubricEnvelope } from '@/types'

function legacyEditingJob(): Job {
  return {
    jobId: 'job-1',
    jobCode: 'JOB-1',
    title: 'Senior Data Platform Engineer',
    department: 'Engineering',
    organization: 'Northwind Analytics',
    postingDate: '2026-08-01T00:00:00.000Z',
    createdBy: 'admin-1',
    createdAt: '2026-08-01T00:00:00.000Z',
    status: 'Active',
    currentVersion: {
      versionId: 'cfg-1',
      jobId: 'job-1',
      rubric: [
        { id: 'legacy-1', name: 'Technical Skills', description: 'Expert SQL experience; Expert Python experience', weight: 1 },
      ],
      mustHaves: [{ id: 'm1', criterion: 'Expert SQL experience', description: 'Expert SQL experience' }],
      desiredCriteria: [],
      runsPerApplication: 3,
      aggregationStrategy: 'median',
      longlistThreshold: 60,
      shortlistThreshold: 75,
      varianceThreshold: 15,
      rubricApprovalStatus: 'draft',
      rubricSource: 'manual',
      createdAt: '2026-08-01T00:00:00.000Z',
    },
  }
}

const preview: RubricEnvelope = {
  schemaVersion: 'rubric-v2',
  legacySourceVersionId: 'cfg-1',
  categories: [{ id: 'cat-1', name: 'Technical Skills', weight: 1, description: 'Core technical match', order: 0 }],
  items: [{
    id: 'item-1',
    categoryId: 'cat-1',
    text: 'Expert SQL experience',
    requirementType: 'must_have',
    order: 0,
    sourceText: 'Expert SQL experience',
    reviewStatus: 'confirmed',
    createdFrom: 'legacy_conversion',
  }],
}

describe('CreateJobDialog legacy-conversion safeguards', () => {
  it('blocks generic saves while a legacy conversion preview is awaiting confirmation', () => {
    expect(requiresLegacyConversionConfirmation(legacyEditingJob(), preview)).toBe(true)
  })

  it('allows normal saves when there is no pending preview or the rubric is already itemized', () => {
    expect(requiresLegacyConversionConfirmation(legacyEditingJob(), null)).toBe(false)

    const alreadyConverted = {
      ...legacyEditingJob(),
      currentVersion: {
        ...legacyEditingJob().currentVersion,
        rubricEnvelope: preview,
      },
    }
    expect(requiresLegacyConversionConfirmation(alreadyConverted, preview)).toBe(false)
  })
})
