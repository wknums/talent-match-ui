import { describe, it, expect } from 'vitest'
import {
  buildStackBManualReviewPrepopulation,
  collectEvidenceByRubricCategory,
  matchCategoryToRubric,
} from '@/lib/stackb-scoring'
import type { ScoringRun, AggregatedResult, ManualReviewData } from '@/types'
import fixtures from './fixtures/manual-review-prepopulation-fixtures.json'

function createEmptyReview(appId = 'app-1', jobId = 'job-1'): ManualReviewData {
  return {
    applicationId: appId,
    jobId,
    rubricScores: {},
    overallComment: '',
    auditTrail: [],
    humanEdited: false,
    lastModifiedAt: new Date().toISOString(),
    lastModifiedBy: 'test',
  }
}

describe('collectEvidenceByRubricCategory', () => {
  it('groups evidence by matched rubric category', () => {
    const { scoringRuns, rubric } = fixtures.matchedCategories
    const rubricNames = rubric.map((r: any) => r.name)
    const evidence = collectEvidenceByRubricCategory(scoringRuns as unknown as ScoringRun[], rubricNames)

    expect(evidence['Technical Skills']).toContain('5 years Java experience')
    expect(evidence['Communication']).toContain('Led presentations')
  })

  it('matches via substring containment', () => {
    const { scoringRuns, rubric } = fixtures.substringMatchCategories
    const rubricNames = rubric.map((r: any) => r.name)
    const evidence = collectEvidenceByRubricCategory(scoringRuns as unknown as ScoringRun[], rubricNames)

    expect(evidence['Technical Skills']).toContain('Java expert')
  })

  it('handles empty evidence arrays without crash', () => {
    const { scoringRuns, rubric } = fixtures.emptyEvidenceArrays
    const rubricNames = rubric.map((r: any) => r.name)
    const evidence = collectEvidenceByRubricCategory(scoringRuns as unknown as ScoringRun[], rubricNames)

    expect(Object.keys(evidence)).toHaveLength(0)
  })

  it('deduplicates evidence case-insensitively', () => {
    const runs: ScoringRun[] = [
      {
        runId: 'r1', applicationId: 'a1', versionId: 'v1', runIndex: 1,
        modelDeploymentId: 'm1', promptVersionId: 'p1', overallScore: 80,
        subScores: {}, mustHaveResult: { passed: true, missingCriteria: [], details: {} },
        evidenceCitations: [
          { category: 'Technical Skills', snippet: 'Java expert', section: '', confidence: 0.9 },
        ],
        rationale: '', improvementRecommendations: [], createdAt: '', durationMs: 0, status: 'Success',
      },
      {
        runId: 'r2', applicationId: 'a1', versionId: 'v1', runIndex: 2,
        modelDeploymentId: 'm1', promptVersionId: 'p1', overallScore: 80,
        subScores: {}, mustHaveResult: { passed: true, missingCriteria: [], details: {} },
        evidenceCitations: [
          { category: 'Technical Skills', snippet: 'java expert', section: '', confidence: 0.9 },
        ],
        rationale: '', improvementRecommendations: [], createdAt: '', durationMs: 0, status: 'Success',
      },
    ]
    const evidence = collectEvidenceByRubricCategory(runs, ['Technical Skills'])
    expect(evidence['Technical Skills']).toHaveLength(1)
  })
})

describe('buildStackBManualReviewPrepopulation', () => {
  it('prepopulates matched categories with scores and evidence', () => {
    const { scoringRuns, rubric } = fixtures.matchedCategories
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: createEmptyReview(),
    })

    expect(result.aiPrePopulated).toBe(true)
    expect(result.aiScoringMismatch).toBe(false)
    expect(result.mismatchedCategories).toHaveLength(0)
    expect(result.rubricScores['cat-1'].score).toBeCloseTo(85)
    expect(result.rubricScores['cat-1'].comment).toContain('AI Score')
    expect(result.rubricScores['cat-1'].comment).toContain('5 years Java experience')
  })

  it('populates mismatchedCategories for completely unmatched categories', () => {
    const { scoringRuns, rubric } = fixtures.mismatchedCategories
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: createEmptyReview(),
    })

    expect(result.aiScoringMismatch).toBe(true)
    expect(result.aiPrePopulated).toBe(false)
    expect(result.mismatchedCategories).toContain('Technical Skills')
    expect(result.mismatchedCategories).toContain('Communication')
  })

  it('does not overwrite existing saved review (PR-2)', () => {
    const { scoringRuns, rubric, existingReview } = fixtures.existingSavedReview
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: existingReview as unknown as ManualReviewData,
    })

    expect(result.aiPrePopulated).toBe(false)
    expect(result.mismatchedCategories).toHaveLength(0)
    expect(result.rubricScores['cat-1'].comment).toBe('Manually scored')
  })

  it('returns empty prepopulation with no scoring runs (PR-4)', () => {
    const { scoringRuns, rubric } = fixtures.emptyScoringRuns
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: createEmptyReview(),
    })

    expect(result.aiPrePopulated).toBe(false)
    expect(result.aiScoringMismatch).toBe(false)
    expect(result.mismatchedCategories).toHaveLength(0)
  })

  it('handles mixed matched/unmatched categories (partial prepopulation)', () => {
    const runs: ScoringRun[] = [
      {
        runId: 'r1', applicationId: 'a1', versionId: 'v1', runIndex: 1,
        modelDeploymentId: 'm1', promptVersionId: 'p1', overallScore: 80,
        subScores: { 'Technical Skills': 85 },
        mustHaveResult: { passed: true, missingCriteria: [], details: {} },
        evidenceCitations: [
          { category: 'Technical Skills', snippet: 'Java expert', section: '', confidence: 0.9 },
        ],
        rationale: '', improvementRecommendations: [], createdAt: '', durationMs: 0, status: 'Success',
      },
    ]
    const rubric = [
      { id: 'cat-1', name: 'Technical Skills', weight: 0.6 },
      { id: 'cat-2', name: 'Communication', weight: 0.4 },
    ]
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: runs,
      aggregatedResult: null,
      rubric,
      existingReview: createEmptyReview(),
    })

    expect(result.aiPrePopulated).toBe(true)
    expect(result.mismatchedCategories).toContain('Communication')
    expect(result.mismatchedCategories).not.toContain('Technical Skills')
  })

  it('matches categories via word overlap (≥40%)', () => {
    const runs: ScoringRun[] = [
      {
        runId: 'r1', applicationId: 'a1', versionId: 'v1', runIndex: 1,
        modelDeploymentId: 'm1', promptVersionId: 'p1', overallScore: 80,
        subScores: { 'Communication and Presentation Skills': 75 },
        mustHaveResult: { passed: true, missingCriteria: [], details: {} },
        evidenceCitations: [
          { category: 'Communication and Presentation Skills', snippet: 'Good speaker', section: '', confidence: 0.8 },
        ],
        rationale: '', improvementRecommendations: [], createdAt: '', durationMs: 0, status: 'Success',
      },
    ]
    const rubric = [
      { id: 'cat-1', name: 'Communication Skills', weight: 1.0 },
    ]
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: runs,
      aggregatedResult: null,
      rubric,
      existingReview: createEmptyReview(),
    })

    expect(result.aiPrePopulated).toBe(true)
    expect(result.rubricScores['cat-1'].score).toBeCloseTo(75)
    expect(result.rubricScores['cat-1'].comment).toContain('Good speaker')
  })

  it('handles empty evidence with scores', () => {
    const { scoringRuns, rubric } = fixtures.emptyEvidenceArrays
    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: createEmptyReview(),
    })

    expect(result.aiPrePopulated).toBe(true)
    expect(result.rubricScores['cat-1'].comment).toContain('AI Score')
    expect(result.rubricScores['cat-1'].comment).not.toContain('Evidence')
  })

  it('does not skip prepopulation when saved review is AI-only and humanEdited is false', () => {
    const { scoringRuns, rubric } = fixtures.matchedCategories
    const aiOnlySavedReview = {
      ...createEmptyReview(),
      rubricScores: {
        'cat-1': { points: 51, maxPoints: 60, score: 85, comment: 'AI Score\n85 / 100' },
      },
      overallComment: 'Pre-populated from AI scoring...',
      humanEdited: false,
    }

    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: aiOnlySavedReview,
    })

    expect(result.aiPrePopulated).toBe(true)
  })

  it('skips prepopulation when humanEdited is true', () => {
    const { scoringRuns, rubric } = fixtures.matchedCategories
    const humanEditedReview = {
      ...createEmptyReview(),
      rubricScores: {
        'cat-1': { points: 55, maxPoints: 60, score: 91.7, comment: 'Recruiter override' },
      },
      overallComment: 'Recruiter final decision notes',
      humanEdited: true,
    }

    const result = buildStackBManualReviewPrepopulation({
      scoringRuns: scoringRuns as unknown as ScoringRun[],
      aggregatedResult: null,
      rubric: rubric as any,
      existingReview: humanEditedReview,
    })

    expect(result.aiPrePopulated).toBe(false)
    expect(result.rubricScores['cat-1'].comment).toBe('Recruiter override')
  })
})

describe('matchCategoryToRubric', () => {
  it('matches exact normalized names', () => {
    expect(matchCategoryToRubric('Technical Skills', ['Technical Skills'])).toBe('Technical Skills')
  })

  it('matches substring containment', () => {
    expect(matchCategoryToRubric('Technical Skills Assessment', ['Technical Skills', 'Communication'])).toBe('Technical Skills')
  })

  it('returns null for completely unmatched', () => {
    expect(matchCategoryToRubric('Quantum Physics', ['Technical Skills', 'Communication'])).toBeNull()
  })
})
