import { promptRepo } from '../storage/repos/index.js'

export async function getProductionApprovedPromptId(
  jobId: string,
): Promise<string | null> {
  const approved = await promptRepo.getProductionApproved(jobId)
  return approved?.promptId || null
}
