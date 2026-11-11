const API_BASE = '/api'

/**
 * Client-side KV store that replaces window.spark.kv.
 * Calls the backend /api/kv endpoints.
 */
export const kv = {
  async keys(): Promise<string[]> {
    const res = await fetch(`${API_BASE}/kv/keys`)
    if (!res.ok) return []
    return res.json()
  },

  async get<T>(key: string): Promise<T | undefined> {
    const res = await fetch(`${API_BASE}/kv/${encodeURIComponent(key)}`)
    if (res.status === 404) return undefined
    if (!res.ok) throw new Error(`KV get failed: ${res.statusText}`)
    return res.json()
  },

  async set<T>(key: string, value: T): Promise<void> {
    const res = await fetch(`${API_BASE}/kv/${encodeURIComponent(key)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(value),
    })
    if (!res.ok) throw new Error(`KV set failed: ${res.statusText}`)
  },

  async delete(key: string): Promise<void> {
    const res = await fetch(`${API_BASE}/kv/${encodeURIComponent(key)}`, {
      method: 'DELETE',
    })
    if (!res.ok) throw new Error(`KV delete failed: ${res.statusText}`)
  },
}

/**
 * Client-side LLM proxy that replaces window.spark.llm.
 * Calls the backend /api/llm endpoint.
 */
export async function llm(
  prompt: string,
  model: string = 'gpt-4o',
  jsonMode: boolean = false
): Promise<string> {
  const res = await fetch(`${API_BASE}/llm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ prompt, model, jsonMode }),
  })
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText }))
    throw new Error(err.error || 'LLM request failed')
  }
  const data = await res.json()
  return data.response
}
