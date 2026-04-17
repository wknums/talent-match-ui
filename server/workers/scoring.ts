import { randomUUID } from 'node:crypto'
import { applicationRepo, jobRepo, promptRepo } from '../storage/repos/index.js'
import { getAwrAuthHeaders } from '../services/awr-auth.js'
import type { ScoringRun, ApplicationDocument, ScoringPrompt, AggregatedResult } from '../../src/types/index.js'

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

/**
 * Extract JSON from an LLM response that may be wrapped in markdown code fences
 * or contain preamble/postamble text around a JSON object.
 */
function extractJson(text: string): string {
  // Strip markdown code fences: ```json ... ``` or ``` ... ```
  const fenceMatch = text.match(/```(?:json)?\s*\n?([\s\S]*?)\n?```/)
  if (fenceMatch) return fenceMatch[1].trim()

  // Try to find a JSON object in the response
  const braceStart = text.indexOf('{')
  const braceEnd = text.lastIndexOf('}')
  if (braceStart !== -1 && braceEnd > braceStart) {
    return text.slice(braceStart, braceEnd + 1)
  }

  return text.trim()
}

interface LlmScoringResponse {
  eligibility_gate?: {
    passed: boolean
    missing_criteria: string[]
    details: Record<string, boolean>
  }
  rubric_scores?: Array<{
    category?: string
    name?: string
    score?: number
    score_0_to_5?: number
    weighted_score?: number
    evidence?: string
    key_evidence?: string[]
    concerns_or_gaps?: string
    section?: string
    confidence?: number
    weight?: number
  }>
  composite_score?: number
  notes?: string
  improvement_recommendations?: string[]
  overall_recommendation?: string
  overall_weighted_score_percentage?: number
  overall_score?: number
  total_score?: number
  criteria?: LlmScoringResponse['rubric_scores']
  category_scores?: LlmScoringResponse['rubric_scores']
  scores?: LlmScoringResponse['rubric_scores']
  recommendations?: string[]
  improvement_tips?: string[]
  areas_for_improvement?: string[]
}

interface CombinedEngineResponse {
  runs: Array<LlmScoringResponse & { runIndex: number }>
  aggregated: {
    final_score: number
    variance: number
    confidence: number
    final_decision: string
    consolidated_rationale: string
    sub_score_averages: Record<string, number>
  }
}

interface TextCandidate {
  path: string
  key: string
  value: string
}

interface NumericCandidate {
  path: string
  key: string
  value: number
}

interface GateCandidate {
  path: string
  criterion: string
  passed: boolean
  evidence: string
  raw: Record<string, unknown>
}

interface CategoryCandidate {
  path: string
  name: string
  score: number
  evidence?: string
  raw: Record<string, unknown>
}

interface InterpretedRun {
  overallScore: number
  subScores: Record<string, number>
  evidenceCitations: ScoringRun['evidenceCitations']
  mustHaveResult: ScoringRun['mustHaveResult']
  rationale: string
  improvementRecommendations: string[]
  parserWarnings: string[]
  parserConfidence: number
}

interface InterpretedAggregatedResult {
  finalScore: number
  variance: number
  confidence: number
  finalDecision: string
  consolidatedRationale: string
  subScoreAverages: Record<string, number>
}

interface BuildScoringRunOptions {
  applicationId: string
  versionId: string
  runIndex: number
  promptVersionId: string
  durationMs: number
  rawResponseText?: string
  rawParsedResponse: Record<string, unknown>
  modelDeploymentId?: string
}

function normalizeKey(input: string): string {
  return input.toLowerCase().replace(/[_-]/g, ' ')
}

function findBestRubricMatch(rubricName: string, llmKeys: string[]): string | null {
  const normalizedRubric = normalizeKey(rubricName)

  const exact = llmKeys.find(key => normalizeKey(key) === normalizedRubric)
  if (exact) return exact

  const contained = llmKeys.find((key) => {
    const normalizedKey = normalizeKey(key)
    return normalizedRubric.includes(normalizedKey) || normalizedKey.includes(normalizedRubric)
  })
  if (contained) return contained

  const rubricWords = new Set(normalizedRubric.split(' ').filter(word => word.length > 2))
  let best: string | null = null
  let bestOverlap = 0

  for (const key of llmKeys) {
    const keyWords = normalizeKey(key).split(' ').filter(word => word.length > 2)
    const overlap = keyWords.filter(word => rubricWords.has(word)).length
    if (overlap > bestOverlap && keyWords.length > 0 && overlap / keyWords.length >= 0.4) {
      bestOverlap = overlap
      best = key
    }
  }

  return best
}

function remapRunCategoryScoresToRubric(
  run: ScoringRun,
  rubric: Array<{ name: string }> | undefined,
): ScoringRun {
  if (!rubric?.length) {
    return run
  }

  const scores = run.subScores ?? {}
  const llmKeys = Object.keys(scores)
  if (llmKeys.length === 0) {
    return run
  }

  const remapped: Record<string, number> = {}
  for (const category of rubric) {
    if (!category?.name) continue

    if (Object.hasOwn(scores, category.name)) {
      remapped[category.name] = scores[category.name]
      continue
    }

    const match = findBestRubricMatch(category.name, llmKeys)
    if (match && Object.hasOwn(scores, match)) {
      remapped[category.name] = scores[match]
    }
  }

  if (Object.keys(remapped).length === 0) {
    return run
  }

  return {
    ...run,
    subScores: remapped,
  }
}

function hasExplicitScoreKey(obj: Record<string, unknown>): boolean {
  return Object.keys(obj).some(k => {
    const lower = normalizeKey(k)
    return lower.includes('score') || lower === 'points' || lower === 'grade' || lower === 'rating'
  })
}

function extractNumericField(obj: Record<string, unknown>): number | null {
  // Prefer score-named keys over arbitrary first-number (avoids picking "weight: 0.60" over "score: 85")
  const scoreEntries = Object.entries(obj).filter(([key]) => {
    const lower = normalizeKey(key)
    return lower.includes('score') || lower === 'points' || lower === 'grade' || lower === 'rating'
  })
  for (const [, value] of scoreEntries) {
    if (typeof value === 'number' && Number.isFinite(value)) return value
    if (typeof value === 'string') {
      const parsed = Number(value)
      if (!Number.isNaN(parsed)) return parsed
    }
  }
  // Fallback: first numeric value
  for (const value of Object.values(obj)) {
    if (typeof value === 'number' && Number.isFinite(value)) return value
    if (typeof value === 'string') {
      const parsed = Number(value)
      if (!Number.isNaN(parsed)) return parsed
    }
  }
  return null
}

function extractStringField(obj: Record<string, unknown>): string {
  let best = ''
  for (const value of Object.values(obj)) {
    if (typeof value === 'string' && value.length > best.length) {
      best = value
    }
  }
  return best
}

function extractPreferredString(obj: Record<string, unknown>, keys: string[]): string {
  for (const key of keys) {
    const value = obj[key]
    if (typeof value === 'string' && value.trim().length > 0) {
      return value.trim()
    }
    if (Array.isArray(value)) {
      const joined = value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0).join('\n')
      if (joined.trim().length > 0) {
        return joined.trim()
      }
    }
  }

  return ''
}

function extractEvidenceField(obj: Record<string, unknown>): string {
  const explicitEvidence = extractPreferredString(obj, [
    'evidence',
    'key_evidence',
    'supporting_evidence',
    'citation',
    'citations',
    'justification',
    'reasons',
    'reasoning',
    'support',
  ])

  if (explicitEvidence) {
    return explicitEvidence
  }

  return extractStringField(obj)
}

function extractCategoryName(obj: Record<string, unknown>): string | null {
  const preferredKeys = ['category', 'name', 'label', 'criterion', 'title', 'dimension', 'competency']
  for (const key of preferredKeys) {
    const value = obj[key]
    if (typeof value === 'string' && value.length > 0 && value.length < 200) {
      return value
    }
  }

  return null
}

function looksLikeTotalScore(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('total') || lower.includes('overall') || lower.includes('composite')
    || lower.includes('final score') || lower.includes('weighted score') || lower.includes('aggregate score')
}

function looksLikeRecommendation(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('recommendation') || lower.includes('decision') || lower.includes('verdict')
}

function looksLikeNotes(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('summary') || lower.includes('notes') || lower.includes('rationale')
    || lower.includes('comment') || lower.includes('narrative')
}

function looksLikeEvidence(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('evidence') || lower.includes('justification') || lower.includes('reason')
    || lower.includes('support') || lower.includes('citation')
}

function looksLikeRecommendationArray(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('recommend') || lower.includes('improvement') || lower.includes('tip')
    || lower.includes('action') || lower.includes('next step') || lower.includes('advice')
}

function looksLikeEligibilityKey(name: string): boolean {
  const lower = normalizeKey(name)
  return lower.includes('eligibility') || lower.includes('must have') || lower.includes('gate')
    || lower.includes('requirement') || lower.includes('mandatory')
}

function looksLikeBooleanPassKey(name: string): boolean {
  const lower = normalizeKey(name)
  return lower === 'passed' || lower === 'met' || lower === 'eligible' || lower === 'satisfied'
    || lower === 'is pass' || lower === 'is met'
}

function looksLikePositiveRecommendation(text: string): boolean {
  const lower = text.toLowerCase()
  return lower.includes('recommend') || lower.includes('suitable') || lower.includes('eligible')
    || lower.includes('proceed') || lower.includes('interview') || lower.includes('pass')
    || lower.includes('approve') || lower.includes('shortlist')
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function collectSignals(
  node: unknown,
  path: string,
  textCandidates: TextCandidate[],
  numericCandidates: NumericCandidate[],
  gateCandidates: GateCandidate[],
  categoryCandidates: CategoryCandidate[],
): void {
  if (typeof node === 'string') {
    const key = path.split('.').pop() ?? path
    textCandidates.push({ path, key, value: node })
    return
  }

  if (typeof node === 'number' && Number.isFinite(node)) {
    const key = path.split('.').pop() ?? path
    numericCandidates.push({ path, key, value: node })
    return
  }

  if (Array.isArray(node)) {
    node.forEach((item, index) => collectSignals(item, `${path}[${index}]`, textCandidates, numericCandidates, gateCandidates, categoryCandidates))

    const objectItems = node.filter(isRecord)
    if (objectItems.length > 0) {
      const pathKey = path.split('.').pop() ?? path
      const gateLikeItems = objectItems.filter(item => {
        const keys = Object.keys(item)
        return keys.some(looksLikeBooleanPassKey) && keys.some(key => normalizeKey(key).includes('criterion') || normalizeKey(key).includes('requirement'))
      })

      if (gateLikeItems.length > 0 || looksLikeEligibilityKey(pathKey)) {
        for (const item of objectItems) {
          const criterion = typeof item.criterion === 'string'
            ? item.criterion
            : typeof item.requirement === 'string'
              ? item.requirement
              : typeof item.name === 'string'
                ? item.name
                : ''
          const booleanEntry = Object.entries(item).find(([key, value]) => looksLikeBooleanPassKey(key) && typeof value === 'boolean')
          if (!criterion || !booleanEntry) continue

          gateCandidates.push({
            path,
            criterion,
            passed: booleanEntry[1] as boolean,
            evidence: extractStringField(item),
            raw: item,
          })
        }
      }

      for (const item of objectItems) {
        const score = extractNumericField(item)
        const name = extractCategoryName(item)
        if (!name || score == null) continue
        const keys = Object.keys(item)
        // Only treat as gate-like if there's no explicit score field;
        // items with both passed/met AND a score are scoring categories, not pure gates
        const gateish = !hasExplicitScoreKey(item) && (
          keys.some(looksLikeBooleanPassKey) ||
          keys.some(key => normalizeKey(key).includes('criterion') && keys.some(looksLikeBooleanPassKey))
        )
        if (gateish) continue

        categoryCandidates.push({
          path,
          name,
          score,
          evidence: extractEvidenceField(item),
          raw: item,
        })
      }
    }
    return
  }

  if (isRecord(node)) {
    const pathKey = path.split('.').pop() ?? path
    const score = extractNumericField(node)
    const name = extractCategoryName(node)
    const keys = Object.keys(node)

    if (name && score != null && (!keys.some(looksLikeBooleanPassKey) || hasExplicitScoreKey(node))) {
      categoryCandidates.push({
        path,
        name,
        score,
        evidence: extractEvidenceField(node),
        raw: node,
      })
    }

    const criterion = typeof node.criterion === 'string'
      ? node.criterion
      : typeof node.requirement === 'string'
        ? node.requirement
        : typeof node.name === 'string' && keys.some(looksLikeBooleanPassKey)
          ? node.name
          : ''
    const booleanEntry = Object.entries(node).find(([key, value]) => looksLikeBooleanPassKey(key) && typeof value === 'boolean')
    if (criterion && booleanEntry) {
      gateCandidates.push({
        path,
        criterion,
        passed: booleanEntry[1] as boolean,
        evidence: extractStringField(node),
        raw: node,
      })
    }

    for (const [key, value] of Object.entries(node)) {
      collectSignals(value, path ? `${path}.${key}` : key, textCandidates, numericCandidates, gateCandidates, categoryCandidates)
    }

    if (score != null && !name && pathKey.length > 0 && !keys.some(looksLikeBooleanPassKey) && !looksLikeTotalScore(pathKey)) {
      categoryCandidates.push({
        path,
        name: pathKey,
        score,
        evidence: extractEvidenceField(node),
        raw: node,
      })
    }
  }
}

function extractNumericFromString(value: string): number | null {
  // Handle formula strings like "95*0.60 + 80*0.20 + ... = 90.3" by extracting the last number
  const equalsParts = value.split('=')
  if (equalsParts.length > 1) {
    const lastPart = equalsParts[equalsParts.length - 1].trim()
    const parsed = parseFloat(lastPart)
    if (Number.isFinite(parsed)) return parsed
  }
  // Try parsing the entire string as a number
  const direct = parseFloat(value.trim())
  if (Number.isFinite(direct)) return direct
  return null
}

function inferOverallScore(numericCandidates: NumericCandidate[], categoryScores: Record<string, number>, warnings: string[], textCandidates: TextCandidate[] = []): number {
  const preferred = numericCandidates.find(candidate => looksLikeTotalScore(candidate.key) || looksLikeTotalScore(candidate.path))
  if (preferred) return preferred.value

  // Check text candidates for overall score expressed as a formula or string number
  const textPreferred = textCandidates.find(candidate => looksLikeTotalScore(candidate.key) || looksLikeTotalScore(candidate.path))
  if (textPreferred) {
    const extracted = extractNumericFromString(textPreferred.value)
    if (extracted != null) return extracted
  }

  const values = Object.values(categoryScores)
  if (values.length > 0) {
    warnings.push('No explicit overall score found; using average of inferred category scores.')
    return values.reduce((sum, value) => sum + value, 0) / values.length
  }

  warnings.push('No overall score or category scores could be inferred from the parsed JSON.')
  return 0
}

function inferEligibility(
  parsed: Record<string, unknown>,
  gateCandidates: GateCandidate[],
  recommendation: string,
  warnings: string[],
): ScoringRun['mustHaveResult'] {
  const rawEligibility = parsed.eligibility_gate
  if (gateCandidates.length > 0) {
    return {
      passed: gateCandidates.every(candidate => candidate.passed),
      missingCriteria: gateCandidates.filter(candidate => !candidate.passed).map(candidate => candidate.criterion),
      details: {
        source: Array.isArray(rawEligibility) ? 'eligibility-array' : 'eligibility-inferred',
        entries: gateCandidates.map(candidate => ({
          criterion: candidate.criterion,
          passed: candidate.passed,
          evidence: candidate.evidence,
        })),
      },
    }
  }

  if (isRecord(rawEligibility)) {
    const passed = Object.entries(rawEligibility).find(([key, value]) => looksLikeBooleanPassKey(key) && typeof value === 'boolean')
    if (passed) {
      return {
        passed: passed[1] as boolean,
        missingCriteria: Array.isArray(rawEligibility.missing_criteria)
          ? rawEligibility.missing_criteria.filter((item): item is string => typeof item === 'string')
          : [],
        details: rawEligibility,
      }
    }
  }

  if (recommendation) {
    warnings.push('Eligibility gate not explicitly found; inferring pass/fail from recommendation text.')
    return {
      passed: looksLikePositiveRecommendation(recommendation),
      missingCriteria: [],
      details: { recommendation },
    }
  }

  warnings.push('No eligibility gate structure could be inferred from the parsed JSON.')
  return { passed: false, missingCriteria: [], details: {} }
}

function interpretRun(root: Record<string, unknown>): InterpretedRun {
  const warnings: string[] = []
  const textCandidates: TextCandidate[] = []
  const numericCandidates: NumericCandidate[] = []
  const gateCandidates: GateCandidate[] = []
  const categoryCandidates: CategoryCandidate[] = []

  collectSignals(root, '', textCandidates, numericCandidates, gateCandidates, categoryCandidates)

  const subScores: Record<string, number> = {}
  for (const candidate of categoryCandidates) {
    if (candidate.name in subScores) continue
    subScores[candidate.name] = candidate.score
  }
  if (Object.keys(subScores).length === 0) {
    for (const candidate of numericCandidates) {
      if (!looksLikeTotalScore(candidate.key) && !looksLikeEligibilityKey(candidate.path)) {
        subScores[candidate.key] = candidate.value
      }
    }
  }

  const evidenceCitations: ScoringRun['evidenceCitations'] = categoryCandidates
    .filter(candidate => candidate.evidence && candidate.evidence.trim().length > 0)
    .map(candidate => ({
      category: candidate.name,
      snippet: candidate.evidence ?? '',
      section: candidate.path,
      confidence: 0.5,
    }))

  const recommendationTexts = textCandidates
    .filter(candidate => looksLikeRecommendation(candidate.key) || looksLikeRecommendation(candidate.path))
    .map(candidate => candidate.value)
  const recommendation = recommendationTexts[0] ?? ''

  const rationaleTexts = textCandidates
    .filter(candidate => looksLikeNotes(candidate.key) || looksLikeNotes(candidate.path) || looksLikeEvidence(candidate.key))
    .map(candidate => candidate.value)

  const listRecommendations = textCandidates
    .filter(candidate => looksLikeRecommendationArray(candidate.key) || looksLikeRecommendationArray(candidate.path))
    .map(candidate => candidate.value)

  const mustHaveResult = inferEligibility(root, gateCandidates, recommendation, warnings)
  const overallScore = inferOverallScore(numericCandidates, subScores, warnings, textCandidates)

  if (Object.keys(subScores).length === 0) {
    warnings.push('No category-level scores were confidently extracted from the parsed JSON.')
  }

  let parserConfidence = 1
  parserConfidence -= Math.min(0.6, warnings.length * 0.15)
  if (Object.keys(subScores).length === 0) parserConfidence -= 0.1
  if (gateCandidates.length === 0 && Object.keys(mustHaveResult.details ?? {}).length === 0) parserConfidence -= 0.1
  parserConfidence = Math.max(0, Math.min(1, parserConfidence))

  return {
    overallScore,
    subScores,
    evidenceCitations,
    mustHaveResult,
    rationale: rationaleTexts[0] ?? recommendation,
    improvementRecommendations: listRecommendations.length > 0 ? listRecommendations : recommendationTexts.slice(1),
    parserWarnings: warnings,
    parserConfidence,
  }
}

export function interpretAggregatedResult(aggregated: Record<string, unknown>): InterpretedAggregatedResult {
  const warnings: string[] = []
  const numericCandidates: NumericCandidate[] = []
  const textCandidates: TextCandidate[] = []
  const gateCandidates: GateCandidate[] = []
  const categoryCandidates: CategoryCandidate[] = []

  collectSignals(aggregated, 'aggregated', textCandidates, numericCandidates, gateCandidates, categoryCandidates)

  const subScoreAverages: Record<string, number> = {}
  for (const candidate of categoryCandidates) {
    if (!looksLikeTotalScore(candidate.name)) {
      subScoreAverages[candidate.name] = candidate.score
    }
  }

  const finalScore = inferOverallScore(numericCandidates, subScoreAverages, warnings, textCandidates)
  const variance = numericCandidates.find(candidate => normalizeKey(candidate.key).includes('variance'))?.value ?? 0
  const confidence = numericCandidates.find(candidate => normalizeKey(candidate.key) === 'confidence' || normalizeKey(candidate.path).includes('confidence'))?.value ?? 0
  const decision = textCandidates.find(candidate => looksLikeRecommendation(candidate.key) || normalizeKey(candidate.key).includes('decision'))?.value ?? 'Excluded'
  const rationale = textCandidates.find(candidate => looksLikeNotes(candidate.key) || looksLikeNotes(candidate.path))?.value
    ?? `Aggregated result inferred from parsed response. Final score: ${finalScore.toFixed(1)}.`

  return {
    finalScore,
    variance,
    confidence,
    finalDecision: decision,
    consolidatedRationale: rationale,
    subScoreAverages,
  }
}

export function buildScoringRunFromParsedResponse(options: BuildScoringRunOptions): ScoringRun {
  const interpreted = interpretRun(options.rawParsedResponse)

  return {
    runId: randomUUID(),
    applicationId: options.applicationId,
    versionId: options.versionId,
    runIndex: options.runIndex,
    modelDeploymentId: options.modelDeploymentId ?? 'passthrough-llm',
    promptVersionId: options.promptVersionId,
    overallScore: interpreted.overallScore,
    subScores: interpreted.subScores,
    mustHaveResult: interpreted.mustHaveResult,
    evidenceCitations: interpreted.evidenceCitations,
    rationale: interpreted.rationale,
    improvementRecommendations: interpreted.improvementRecommendations,
    createdAt: new Date().toISOString(),
    durationMs: options.durationMs,
    tokenUsage: undefined,
    status: 'Success',
    rawResponseText: options.rawResponseText,
    rawParsedResponse: options.rawParsedResponse,
    parserWarnings: interpreted.parserWarnings,
    parserConfidence: interpreted.parserConfidence,
  }
}

function parseSingleRun(
  parsed: LlmScoringResponse,
  applicationId: string,
  versionId: string,
  runIndex: number,
  promptId: string,
  durationMs: number,
  rawResponseText: string,
  rawParsedResponse?: Record<string, unknown>,
  rubric?: Array<{ name: string }>,
): ScoringRun {
  return remapRunCategoryScoresToRubric(buildScoringRunFromParsedResponse({
    applicationId,
    versionId,
    runIndex,
    promptVersionId: promptId,
    durationMs,
    rawResponseText,
    rawParsedResponse: (rawParsedResponse ?? parsed) as Record<string, unknown>,
    modelDeploymentId: 'passthrough-llm',
  }), rubric)
}
export interface ScoringResult {
  runs: ScoringRun[]
  aggregated?: {
    finalScore: number
    variance: number
    confidence: number
    finalDecision: string
    consolidatedRationale: string
    subScoreAverages: Record<string, number>
  }
}

/**
 * Run scoring via the passthrough API.
 * Sends a single request with `runs` parameter - the engine performs N scoring passes
 * and returns both individual results and an aggregated result (FR-009, FR-062).
 */
export async function runScoring(
  applicationId: string,
  jobId: string,
  runCount: number = 3,
  promptVersionId?: string,
  apiEndpoint?: string,
): Promise<ScoringResult> {
  const endpoint = apiEndpoint || AWR_SEQ_API_ENDPOINT
  if (!endpoint) {
    throw new Error('AWR_SEQ_API_ENDPOINT is not configured. Cannot perform LLM scoring.')
  }

  const job = await jobRepo.getById(jobId)
  if (!job) {
    throw new Error(`Job ${jobId} not found`)
  }
  const config = job.currentVersion

  const prompts = await promptRepo.getByJobId(jobId)
  const prompt = promptVersionId
    ? prompts.find(p => p.promptId === promptVersionId)
    : prompts.find(p => p.status === 'production-approved')
  if (!prompt) {
    throw new Error(`No scoring prompt found for job ${jobId} (promptVersionId: ${promptVersionId || 'production-approved'})`)
  }
  // TypeScript can't narrow across the async closure below, so bind to a definitely-assigned const
  const resolvedPromptRecord = prompt

  const documents = await applicationRepo.getDocuments(applicationId)
  if (!documents.length) {
    throw new Error(`No documents found for application ${applicationId}`)
  }
  const primaryDoc = documents[0]
  const rawContent = await applicationRepo.getBlob(primaryDoc.documentId)
  if (!rawContent) {
    throw new Error(`No document blob found for application ${applicationId}`)
  }

  const jobDescriptionText = job.jobDescription || job.title

  const resolvedPrompt = resolvedPromptRecord.promptText
    .replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)

  // Detect whether stored content is base64-encoded or raw text (e.g. synthetic test PDFs)
  const isBase64 = /^[A-Za-z0-9+/\r\n]+=*$/.test(rawContent.slice(0, 256)) && !rawContent.startsWith('%PDF')
  const docBuffer = isBase64 ? Buffer.from(rawContent, 'base64') : Buffer.from(rawContent)

  // Generate a batch ID for multi-run scoring (matches assess-ux.py pattern)
  const batchId = runCount > 1 ? randomUUID().replace(/-/g, '') : undefined

  // Helper: make a single API call and parse the response
  async function callEngineOnce(runIndex: number): Promise<{ run?: ScoringRun; multiResult?: ScoringResult }> {
    const callStart = Date.now()
    const formData = new FormData()
    formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
    // CV/PDF goes in cvFiles[] (not specFile) per the engine's OpenAPI spec
    formData.append('cvFiles[]', new Blob([docBuffer], { type: primaryDoc.mimeType }), primaryDoc.fileName)
    // Batch params for multi-run (matches assess-ux.py / api_client.py pattern)
    if (batchId) {
      formData.append('batchId', batchId)
      formData.append('runNumber', String(runIndex))
      formData.append('totalRuns', String(runCount))
    }

    const awrHeaders = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })
    const response = await fetch(`${endpoint}/assess/passthrough`, {
      method: 'POST',
      headers: awrHeaders,
      body: formData,
    })

    if (!response.ok) {
      const errorText = await response.text().catch(() => response.statusText)
      throw new Error(`Passthrough API error (${response.status}): ${errorText}`)
    }

    const responseText = await response.text()
    const durationMs = Date.now() - callStart

    console.log(`[Scoring] callEngineOnce(runIndex=${runIndex}, runCount=${runCount}) response length=${responseText.length}, duration=${durationMs}ms`)

    const jsonText = extractJson(responseText)
    let parsed: unknown

    try {
      parsed = JSON.parse(jsonText)
    } catch (parseError) {
      const message = parseError instanceof Error ? parseError.message : 'Unknown JSON parsing error'
      const failedRun: ScoringRun = {
        runId: randomUUID(), applicationId, versionId: config.versionId, runIndex,
        modelDeploymentId: 'passthrough-llm', promptVersionId: resolvedPromptRecord.promptId,
        overallScore: 0, subScores: {}, mustHaveResult: { passed: false, missingCriteria: [], details: {} },
        evidenceCitations: [], rationale: `LLM response was not valid JSON: ${message}`,
        improvementRecommendations: [], createdAt: new Date().toISOString(), durationMs,
        tokenUsage: undefined, status: 'Failed', rawResponseText: responseText,
        parserWarnings: ['Response could not be parsed as JSON.'], parserConfidence: 0,
      }
      await applicationRepo.addScoringRun(failedRun)
      return { run: failedRun }
    }

    try {
      // Check for combined response format (multi-run with aggregated result)
      const hasRuns = isRecord(parsed) && Array.isArray(parsed.runs)
      const hasAggregated = isRecord(parsed) && isRecord(parsed.aggregated)
      const topKeys = isRecord(parsed) ? Object.keys(parsed).slice(0, 10).join(', ') : typeof parsed
      console.log(`[Scoring] Response structure: hasRuns=${hasRuns}, hasAggregated=${hasAggregated}, topKeys=[${topKeys}]`)
      if (hasRuns && hasAggregated) {
        const combined = parsed as CombinedEngineResponse
        const runs: ScoringRun[] = []
        for (const engineRun of combined.runs) {
          const run = parseSingleRun(
            engineRun, applicationId, config.versionId, engineRun.runIndex,
            resolvedPromptRecord.promptId, durationMs, responseText, engineRun as unknown as Record<string, unknown>, config.rubric,
          )
          await applicationRepo.addScoringRun(run)
          runs.push(run)
        }
        return {
          multiResult: { runs, aggregated: interpretAggregatedResult(combined.aggregated as Record<string, unknown>) },
        }
      }

      // Single-run response
      const singleRun = parseSingleRun(
        (isRecord(parsed) ? parsed : {}) as LlmScoringResponse,
        applicationId, config.versionId, runIndex, resolvedPromptRecord.promptId,
        durationMs, responseText,
        (isRecord(parsed) ? parsed : { value: parsed }) as Record<string, unknown>,
        config.rubric,
      )
      await applicationRepo.addScoringRun(singleRun)
      return { run: singleRun }
    } catch (interpretationError) {
      const message = interpretationError instanceof Error ? interpretationError.message : 'Unknown interpretation error'
      const failedRun: ScoringRun = {
        runId: randomUUID(), applicationId, versionId: config.versionId, runIndex,
        modelDeploymentId: 'passthrough-llm', promptVersionId: resolvedPromptRecord.promptId,
        overallScore: 0, subScores: {}, mustHaveResult: { passed: false, missingCriteria: [], details: {} },
        evidenceCitations: [], rationale: `LLM response was valid JSON, but semantic interpretation failed: ${message}`,
        improvementRecommendations: [], createdAt: new Date().toISOString(), durationMs,
        tokenUsage: undefined, status: 'Failed', rawResponseText: responseText,
        rawParsedResponse: (isRecord(parsed) ? parsed : { value: parsed }) as Record<string, unknown>,
        parserWarnings: ['Parsed JSON successfully but could not interpret scoring concepts from it.'], parserConfidence: 0,
      }
      await applicationRepo.addScoringRun(failedRun)
      return { run: failedRun }
    }
  }

  const MAX_RETRIES_PER_RUN = 3

  async function callWithRetry(runIndex: number): Promise<{ run?: ScoringRun; multiResult?: ScoringResult }> {
    for (let attempt = 1; attempt <= MAX_RETRIES_PER_RUN; attempt++) {
      const result = await callEngineOnce(runIndex)
      if (result.multiResult || (result.run && result.run.status === 'Success')) {
        return result
      }
      if (attempt < MAX_RETRIES_PER_RUN) {
        console.log(`[Scoring] Run ${runIndex} failed (attempt ${attempt}/${MAX_RETRIES_PER_RUN}), retrying...`)
      } else {
        console.log(`[Scoring] Run ${runIndex} failed after ${MAX_RETRIES_PER_RUN} attempts, keeping failed result`)
        return result
      }
    }
    // Unreachable, but satisfies TS
    throw new Error('Unexpected retry loop exit')
  }

  try {
    console.log(`[Scoring] Starting ${runCount} run(s) for app ${applicationId}${batchId ? ` (batchId=${batchId})` : ''}`)
    const first = await callWithRetry(1)

    if (first.multiResult) {
      console.log(`[Scoring] Engine returned multi-run response with ${first.multiResult.runs.length} runs`)
      return first.multiResult
    }

    const allRuns: ScoringRun[] = [first.run!]
    for (let i = 2; i <= runCount; i++) {
      console.log(`[Scoring] Batch run ${i}/${runCount} (batchId=${batchId})...`)
      const result = await callWithRetry(i)
      if (result.multiResult) {
        return { runs: [...allRuns, ...result.multiResult.runs], aggregated: result.multiResult.aggregated }
      }
      allRuns.push(result.run!)
    }

    console.log(`[Scoring] Completed ${allRuns.length} run(s) for app ${applicationId}`)
    return { runs: allRuns }
  } catch (error) {
    throw error
  }
}