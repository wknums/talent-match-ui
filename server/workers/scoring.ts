import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appRunsKey, JOBS } from '../storage/kv-keys.js'
import { getArray, pushToArray } from '../storage/kv-helpers.js'
import type { ScoringRun, Job, RubricCategory, MustHave } from '../../src/types/index.js'

export async function runScoring(
  storage: StorageProvider,
  applicationId: string,
  jobId: string,
  runCount: number = 3,
): Promise<ScoringRun[]> {
  const jobs = await getArray<Job>(storage, JOBS)
  const job = jobs.find(j => j.jobId === jobId)
  const config = job?.currentVersion
  const rubric = config?.rubric || []
  const mustHaves = config?.mustHaves || []

  const runs: ScoringRun[] = []

  for (let i = 0; i < runCount; i++) {
    const subScores: Record<string, number> = {}
    rubric.forEach((cat: RubricCategory) => {
      subScores[cat.name] = 50 + Math.random() * 50
    })

    const mustHaveDetails: Record<string, boolean> = {}
    mustHaves.forEach((mh: MustHave) => {
      mustHaveDetails[mh.criterion] = Math.random() > 0.15
    })

    const overallScore = rubric.length > 0
      ? rubric.reduce((sum: number, cat: RubricCategory) => sum + (subScores[cat.name] || 0) * cat.weight, 0)
      : 60 + Math.random() * 35

    const run: ScoringRun = {
      runId: randomUUID(),
      applicationId,
      versionId: config?.versionId || 'unknown',
      runIndex: i + 1,
      modelDeploymentId: 'gpt-4o-deployment-001',
      promptVersionId: 'prompt-v2.3',
      overallScore,
      subScores,
      mustHaveResult: {
        passed: Object.values(mustHaveDetails).every(v => v),
        missingCriteria: mustHaves.filter((mh: MustHave) => !mustHaveDetails[mh.criterion]).map((mh: MustHave) => mh.criterion),
        details: mustHaveDetails,
      },
      evidenceCitations: rubric.slice(0, 2).map((cat: RubricCategory) => ({
        category: cat.name,
        snippet: `Demonstrated strong ${cat.name.toLowerCase()} through documented experience.`,
        section: 'Resume',
        confidence: 0.8 + Math.random() * 0.2,
      })),
      rationale: `Scoring run ${i + 1}: Assessed across ${rubric.length} categories with overall score of ${overallScore.toFixed(1)}.`,
      improvementRecommendations: [
        'Consider adding more quantified achievements',
        'Highlight relevant certifications',
      ],
      createdAt: new Date().toISOString(),
      durationMs: 8000 + Math.random() * 12000,
      tokenUsage: {
        promptTokens: 2800 + Math.floor(Math.random() * 800),
        completionTokens: 600 + Math.floor(Math.random() * 400),
        totalTokens: 3400 + Math.floor(Math.random() * 1200),
      },
      status: 'Success',
    }

    await pushToArray(storage, appRunsKey(applicationId), run)
    runs.push(run)
  }

  return runs
}
