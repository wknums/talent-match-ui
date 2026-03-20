import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appRunsKey, appDocumentsKey, appDocBlobKey, promptsKey, JOBS } from '../storage/kv-keys.js'
import { getArray, pushToArray } from '../storage/kv-helpers.js'
import { getAwrAuthHeaders } from '../services/awr-auth.js'
import type { ScoringRun, Job, ApplicationDocument, ScoringPrompt, AggregatedResult } from '../../src/types/index.js'

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
function parseSingleRun(parsed: LlmScoringResponse, applicationId: string, versionId: string, runIndex: number, promptId: string, durationMs: number): ScoringRun {
  const overallScore = parsed.composite_score
    ?? parsed.overall_weighted_score_percentage
    ?? parsed.overall_score
    ?? parsed.total_score
    ?? 0

  const rubricScores = parsed.rubric_scores
    ?? parsed.criteria
    ?? parsed.category_scores
    ?? parsed.scores
    ?? []

  const subScores: Record<string, number> = {}
  const evidenceCitations = rubricScores.map(r => {
    const category = r.category || r.name || ''
    const score = r.score ?? r.score_0_to_5 ?? r.weighted_score ?? 0
    subScores[category] = score
    const snippet = r.evidence ?? (r.key_evidence ? r.key_evidence.join('; ') : '')
    return { category, snippet, section: r.section || '', confidence: r.confidence ?? 0 }
  })

  let mustHaveResult: ScoringRun['mustHaveResult']
  if (parsed.eligibility_gate) {
    mustHaveResult = {
      passed: parsed.eligibility_gate.passed ?? false,
      missingCriteria: parsed.eligibility_gate.missing_criteria ?? [],
      details: parsed.eligibility_gate.details ?? {},
    }
  } else if (parsed.overall_recommendation) {
    const rec = (parsed.overall_recommendation as string).toLowerCase()
    mustHaveResult = {
      passed: rec.includes('suitable') || rec.includes('recommended') || rec.includes('eligible') || rec.includes('pass'),
      missingCriteria: [],
      details: {},
    }
  } else {
    mustHaveResult = { passed: false, missingCriteria: [], details: {} }
  }

  const improvementRecommendations = parsed.improvement_recommendations
    ?? parsed.recommendations ?? parsed.improvement_tips ?? parsed.areas_for_improvement ?? []

  return {
    runId: randomUUID(), applicationId, versionId, runIndex,
    modelDeploymentId: 'passthrough-llm', promptVersionId: promptId,
    overallScore, subScores, mustHaveResult, evidenceCitations,
    rationale: parsed.notes || '', improvementRecommendations,
    createdAt: new Date().toISOString(), durationMs,
    tokenUsage: undefined, status: 'Success',
  }
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
  storage: StorageProvider,
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

  const jobs = await getArray<Job>(storage, JOBS)
  const job = jobs.find(j => j.jobId === jobId)
  if (!job) {
    throw new Error(`Job ${jobId} not found`)
  }
  const config = job.currentVersion

  const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
  const prompt = promptVersionId
    ? prompts.find(p => p.promptId === promptVersionId)
    : prompts.find(p => p.status === 'production-approved')
  if (!prompt) {
    throw new Error(`No scoring prompt found for job ${jobId} (promptVersionId: ${promptVersionId || 'production-approved'})`)
  }

  const documents = await getArray<ApplicationDocument>(storage, appDocumentsKey(applicationId))
  if (!documents.length) {
    throw new Error(`No documents found for application ${applicationId}`)
  }
  const primaryDoc = documents[0]
  const rawContent = await storage.get<string>(appDocBlobKey(applicationId, primaryDoc.documentId))
  if (!rawContent) {
    throw new Error(`No document blob found for application ${applicationId}`)
  }

  const jobDescriptionText = job.jobDescription || job.title

  const resolvedPrompt = prompt.promptText
    .replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)

  const startTime = Date.now()
  try {
    // Build FormData with runs parameter (FR-009, FR-062)
    const docBuffer = Buffer.from(rawContent, 'base64')
    const formData = new FormData()
    formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
    formData.append('specFile', new Blob([docBuffer], { type: primaryDoc.mimeType }), primaryDoc.fileName)
    formData.append('runs', String(runCount))

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
    const durationMs = Date.now() - startTime

    try {
      const jsonText = extractJson(responseText)
      const parsed = JSON.parse(jsonText)

      // Check for combined response format (multi-run with aggregated result)
      if (parsed.runs && Array.isArray(parsed.runs) && parsed.aggregated) {
        const combined = parsed as CombinedEngineResponse
        const runs: ScoringRun[] = []

        for (const engineRun of combined.runs) {
          const run = parseSingleRun(engineRun, applicationId, config.versionId, engineRun.runIndex, prompt.promptId, durationMs)
          await pushToArray(storage, appRunsKey(applicationId), run)
          runs.push(run)
        }

        return {
          runs,
          aggregated: {
            finalScore: combined.aggregated.final_score,
            variance: combined.aggregated.variance,
            confidence: combined.aggregated.confidence,
            finalDecision: combined.aggregated.final_decision,
            consolidatedRationale: combined.aggregated.consolidated_rationale,
            subScoreAverages: combined.aggregated.sub_score_averages || {},
          },
        }
      }

      // Fallback: single-run response format (backward compatibility)
      const singleRun = parseSingleRun(parsed as LlmScoringResponse, applicationId, config.versionId, 1, prompt.promptId, durationMs)
      await pushToArray(storage, appRunsKey(applicationId), singleRun)
      return { runs: [singleRun] }
    } catch {
      // JSON parse failure - mark run as Failed with raw response
      const failedRun: ScoringRun = {
        runId: randomUUID(),
        applicationId,
        versionId: config.versionId,
        runIndex: 1,
        modelDeploymentId: 'passthrough-llm',
        promptVersionId: prompt.promptId,
        overallScore: 0,
        subScores: {},
        mustHaveResult: { passed: false, missingCriteria: [], details: {} },
        evidenceCitations: [],
        rationale: `Failed to parse LLM response as JSON. Raw response: ${responseText}`,
        improvementRecommendations: [],
        createdAt: new Date().toISOString(),
        durationMs: Date.now() - startTime,
        tokenUsage: undefined,
        status: 'Failed',
      }
      await pushToArray(storage, appRunsKey(applicationId), failedRun)
      return { runs: [failedRun] }
    }
  } catch (error) {
    if (error instanceof Error && error.message.includes('Passthrough API error')) {
      throw error
    }
    throw error
  }
}