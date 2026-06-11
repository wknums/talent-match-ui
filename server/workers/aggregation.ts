import { randomUUID } from 'node:crypto'
import { applicationRepo, jobRepo } from '../storage/repos/index.js'
import type { ScoringRun, AggregatedResult } from '../../src/types/index.js'

/**
 * Fuzzy-match an LLM-derived category name to the closest rubric category name.
 * Handles normalisation differences (casing, punctuation, word order).
 */
export function findBestRubricMatch(llmName: string, rubricNames: string[]): string | null {
  const norm = (s: string) => s.toLowerCase().replace(/[_\-()&/,;:]/g, ' ').replace(/\s+/g, ' ').trim()
  const normLlm = norm(llmName)

  // 1. Exact normalised match
  const exact = rubricNames.find(rn => norm(rn) === normLlm)
  if (exact) return exact

  // 2. One contains the other
  const contained = rubricNames.find(rn => {
    const normRn = norm(rn)
    return normLlm.includes(normRn) || normRn.includes(normLlm)
  })
  if (contained) return contained

  // 3. Significant word overlap (words > 2 chars)
  const llmWords = new Set(normLlm.split(/\s+/).filter(w => w.length > 2))
  let best: string | null = null
  let bestOverlap = 0
  for (const rn of rubricNames) {
    const rnWords = norm(rn).split(/\s+/).filter(w => w.length > 2)
    const overlap = rnWords.filter(w => llmWords.has(w)).length
    if (overlap > bestOverlap && rnWords.length > 0 && overlap / rnWords.length >= 0.4) {
      bestOverlap = overlap
      best = rn
    }
  }
  return best
}

export async function runAggregation(
  applicationId: string,
  jobId: string,
): Promise<AggregatedResult> {
  // Update status to Aggregating
  await applicationRepo.updateStatus(applicationId, 'Aggregating')

  const runs = await applicationRepo.getScoringRuns(applicationId)
  const job = await jobRepo.getById(jobId)
  const config = job?.currentVersion

  const strategy = config?.aggregationStrategy || 'median'
  const scores = runs.map(r => r.overallScore)

  let finalScore: number
  if (strategy === 'median') {
    const sorted = [...scores].sort((a, b) => a - b)
    const mid = Math.floor(sorted.length / 2)
    finalScore = sorted.length % 2 !== 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
  } else if (strategy === 'mean') {
    finalScore = scores.reduce((a, b) => a + b, 0) / scores.length
  } else {
    // weighted - use mean as fallback
    finalScore = scores.reduce((a, b) => a + b, 0) / scores.length
  }

  const variance = Math.sqrt(
    scores.reduce((sum, s) => sum + Math.pow(s - finalScore, 2), 0) / scores.length
  )

  // Compute sub-score averages (fuzzy-match LLM category names to rubric names)
  const finalSubScores: Record<string, number> = {}
  if (config?.rubric) {
    const rubricNames = config.rubric.map(c => c.name)
    for (const cat of config.rubric) {
      const catScores = runs.map(r => {
        // Exact match first
        if (cat.name in r.subScores) return r.subScores[cat.name]
        // Fuzzy match for LLM-derived names that differ from rubric names
        const match = findBestRubricMatch(cat.name, Object.keys(r.subScores))
        return match ? r.subScores[match] : 0
      })
      finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
    }
  }

  const longlistThreshold = config?.longlistThreshold || 60
  const varianceThreshold = config?.varianceThreshold || 15

  // Aggregate must-have gate results across runs (majority failure excludes).
  const gatePassVotes = runs.filter(r => r.mustHaveResult?.passed === true).length
  const gateFailVotes = runs.filter(r => r.mustHaveResult?.passed === false).length
  const gateFailedByAggregation = gateFailVotes > gatePassVotes
  const hasGateVotes = gatePassVotes + gateFailVotes > 0

  let finalDecision: 'Eligible' | 'Excluded' | 'NeedsManualReview'
  if (gateFailedByAggregation) {
    finalDecision = 'Excluded'
  } else if (variance > varianceThreshold) {
    finalDecision = 'NeedsManualReview'
  } else if (finalScore >= longlistThreshold) {
    finalDecision = 'Eligible'
  } else {
    finalDecision = 'Excluded'
  }

  const result: AggregatedResult = {
    resultId: randomUUID(),
    applicationId,
    versionId: config?.versionId || 'unknown',
    finalScore,
    finalSubScores,
    confidence: 1 - (variance / 100),
    variance,
    finalDecision,
    rationaleText: gateFailedByAggregation
      ? `Excluded: eligibility gate failed by aggregated votes (passed: ${gatePassVotes}, failed: ${gateFailVotes}). Score: ${finalScore.toFixed(1)} (${strategy}, ${runs.length} run${runs.length > 1 ? 's' : ''}).`
      : hasGateVotes
        ? `Aggregated ${runs.length} scoring runs using ${strategy} strategy. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}. Eligibility votes: passed ${gatePassVotes}, failed ${gateFailVotes}.`
        : `Aggregated ${runs.length} scoring runs using ${strategy} strategy. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}.`,
    recommendationsText: runs[0]?.improvementRecommendations?.join('; ') || '',
    allRuns: runs,
    createdAt: new Date().toISOString(),
  }

  await applicationRepo.setAggregatedResult(result)

  // Update application status and scores
  const newStatus = finalDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed'
  await applicationRepo.updateStatus(applicationId, newStatus, {
    finalScore,
    finalDecision,
    variance,
    flagged: finalDecision === 'NeedsManualReview',
  })

  return result
}
