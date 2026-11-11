import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  assertPromptProfileCurrent,
  getCurrentScoringProfile,
  getPromptProfileStatus,
  roundScore3,
} from '../../server/services/scoring-profile'
import { scoringPrompt as prompt, scoringTestRun as approvedTest } from '../scoring-governance-fixtures'

afterEach(() => {
  vi.unstubAllEnvs()
})

describe('scoring profile lifecycle', () => {
  it('reads the configured backend model and reasoning level', () => {
    vi.stubEnv('AWR_MODEL_ID', 'model-current')
    vi.stubEnv('AWR_REASONING_LEVEL', 'high')

    expect(getCurrentScoringProfile()).toEqual({
      modelId: 'model-current',
      reasoningLevel: 'high',
    })
  })

  it('keeps the prompt profile stable after an environment-only change', () => {
    vi.stubEnv('AWR_MODEL_ID', 'model-new')
    vi.stubEnv('AWR_REASONING_LEVEL', 'high')

    const status = getPromptProfileStatus(prompt(), [approvedTest()])
    expect(status.isMatch).toBe(true)
  })

  it('accepts only matching prompt, test, and approval snapshots', () => {
    vi.stubEnv('AWR_MODEL_ID', 'model-current')
    vi.stubEnv('AWR_REASONING_LEVEL', 'high')

    expect(getPromptProfileStatus(prompt(), [approvedTest()])).toMatchObject({
      isMatch: true,
      hasExactProfileApprovedTest: true,
      approvedTestRunId: 'test-1',
    })
  })

  it('normalizes candidate scores to three decimal places', () => {
    expect(roundScore3(91.23456)).toBe(91.235)
    expect(roundScore3(91.2344)).toBe(91.234)
  })

  it.each([
    { modelId: 'model-old' },
    { reasoningLevel: 'low' },
    { approvedReasoningLevel: 'low' },
    { promptId: 'another-prompt' },
    { jobId: 'another-job' },
    { testRunId: 'not-the-approved-test' },
  ])('rejects incompatible approval evidence: %j', overrides => {
    expect(getPromptProfileStatus(prompt(), [approvedTest(overrides)]).isMatch).toBe(false)
  })

  it('requires an explicit approval reference even when a matching test exists', () => {
    expect(getPromptProfileStatus(prompt({ approvedTestRunId: undefined }), [approvedTest()]).isMatch).toBe(false)
  })

  it('rejects legacy prompts without an explicit stored profile', () => {
    expect(() => assertPromptProfileCurrent(prompt({ reasoningLevel: '' }))).toThrow(expect.objectContaining({
      statusCode: 409,
      message: expect.stringContaining('does not have a model and reasoning effort'),
    }))
  })
})
