import type { PromptProfileStatus, PromptTestRun, ScoringPrompt } from '../../src/types/index.js'

export interface ScoringProfile {
  modelId: string
  reasoningLevel: string
}

export class ScoringProfileMismatchError extends Error {
  readonly statusCode = 409
}

export function getCurrentScoringProfile(): ScoringProfile {
  return {
    modelId: (process.env.AWR_MODEL_ID || process.env.AWR_MODEL || 'passthrough-llm').trim(),
    reasoningLevel: (process.env.AWR_REASONING_LEVEL || process.env.AWR_REASONING || 'medium').trim(),
  }
}

export function scoringProfileMatches(
  profile: ScoringProfile,
  modelId?: string,
  reasoningLevel?: string,
): boolean {
  return profile.modelId === modelId && profile.reasoningLevel === reasoningLevel
}

export function addScoringProfile(formData: FormData, profile = getCurrentScoringProfile()): void {
  formData.append('reasoningModel', profile.modelId)
  formData.append('reasoningEffort', profile.reasoningLevel)
}

export function isExactProfileApprovedTest(
  prompt: ScoringPrompt,
  run: PromptTestRun,
): boolean {
  return run.promptId === prompt.promptId && run.jobId === prompt.jobId
    && run.status === 'approved'
    && scoringProfileMatches(
      { modelId: prompt.modelId ?? '', reasoningLevel: prompt.reasoningLevel ?? '' },
      run.modelId,
      run.reasoningLevel,
    )
    && scoringProfileMatches(
      { modelId: prompt.modelId ?? '', reasoningLevel: prompt.reasoningLevel ?? '' },
      run.approvedModelId,
      run.approvedReasoningLevel,
    )
}

export function getPromptProfileStatus(
  prompt: ScoringPrompt,
  testRuns: PromptTestRun[],
): PromptProfileStatus {
  const exactApproved = testRuns
    .filter(run => isExactProfileApprovedTest(prompt, run))
    .sort((left, right) => (right.completedAt || '').localeCompare(left.completedAt || ''))[0]
  const approvalEvidence = testRuns.find(run => run.testRunId === prompt.approvedTestRunId
    && isExactProfileApprovedTest(prompt, run))
  const selected = {
    modelId: prompt.modelId ?? '',
    reasoningLevel: prompt.reasoningLevel ?? '',
  }
  const isMatch = Boolean(selected.modelId && selected.reasoningLevel)
    && scoringProfileMatches(selected, prompt.approvedModelId, prompt.approvedReasoningLevel)
    && Boolean(approvalEvidence)

  return {
    promptId: prompt.promptId,
    modelId: prompt.modelId ?? '',
    reasoningLevel: prompt.reasoningLevel ?? '',
    currentModelId: selected.modelId,
    currentReasoningLevel: selected.reasoningLevel,
    isMatch,
    hasExactProfileApprovedTest: Boolean(exactApproved),
    approvedTestRunId: approvalEvidence?.testRunId,
    mismatchMessage: isMatch
      ? undefined
      : `Production scoring is blocked. Prompt v${prompt.versionNumber} is configured for `
        + `'${selected.modelId}' / '${selected.reasoningLevel}', but it has not been tested and `
        + 'approved for that exact profile. Create a new prompt version if the selection changes.',
  }
}

export function assertPromptProfileCurrent(prompt: ScoringPrompt): void {
  if (!prompt.modelId || !prompt.reasoningLevel) {
    throw new ScoringProfileMismatchError(
      `Prompt v${prompt.versionNumber} does not have a model and reasoning effort. `
      + 'Create a new prompt version before testing or scoring.',
    )
  }
}

export function roundScore3(value: number): number {
  return Math.round((value + Number.EPSILON) * 1000) / 1000
}
