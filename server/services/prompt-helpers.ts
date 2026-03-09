import type { StorageProvider } from '../storage/types.js'
import { promptsKey } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'
import type { ScoringPrompt } from '../../src/types/index.js'

export async function getProductionApprovedPromptId(
  storage: StorageProvider,
  jobId: string,
): Promise<string | null> {
  const prompts = await getArray<ScoringPrompt>(storage, promptsKey(jobId))
  const approved = prompts.find(p => p.status === 'production-approved')
  return approved?.promptId || null
}
