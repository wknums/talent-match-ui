import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appRunsKey, appExtractionKey, promptsKey, JOBS } from '../storage/kv-keys.js'
import { getArray, pushToArray } from '../storage/kv-helpers.js'
import type { ScoringRun, Job, ExtractionArtifact, ScoringPrompt } from '../../src/types/index.js'

const AWR_SEQ_API_ENDPOINT = process.env.AWR_SEQ_API_ENDPOINT || ''

interface LlmScoringResponse {
  eligibility_gate: {
    passed: boolean
    missing_criteria: string[]
    details: Record<string, boolean>
  }
  rubric_scores: Array<{
    category: string
    score: number
    evidence: string
    section: string
    confidence: number
  }>
  composite_score: number
  notes: string
  improvement_recommendations: string[]
}

export async function runScoring(
  storage: StorageProvider,
  applicationId: string,
  jobId: string,
  runCount: number = 3,
  promptVersionId?: string,
): Promise<ScoringRun[]> {
  if (!AWR_SEQ_API_ENDPOINT) {
    throw new Error('AWR_SEQ_API_ENDPOINT is not configured. Cannot perform LLM scoring.')
  }

  const jobs = await getArray<Job>(storage, JOBS)
  const job = jobs.find(j => j.jobId === jobId)
  if (!job) {
    throw new Error(`Job ${jobId} not found`)
  }
  const config = job.currentVersion

  // Load the scoring prompt
  const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
  const prompt = promptVersionId
    ? prompts.find(p => p.promptId === promptVersionId)
    : prompts.find(p => p.status === 'production-approved')
  if (!prompt) {
    throw new Error(`No scoring prompt found for job ${jobId} (promptVersionId: ${promptVersionId || 'production-approved'})`)
  }

  // Load extracted CV text
  const extraction = await storage.get(appExtractionKey(applicationId)) as ExtractionArtifact | null
  if (!extraction?.markdown) {
    throw new Error(`No extraction artifact found for application ${applicationId}`)
  }

  const candidateText = extraction.markdown
  const jobDescriptionText = job.jobDescription || job.title

  const runs: ScoringRun[] = []

  for (let i = 0; i < runCount; i++) {
    const startTime = Date.now()

    // Resolve placeholders in prompt text
    const resolvedPrompt = prompt.promptText
      .replace(/\{\{JOB_SPEC_TEXT\}\}/g, jobDescriptionText)
      .replace(/\{\{CANDIDATE_CV_TEXT\}\}/g, candidateText)

    try {
      // Build FormData for passthrough API
      const formData = new FormData()
      formData.append('promptFile', new Blob([resolvedPrompt], { type: 'text/plain' }), 'score-prompt.md')
      formData.append('specFile', new Blob([candidateText], { type: 'text/plain' }), 'candidate-cv.md')

      const response = await fetch(`${AWR_SEQ_API_ENDPOINT}/assess/passthrough`, {
        method: 'POST',
        body: formData,
      })

      if (!response.ok) {
        const errorText = await response.text().catch(() => response.statusText)
        throw new Error(`Passthrough API error (${response.status}): ${errorText}`)
      }

      const responseText = await response.text()
      const durationMs = Date.now() - startTime

      let run: ScoringRun

      try {
        const parsed: LlmScoringResponse = JSON.parse(responseText)

        // Map LLM response to ScoringRun fields
        const subScores: Record<string, number> = {}
        const evidenceCitations = (parsed.rubric_scores || []).map(r => {
          subScores[r.category] = r.score
          return {
            category: r.category,
            snippet: r.evidence,
            section: r.section,
            confidence: r.confidence,
          }
        })

        run = {
          runId: randomUUID(),
          applicationId,
          versionId: config.versionId,
          runIndex: i + 1,
          modelDeploymentId: 'passthrough-llm',
          promptVersionId: prompt.promptId,
          overallScore: parsed.composite_score ?? 0,
          subScores,
          mustHaveResult: {
            passed: parsed.eligibility_gate?.passed ?? false,
            missingCriteria: parsed.eligibility_gate?.missing_criteria ?? [],
            details: parsed.eligibility_gate?.details ?? {},
          },
          evidenceCitations,
          rationale: parsed.notes || '',
          improvementRecommendations: parsed.improvement_recommendations || [],
          createdAt: new Date().toISOString(),
          durationMs,
          tokenUsage: undefined,
          status: 'Success',
        }
      } catch {
        // JSON parse failure — mark run as Failed with raw response
        run = {
          runId: randomUUID(),
          applicationId,
          versionId: config.versionId,
          runIndex: i + 1,
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
      }

      await pushToArray(storage, appRunsKey(applicationId), run)
      runs.push(run)
    } catch (error) {
      // HTTP/network errors — re-throw to let withRetry() in pipeline handle retries
      if (error instanceof Error && error.message.includes('Passthrough API error')) {
        throw error
      }
      // For other errors (network), also throw for retry
      throw error
    }
  }

  return runs
}
