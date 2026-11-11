import { getAwrAuthHeaders } from './awr-auth.js'
import { createAwrTimeoutSignal } from './awr-timeout.js'
import type {
  ReasoningEffort,
  ReasoningModelsResponse,
} from '../../src/types/index.js'
import { getCurrentScoringProfile } from './scoring-profile.js'

const supportedEfforts = new Set<ReasoningEffort>(['low', 'medium', 'high'])

function isReasoningEffort(value: unknown): value is ReasoningEffort {
  return typeof value === 'string' && supportedEfforts.has(value as ReasoningEffort)
}

export async function getReasoningModels(): Promise<ReasoningModelsResponse> {
  const endpoint = process.env.AWR_SEQ_API_ENDPOINT?.replace(/\/+$/, '')
  if (!endpoint) {
    throw new Error('AWR_SEQ_API_ENDPOINT is not configured')
  }

  const headers = await getAwrAuthHeaders({ username: 'system', role: 'pipeline' })
  const timeout = createAwrTimeoutSignal()
  const response = await fetch(`${endpoint}/reasoning-models`, {
    headers,
    signal: timeout.signal,
  }).finally(() => timeout.dispose())

  if (!response.ok) {
    const detail = await response.text().catch(() => response.statusText)
    throw new Error(`Reasoning model discovery failed (${response.status}): ${detail}`)
  }

  const raw = await response.json() as Partial<ReasoningModelsResponse>
  const models = Array.isArray(raw.models)
    ? raw.models.filter(model =>
      model
      && typeof model.slot === 'string'
      && typeof model.deployment === 'string'
      && typeof model.isDefault === 'boolean')
    : []
  const efforts = Array.isArray(raw.supportedReasoningEfforts)
    ? raw.supportedReasoningEfforts.filter(isReasoningEffort)
    : []

  if (
    typeof raw.defaultModel !== 'string'
    || !isReasoningEffort(raw.defaultReasoningEffort)
    || models.length === 0
    || efforts.length === 0
  ) {
    throw new Error('Reasoning model discovery returned an invalid contract')
  }

  return {
    defaultModel: raw.defaultModel,
    defaultReasoningEffort: raw.defaultReasoningEffort,
    supportedReasoningEfforts: efforts,
    models,
  }
}

export async function assertSupportedReasoningProfile(
  modelId: unknown,
  reasoningLevel: unknown,
): Promise<{ modelId: string; reasoningLevel: ReasoningEffort }> {
  if (typeof modelId !== 'string' || !modelId.trim()) {
    throw new Error('modelId is required')
  }
  if (!isReasoningEffort(reasoningLevel)) {
    throw new Error('reasoningLevel must be low, medium, or high')
  }

  const catalog = await getReasoningModels()
  const normalizedModel = modelId.trim()
  if (!catalog.models.some(model => model.deployment === normalizedModel)) {
    throw new Error(`Model '${normalizedModel}' is not supported by the AWReason HTTP service`)
  }
  if (!catalog.supportedReasoningEfforts.includes(reasoningLevel)) {
    throw new Error(`Reasoning effort '${reasoningLevel}' is not supported by the AWReason HTTP service`)
  }

  return { modelId: normalizedModel, reasoningLevel }
}

export async function resolveSupportedReasoningProfile(
  modelId: unknown,
  reasoningLevel: unknown,
): Promise<{ modelId: string; reasoningLevel: ReasoningEffort }> {
  if (modelId === undefined && reasoningLevel === undefined) {
    const configured = getCurrentScoringProfile()
    if (!isReasoningEffort(configured.reasoningLevel)) {
      throw new Error('Configured reasoning level must be low, medium, or high')
    }
    return {
      modelId: configured.modelId,
      reasoningLevel: configured.reasoningLevel,
    }
  }
  if (modelId === undefined || reasoningLevel === undefined) {
    throw new Error('modelId and reasoningLevel must be provided together')
  }
  return assertSupportedReasoningProfile(modelId, reasoningLevel)
}
