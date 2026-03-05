import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { appRunsKey, appResultKey, jobApplicationsKey, JOBS } from '../storage/kv-keys.js'
import { getArray, setArray } from '../storage/kv-helpers.js'
import type { ScoringRun, AggregatedResult, Job, Application } from '../../src/types/index.js'

export async function runAggregation(
  storage: StorageProvider,
  applicationId: string,
  jobId: string,
): Promise<AggregatedResult> {
  // Update status to Aggregating
  const apps = await getArray<Application>(storage, jobApplicationsKey(jobId))
  const appIndex = apps.findIndex(a => a.applicationId === applicationId)
  if (appIndex !== -1) {
    apps[appIndex] = { ...apps[appIndex], status: 'Aggregating' }
    await setArray(storage, jobApplicationsKey(jobId), apps)
  }

  const runs = await getArray<ScoringRun>(storage, appRunsKey(applicationId))
  const jobs = await getArray<Job>(storage, JOBS)
  const job = jobs.find(j => j.jobId === jobId)
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

  // Compute sub-score averages
  const finalSubScores: Record<string, number> = {}
  if (config?.rubric) {
    for (const cat of config.rubric) {
      const catScores = runs.map(r => r.subScores[cat.name] || 0)
      finalSubScores[cat.name] = catScores.reduce((a, b) => a + b, 0) / catScores.length
    }
  }

  const longlistThreshold = config?.longlistThreshold || 60
  const varianceThreshold = config?.varianceThreshold || 15

  let finalDecision: 'Eligible' | 'Excluded' | 'NeedsManualReview'
  if (variance > varianceThreshold) {
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
    rationaleText: `Aggregated ${runs.length} scoring runs using ${strategy} strategy. Final score: ${finalScore.toFixed(1)}, Variance: ${variance.toFixed(2)}.`,
    recommendationsText: runs[0]?.improvementRecommendations?.join('; ') || '',
    allRuns: runs,
    createdAt: new Date().toISOString(),
  }

  await storage.set(appResultKey(applicationId), result)

  // Update application status and scores
  const updatedApps = await getArray<Application>(storage, jobApplicationsKey(jobId))
  const idx = updatedApps.findIndex(a => a.applicationId === applicationId)
  if (idx !== -1) {
    updatedApps[idx] = {
      ...updatedApps[idx],
      status: finalDecision === 'NeedsManualReview' ? 'NeedsManualReview' : 'Completed',
      finalScore,
      finalDecision,
      variance,
      flagged: finalDecision === 'NeedsManualReview',
    }
    await setArray(storage, jobApplicationsKey(jobId), updatedApps)
  }

  return result
}
