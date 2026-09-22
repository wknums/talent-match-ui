import type { PromptTestRun, ScoringPrompt } from '../src/types'

export function scoringPrompt(overrides: Partial<ScoringPrompt> = {}): ScoringPrompt {
  return {
    promptId: 'prompt-1',
    jobId: 'job-1',
    versionNumber: 4,
    promptText: 'Score {{JOB_SPEC_TEXT}}.',
    status: 'production-approved',
    createdAt: '2026-09-17T00:00:00Z',
    lastModifiedAt: '2026-09-17T00:00:00Z',
    author: 'admin',
    source: 'manual',
    modelId: 'model-current',
    reasoningLevel: 'high',
    approvedModelId: 'model-current',
    approvedReasoningLevel: 'high',
    approvedTestRunId: 'test-1',
    ...overrides,
  }
}

export function scoringTestRun(overrides: Partial<PromptTestRun> = {}): PromptTestRun {
  return {
    testRunId: 'test-1',
    jobId: 'job-1',
    promptId: 'prompt-1',
    status: 'approved',
    applicationIds: ['app-1'],
    createdAt: '2026-09-17T00:00:00Z',
    completedAt: '2026-09-17T00:01:00Z',
    modelId: 'model-current',
    reasoningLevel: 'high',
    approvedModelId: 'model-current',
    approvedReasoningLevel: 'high',
    ...overrides,
  }
}
