import { promptRepo } from '../storage/repos/index.js'
import { getPromptProfileStatus, ScoringProfileMismatchError } from './scoring-profile.js'

export async function getProductionApprovedPromptId(
  jobId: string,
): Promise<string | null> {
  const approved = await promptRepo.getProductionApproved(jobId)
  if (!approved) return null
  const testRuns = await promptRepo.getTestRunsByPrompt(approved.promptId)
  const status = getPromptProfileStatus(approved, testRuns)
  if (!status.isMatch) {
    throw new ScoringProfileMismatchError(status.mismatchMessage)
  }
  return approved.promptId
}
