import { Router, json } from 'express'

export function createLLMRouter(): Router {
  const router = Router()
  router.use(json({ limit: '1mb' }))

  // POST /api/llm - proxy to OpenAI-compatible API
  router.post('/', async (req, res) => {
    const { prompt, model, jsonMode } = req.body

    if (!prompt) {
      return res.status(400).json({ error: 'prompt is required' })
    }

    const apiKey = process.env.OPENAI_API_KEY || process.env.AZURE_OPENAI_API_KEY
    if (!apiKey) {
      return res.status(501).json({
        error: 'No LLM API key configured. Set OPENAI_API_KEY or AZURE_OPENAI_API_KEY.',
      })
    }

    try {
      const baseUrl =
        process.env.AZURE_OPENAI_ENDPOINT
          ? `${process.env.AZURE_OPENAI_ENDPOINT}/openai/deployments/${model || 'gpt-4o'}/chat/completions?api-version=2024-06-01`
          : 'https://api.openai.com/v1/chat/completions'

      const headers: Record<string, string> = {
        'Content-Type': 'application/json',
      }

      if (process.env.AZURE_OPENAI_ENDPOINT) {
        headers['api-key'] = apiKey
      } else {
        headers['Authorization'] = `Bearer ${apiKey}`
      }

      const body: Record<string, unknown> = {
        model: model || 'gpt-4o',
        messages: [{ role: 'user', content: prompt }],
      }

      if (jsonMode) {
        body.response_format = { type: 'json_object' }
      }

      const response = await fetch(baseUrl, {
        method: 'POST',
        headers,
        body: JSON.stringify(body),
      })

      if (!response.ok) {
        const errorText = await response.text()
        console.error('LLM API error:', response.status, errorText)
        return res.status(response.status).json({ error: 'LLM API request failed' })
      }

      const data = await response.json() as {
        choices: Array<{ message: { content: string } }>
      }
      const content = data.choices?.[0]?.message?.content || ''
      res.json({ response: content })
    } catch (err) {
      console.error('LLM proxy error:', err)
      res.status(500).json({ error: 'Failed to call LLM' })
    }
  })

  return router
}
