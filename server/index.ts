import { readFileSync, existsSync } from 'node:fs'
import { resolve } from 'node:path'
import express from 'express'
import { initializeDatabase, isAzureSql } from './storage/db.js'
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
import { buildHealthReport } from './services/health.js'
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

async function initializeAppState() {
  await initializeDatabase()
  await initializeUsers()
}

async function main() {
  // Validate AWReason API auth configuration before starting
  try {
    validateAwrAuthConfig()
  } catch (err) {
    console.error(`[startup] AWReason auth config error: ${(err as Error).message}`)
    process.exit(1)
  }

  if (!isAzureSql) {
    await initializeAppState()
  }

  const app = express()

  // Parse JSON bodies
  app.use(express.json({ limit: '10mb' }))

  // Public routes (no auth required)
  app.use('/api/auth', createAuthRouter())
  app.use('/api/llm', createLLMRouter())

  // Config endpoint (public)
  app.get('/api/config', (_req, res) => {
    res.json({ apiMode: API_MODE })
  })

  // Health checks (public)
  const handleHealth = async (_req: express.Request, res: express.Response) => {
    const report = await buildHealthReport()
    res.status(report.status === 'ok' ? 200 : 503).json(report)
  }

  app.get('/api/health', (req, res, next) => {
    void handleHealth(req, res).catch(next)
  })

  app.get('/healthz', (req, res, next) => {
    void handleHealth(req, res).catch(next)
  })

  // Auth middleware for protected routes
  const authMiddleware = createAuthMiddleware()

  // Protected routes
  const applicationsRouter = createApplicationsRouter()
  app.use('/api/users', authMiddleware, createUsersRouter())
  app.use('/api/jobs', authMiddleware, createJobsRouter())
  app.use('/api/jobs', authMiddleware, createPromptsRouter())
  app.use('/api', authMiddleware, applicationsRouter)
  app.use('/api/stats', authMiddleware, createStatsRouter())
  app.use('/api/audit', authMiddleware, createAuditRouter())
  app.use('/api/dlq', authMiddleware, createDLQRouter())

  // Error handler (must be last)
  app.use(errorHandler)

  app.listen(PORT, () => {
    console.log(`Server running on http://localhost:${PORT}`)
    console.log(`Storage provider: ${process.env.STORAGE_PROVIDER || 'local'}`)
    console.log(`API mode: ${API_MODE}`)
  })

  if (isAzureSql) {
    console.log('[startup] Azure SQL detected; continuing startup while database initialization runs in the background')
    void initializeAppState()
      .then(() => {
        console.log('[startup] Background database initialization complete')
      })
      .catch((err) => {
        console.error(`[startup] Background database initialization failed: ${(err as Error).message}`)
      })
  }
}

main().catch((err) => {
  console.error('Failed to start server:', err)
  process.exit(1)
})
