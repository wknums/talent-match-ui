import { readFileSync, existsSync } from 'node:fs'
import { resolve } from 'node:path'
import express from 'express'
import { createStorageProvider } from './storage/factory.js'
import { createKVRouter } from './routes/kv.js'
import { createLLMRouter } from './routes/llm.js'

// Load .env file
const envPath = resolve(process.cwd(), '.env')
if (existsSync(envPath)) {
  const envContent = readFileSync(envPath, 'utf-8')
  for (const line of envContent.split('\n')) {
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue
    const eqIdx = trimmed.indexOf('=')
    if (eqIdx === -1) continue
    const key = trimmed.slice(0, eqIdx).trim()
    const value = trimmed.slice(eqIdx + 1).trim()
    if (!process.env[key]) {
      process.env[key] = value
    }
  }
}

const PORT = parseInt(process.env.PORT || '3001', 10)

async function main() {
  const storage = await createStorageProvider()

  const app = express()

  // API routes
  app.use('/api/kv', createKVRouter(storage))
  app.use('/api/llm', createLLMRouter())

  // Health check
  app.get('/api/health', (_req, res) => {
    res.json({
      status: 'ok',
      storage: process.env.STORAGE_PROVIDER || 'local',
    })
  })

  app.listen(PORT, () => {
    console.log(`Server running on http://localhost:${PORT}`)
    console.log(`Storage provider: ${process.env.STORAGE_PROVIDER || 'local'}`)
  })
}

main().catch((err) => {
  console.error('Failed to start server:', err)
  process.exit(1)
})
