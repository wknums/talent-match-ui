import { Router, json } from 'express'
import { DefaultAzureCredential } from '@azure/identity'

const credential = new DefaultAzureCredential()
const azureOpenAiScope = 'https://cognitiveservices.azure.com/.default'

export function createLLMRouter(): Router {
  const router = Router()
  router.use(json({ limit: '1mb' }))

  // POST /api/llm - proxy to OpenAI-compatible API
  router.post('/', async (req, res) => {
    const { prompt, model, jsonMode } = req.body

    if (!prompt) {
      return res.status(400).json({ error: 'prompt is required' })
    }

    const endpoint = process.env.AZURE_OPENAI_ENDPOINT?.replace(/\/$/, '')
    if (!endpoint) {
      return res.status(501).json({
        error: 'Azure OpenAI is not configured. Set AZURE_OPENAI_ENDPOINT.',
      })
    }

    try {
      const deployment = model || 'gpt-4o'
      const baseUrl = `${endpoint}/openai/deployments/${encodeURIComponent(deployment)}/chat/completions?api-version=2024-06-01`
      const accessToken = await credential.getToken(azureOpenAiScope)

      const headers: Record<string, string> = {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${accessToken.token}`,
      }

      const body: Record<string, unknown> = {
        model: deployment,
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
