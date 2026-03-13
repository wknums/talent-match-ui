import { readFileSync, existsSync } from 'node:fs'
import { resolve } from 'node:path'
import express from 'express'
import { createStorageProvider } from './storage/factory.js'
import { createKVRouter } from './routes/kv.js'
import { createLLMRouter } from './routes/llm.js'
import { createAuthRouter } from './routes/auth.js'
import { createUsersRouter } from './routes/users.js'
import { createJobsRouter } from './routes/jobs.js'
import { createApplicationsRouter } from './routes/applications.js'
import { createStatsRouter } from './routes/stats.js'
import { createAuditRouter } from './routes/audit.js'
import { createDLQRouter } from './routes/dlq.js'
import { createPromptsRouter } from './routes/prompts.js'
import { createAuthMiddleware } from './middleware/auth.js'
import { errorHandler } from './middleware/error-handler.js'
import { initializeUsers } from './services/init-users.js'
import { validateAwrAuthConfig } from './services/awr-auth.js'

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
const API_MODE = (process.env.API_MODE || 'mock') as 'mock' | 'real'

async function main() {
  // Validate AWReason API auth configuration before starting
  try {
    validateAwrAuthConfig()
  } catch (err) {
    console.error(`[startup] AWReason auth config error: ${(err as Error).message}`)
    process.exit(1)
  }

  const storage = await createStorageProvider()

  // Seed default admin user if none exist
  await initializeUsers(storage)

  const app = express()

  // Parse JSON bodies
  app.use(express.json({ limit: '10mb' }))

  // Public routes (no auth required)
  app.use('/api/auth', createAuthRouter(storage))
  app.use('/api/kv', createKVRouter(storage))
  app.use('/api/llm', createLLMRouter())

  // Config endpoint (public)
  app.get('/api/config', (_req, res) => {
    res.json({ apiMode: API_MODE })
  })

  // Health check (public)
  app.get('/api/health', (_req, res) => {
    res.json({
      status: 'ok',
      storage: process.env.STORAGE_PROVIDER || 'local',
    })
  })

  // Auth middleware for protected routes
  const authMiddleware = createAuthMiddleware(storage)

  // Protected routes
  const applicationsRouter = createApplicationsRouter(storage)
  app.use('/api/users', authMiddleware, createUsersRouter(storage))
  app.use('/api/jobs', authMiddleware, createJobsRouter(storage))
  app.use('/api/jobs', authMiddleware, createPromptsRouter(storage))
  app.use('/api', authMiddleware, applicationsRouter)
  app.use('/api/stats', authMiddleware, createStatsRouter(storage))
  app.use('/api/audit', authMiddleware, createAuditRouter(storage))
  app.use('/api/dlq', authMiddleware, createDLQRouter(storage))

  // Error handler (must be last)
  app.use(errorHandler)

  app.listen(PORT, () => {
    console.log(`Server running on http://localhost:${PORT}`)
    console.log(`Storage provider: ${process.env.STORAGE_PROVIDER || 'local'}`)
    console.log(`API mode: ${API_MODE}`)
  })
}

main().catch((err) => {
  console.error('Failed to start server:', err)
  process.exit(1)
})
