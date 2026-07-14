import { describe, it, expect } from 'vitest'
import { extractCandidateName } from '../../server/workers/scoring'

describe('extractCandidateName', () => {
  it('extracts candidate_name from top-level payload', () => {
    const payload = {
      candidate_name: 'Jane Doe',
      overall_score: 83,
    }

    expect(extractCandidateName(payload)).toBe('Jane Doe')
  })

  it('extracts nested candidate fullName', () => {
    const payload = {
      candidate: {
        fullName: 'John Q Public',
      },
      overall_score: 72,
    }

    expect(extractCandidateName(payload)).toBe('John Q Public')
  })

  it('ignores placeholder values', () => {
    const payload = {
      candidate_name: 'Unknown',
      overall_score: 65,
    }

    expect(extractCandidateName(payload)).toBeNull()
  })

  it('extracts name from combined multi-run response', () => {
    const payload = {
      runs: [
        { runIndex: 1, candidate_name: 'Taylor Smith', overall_score: 88 },
      ],
      aggregated: { final_score: 88 },
    }

    expect(extractCandidateName(payload)).toBe('Taylor Smith')
  })
})