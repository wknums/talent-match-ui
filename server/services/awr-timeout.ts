const DEFAULT_AWR_API_TIMEOUT_MS = 6 * 60 * 1000

function parseTimeout(rawValue: string | undefined): number {
  const parsed = Number.parseInt(rawValue ?? '', 10)
  if (!Number.isFinite(parsed) || parsed <= 0) return DEFAULT_AWR_API_TIMEOUT_MS
  return parsed
}

export const AWR_API_TIMEOUT_MS = parseTimeout(process.env.AWR_API_TIMEOUT_MS)

export function createAwrTimeoutSignal(timeoutMs?: number) {
  const controller = new AbortController()
  const effective = (typeof timeoutMs === 'number' && timeoutMs > 0) ? timeoutMs : AWR_API_TIMEOUT_MS
  const timeoutId = setTimeout(() => controller.abort(), effective)

  return {
    signal: controller.signal,
    dispose: () => clearTimeout(timeoutId),
  }
}
