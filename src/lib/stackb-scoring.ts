import type { AggregatedResult, EvidenceCitation, ManualReviewData, ManualReviewRubricEntry, ScoringRun } from '@/types'

export interface StackBCompatibleAggregatedResult {
  finalScore: number
  decision: string
  variance: number
  confidence: number
  consolidatedRationale: string
  mergedImprovementTipsJson: string
}

export interface StackBCompatibleGateResult {
  passed: boolean
  missingCriteria: string[]
  recommendation?: string
}

export interface StackBCompatibleGateEntry {
  criterion?: string
  passed: boolean
  evidence?: string
}

export interface StackBPrepopulatedCategory {
  score: number
  points: number
  maxPoints: number
  comment: string
}

export interface StackBPrepopulationResult {
  aiPrePopulated: boolean
  aiScoringMismatch: boolean
  mismatchedCategories: string[]
  rubricScores: Record<string, ManualReviewRubricEntry>
  overallComment?: string
}

function toWeightedPoints(score: number, maxPoints: number): number {
  return Math.round((score / 100) * maxPoints)
}

function toPercentageScore(points: number | undefined, maxPoints: number): number {
  if (!Number.isFinite(points) || maxPoints <= 0) {
    return 0
  }

  return (Number(points) / maxPoints) * 100
}

export function normalizeCategoryName(name: string): string {
  return name.toLowerCase().replace(/[_\-()&/,;:]/g, ' ').replace(/\s+/g, ' ').trim()
}

export function matchCategoryToRubric(llmName: string, rubricNames: string[]): string | null {
  const normalizedLlm = normalizeCategoryName(llmName)
  const exact = rubricNames.find(name => normalizeCategoryName(name) === normalizedLlm)
  if (exact) return exact

  const contained = rubricNames.find(name => {
    const normalizedName = normalizeCategoryName(name)
    return normalizedLlm.includes(normalizedName) || normalizedName.includes(normalizedLlm)
  })
  if (contained) return contained

  const llmWords = new Set(normalizedLlm.split(' ').filter(word => word.length > 2))
  let best: string | null = null
  let bestOverlap = 0
  for (const rubricName of rubricNames) {
    const rubricWords = normalizeCategoryName(rubricName).split(' ').filter(word => word.length > 2)
    const overlap = rubricWords.filter(word => llmWords.has(word)).length
    if (overlap > bestOverlap && rubricWords.length > 0 && overlap / rubricWords.length >= 0.4) {
      bestOverlap = overlap
      best = rubricName
    }
  }

  return best
}

export function toStackBCompatibleAggregatedResult(aggregatedResult: AggregatedResult | null | undefined): StackBCompatibleAggregatedResult | null {
  if (!aggregatedResult) return null

  const mergedTips = aggregatedResult.recommendationsText
    ? JSON.stringify(
        aggregatedResult.recommendationsText
          .split(';')
          .map(part => part.trim())
          .filter(Boolean),
      )
    : '[]'

  return {
    finalScore: aggregatedResult.finalScore,
    decision: aggregatedResult.finalDecision,
    variance: aggregatedResult.variance,
    confidence: aggregatedResult.confidence,
    consolidatedRationale: aggregatedResult.rationaleText,
    mergedImprovementTipsJson: mergedTips,
  }
}

export function parseCategoryScores(run: ScoringRun): Record<string, number> {
  return { ...(run.subScores ?? {}) }
}

export function parseGate(run: ScoringRun): StackBCompatibleGateResult | null {
  const raw = run.mustHaveResult
  if (!raw) return null

  const details = raw.details && typeof raw.details === 'object' ? raw.details as Record<string, unknown> : {}
  const recommendation = typeof details.recommendation === 'string'
    ? details.recommendation
    : undefined

  return {
    passed: raw.passed,
    missingCriteria: Array.isArray(raw.missingCriteria) ? raw.missingCriteria : [],
    recommendation,
  }
}

export function parseGateEntries(run: ScoringRun): StackBCompatibleGateEntry[] | null {
  const details = run.mustHaveResult?.details
  if (!details || typeof details !== 'object') return null

  const record = details as Record<string, unknown>
  const entries = record.entries
  if (Array.isArray(entries)) {
    const mapped = entries
      .filter((item): item is Record<string, unknown> => typeof item === 'object' && item !== null)
      .map(item => ({
        criterion: typeof item.criterion === 'string' ? item.criterion : undefined,
        passed: item.passed === true,
        evidence: typeof item.evidence === 'string' ? item.evidence : undefined,
      }))
      .filter(entry => entry.criterion)
    return mapped.length > 0 ? mapped : null
  }

  const flatEntries = Object.entries(record)
    .filter(([key]) => key !== 'source' && key !== 'entries')
    .filter(([, value]) => typeof value === 'boolean')
    .map(([criterion, passed]) => ({ criterion, passed: passed as boolean, evidence: undefined }))

  return flatEntries.length > 0 ? flatEntries : null
}

export function hasMeaningfulManualReviewContent(review: ManualReviewData | null | undefined): boolean {
  // Clarification (Option 1): only skip AI prepopulation when human edits are persisted.
  return review?.humanEdited === true
}

export function normalizeManualReviewForRubric(args: {
  review: ManualReviewData | null | undefined
  rubric: Array<{ id: string; name: string; weight: number }>
}): ManualReviewData | null {
  const { review, rubric } = args
  if (!review) return null

  const rubricScores = review.rubricScores ?? {}
  const normalizedEntries = Object.entries(rubricScores).map(([key, value]) => ({
    key,
    normalizedKey: normalizeCategoryName(key),
    value,
  }))

  const normalizedRubricScores: Record<string, ManualReviewRubricEntry> = {}

  for (const category of rubric) {
    const matchedEntry = rubricScores[category.id]
      ?? rubricScores[category.name]
      ?? normalizedEntries.find((entry) => entry.normalizedKey === normalizeCategoryName(category.name))?.value

    if (!matchedEntry) {
      continue
    }

    const maxPoints = Number.isFinite(matchedEntry.maxPoints)
      ? matchedEntry.maxPoints
      : Math.round(category.weight * 100)
    const score = Number.isFinite(matchedEntry.score)
      ? matchedEntry.score ?? 0
      : toPercentageScore(matchedEntry.points, maxPoints)
    const points = Number.isFinite(matchedEntry.points)
      ? matchedEntry.points
      : toWeightedPoints(score, maxPoints)

    normalizedRubricScores[category.id] = {
      score,
      points,
      maxPoints,
      comment: matchedEntry.comment ?? '',
    }
  }

  return {
    ...review,
    humanEdited: review.humanEdited === true,
    rubricScores: normalizedRubricScores,
  }
}

export function collectEvidenceByRubricCategory(
  scoringRuns: ScoringRun[],
  rubricNames: string[],
): Record<string, string[]> {
  const evidenceByCategory: Record<string, string[]> = {}

  const appendEvidence = (categoryName: string, value: string | undefined) => {
    const snippet = value?.trim()
    if (!snippet) return
    if (!evidenceByCategory[categoryName]) evidenceByCategory[categoryName] = []
    const normalizedSnippet = snippet.toLowerCase()
    if (!evidenceByCategory[categoryName].some(existing => existing.trim().toLowerCase() === normalizedSnippet)) {
      evidenceByCategory[categoryName].push(snippet)
    }
  }

  for (const run of scoringRuns) {
    for (const citation of run.evidenceCitations ?? []) {
      const matched = matchCategoryToRubric((citation as EvidenceCitation).category ?? '', rubricNames)
      if (!matched || !citation.snippet) continue
      appendEvidence(matched, citation.snippet)
    }
  }

  return evidenceByCategory
}

export function buildStackBManualReviewPrepopulation(args: {
  scoringRuns: ScoringRun[]
  aggregatedResult: AggregatedResult | null
  rubric: Array<{ id: string; name: string; weight: number }>
  existingReview: ManualReviewData
}): StackBPrepopulationResult {
  const { scoringRuns, aggregatedResult, rubric, existingReview } = args
  const rubricNames = rubric.map(category => category.name)

  const hasExistingReviewContent = hasMeaningfulManualReviewContent(existingReview)

  const avgScoresByCategory: Record<string, number> = {}
  const evidenceByCategory = collectEvidenceByRubricCategory(scoringRuns, rubricNames)

  for (const category of rubric) {
    const scores: number[] = []

    for (const run of scoringRuns) {
      for (const [rawCategory, score] of Object.entries(parseCategoryScores(run))) {
        const matched = matchCategoryToRubric(rawCategory, rubricNames)
        if (matched === category.name) {
          scores.push(Number(score ?? 0))
        }
      }
    }

    if (scores.length > 0) {
      avgScoresByCategory[category.name] = scores.reduce((sum, value) => sum + value, 0) / scores.length
    }
  }

  if (hasExistingReviewContent) {
    return {
      aiPrePopulated: false,
      aiScoringMismatch: false,
      mismatchedCategories: [],
      rubricScores: existingReview.rubricScores,
      overallComment: existingReview.overallComment,
    }
  }

  // Prioritize aggregatedResult.finalSubScores if available (ensures parity with Stack B behavior)
  if (aggregatedResult?.finalSubScores && Object.keys(aggregatedResult.finalSubScores).length > 0) {
    for (const category of rubric) {
      if (category.name in aggregatedResult.finalSubScores) {
        avgScoresByCategory[category.name] = aggregatedResult.finalSubScores[category.name]
      }
    }
  }

  // Compute which rubric categories had zero matched AI scores AND zero matched evidence
  const mismatchedCategories: string[] = []
  for (const category of rubric) {
    const hasScore = avgScoresByCategory[category.name] != null
    const hasEvidence = (evidenceByCategory[category.name]?.length ?? 0) > 0
    if (!hasScore && !hasEvidence) {
      mismatchedCategories.push(category.name)
    }
  }

  if (Object.keys(avgScoresByCategory).length === 0) {
    const totalMismatch = scoringRuns.length > 0
    return {
      aiPrePopulated: false,
      aiScoringMismatch: totalMismatch,
      mismatchedCategories: totalMismatch ? mismatchedCategories : [],
      rubricScores: existingReview.rubricScores,
      overallComment: existingReview.overallComment,
    }
  }

  const rubricScores: Record<string, ManualReviewRubricEntry> = {}
  for (const category of rubric) {
    const maxPoints = Math.round(category.weight * 100)
    const aiScore = avgScoresByCategory[category.name]

    if (aiScore == null) {
      rubricScores[category.id] = { score: 0, points: 0, maxPoints, comment: '' }
      continue
    }

    const parts = [`AI Score\n${aiScore.toFixed(1)} / 100`]
    const evidence = evidenceByCategory[category.name]
    if (evidence?.length) {
      parts.push(`\nEvidence\n${evidence.map(item => `- ${item}`).join('\n')}`)
    }

    rubricScores[category.id] = {
      score: aiScore,
      points: toWeightedPoints(aiScore, maxPoints),
      maxPoints,
      comment: parts.join('\n'),
    }
  }

  let overallComment = existingReview.overallComment
  const stackBCompatibleAggregated = toStackBCompatibleAggregatedResult(aggregatedResult)
  if (!overallComment && stackBCompatibleAggregated) {
    const parts = [
      `Pre-populated from AI scoring (score: ${stackBCompatibleAggregated.finalScore.toFixed(1)}, variance: ${stackBCompatibleAggregated.variance.toFixed(2)}). Please verify and adjust.`,
    ]

    if (stackBCompatibleAggregated.consolidatedRationale) {
      parts.push(`\nAI Rationale:\n${stackBCompatibleAggregated.consolidatedRationale}`)
    }

    try {
      const tips = JSON.parse(stackBCompatibleAggregated.mergedImprovementTipsJson) as string[]
      if (tips.length > 0) {
        parts.push(`\nAI Recommendations:\n${tips.map(item => `- ${item}`).join('\n')}`)
      }
    } catch {
      // ignore malformed tips payload
    }

    overallComment = parts.join('')
  }

  return {
    aiPrePopulated: true,
    aiScoringMismatch: false,
    mismatchedCategories,
    rubricScores,
    overallComment,
  }
}