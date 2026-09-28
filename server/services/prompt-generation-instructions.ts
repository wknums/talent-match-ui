import { promptRepo } from '../storage/repos/index.js'

export const DEFAULT_SCORING_GENERATION_INSTRUCTION = `You are an expert at creating scoring prompts for candidate evaluation.
Given an approved job rubric and a target scoring JSON template derived from that rubric, create a structured scoring prompt that an AI model can use to evaluate candidate applications.
The prompt must evaluate every rubric category using its specified weight, check all must-have criteria, consider desired qualifications, score each category from 0-100 with three decimal places, cite candidate evidence, include improvement recommendations and all eligibility-gate details, extract candidate_name, and require complete valid JSON matching the supplied template exactly.
Return only the scoring prompt text, ready for use.`

export async function resolvePromptGenerationInstruction(jobId: string) {
  const jobInstruction = await promptRepo.getActiveInstruction(jobId)
  if (jobInstruction) return { instruction: jobInstruction, scope: 'job' as const }

  const systemInstruction = await promptRepo.getActiveInstruction()
  if (!systemInstruction) {
    throw new Error('No active system scoring prompt generation instruction is configured.')
  }
  return { instruction: systemInstruction, scope: 'system' as const }
}
