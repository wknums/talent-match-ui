import { readFileSync, existsSync } from 'node:fs'
import { resolve } from 'node:path'
import express from 'express'
import { getStorageProvider, initializeDatabase, isAzureSql } from './storage/db.js'
import { createLLMRouter } from './routes/llm.js'
import { createAuthRouter } from './routes/auth.js'
import { createAccessManagementRouter } from './routes/access-management.js'
import { createOrganizationsRouter } from './routes/organizations.js'
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
import { auditService, organizationAdminAudit } from './services/audit.js'
import { createAuthorizationResolver } from './services/authorization.js'
import { EntraAccessManagementService } from './services/entra-access-management.js'
import { OrganizationAdminService } from './services/organization-admin.js'
import { createEntraTokenValidator } from './services/entra-token.js'

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
const DIST_DIR = resolve(process.cwd(), 'dist')
const INDEX_HTML_PATH = resolve(DIST_DIR, 'index.html')
const AUTH_MODE = process.env.APP_AUTH_MODE === 'entra' ? 'entra' : 'simple'

function requireEnvironment(name: string): string {
  const value = process.env[name]?.trim()
  if (!value) throw new Error(`${name} is required when APP_AUTH_MODE=entra.`)
  return value
}

async function createAuthentication() {
  if (AUTH_MODE === 'simple') {
    const middleware = createAuthMiddleware({ mode: 'simple' })
    return { middleware, router: createAuthRouter({ mode: 'simple' }) }
  }

  const storage = await getStorageProvider()
  const authorizedClientIds = [
    process.env.ENTRA_STACK_A_CLIENT_ID,
    process.env.ENTRA_STACK_B_CLIENT_ID,
  ].map(value => value?.trim()).filter((value): value is string => Boolean(value))
  if (authorizedClientIds.length === 0) {
    throw new Error('At least one ENTRA_STACK_A_CLIENT_ID or ENTRA_STACK_B_CLIENT_ID is required when APP_AUTH_MODE=entra.')
  }

  const tokenValidator = createEntraTokenValidator({
    tenantId: requireEnvironment('AZURE_TENANT_ID'),
    audience: requireEnvironment('ENTRA_API_APP_CLIENT_ID'),
    authorizedClientIds,
    requiredScope: requireEnvironment('ENTRA_API_SCOPE'),
  })
  const authorizationResolver = createAuthorizationResolver(storage, {
    bootstrapObjectId: requireEnvironment('ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID'),
  })
  const middleware = createAuthMiddleware({
    mode: 'entra',
    tokenValidator,
    authorizationResolver,
    audit: auditService,
  })
  return {
    middleware,
    router: createAuthRouter({
      mode: 'entra',
      authMiddleware: middleware,
      audit: auditService,
      users: storage.users,
    }),
  }
}

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

  const authentication = await createAuthentication()

  const app = express()

  // Parse JSON bodies
  app.use(express.json({ limit: '10mb' }))

  // Public routes (no auth required)
  app.use('/api/auth', authentication.router)
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
  const authMiddleware = authentication.middleware

  // Protected routes
  const applicationsRouter = createApplicationsRouter()
  app.use('/api/users', authMiddleware, createUsersRouter({ mode: AUTH_MODE }))
  if (AUTH_MODE === 'entra') {
    const storage = await getStorageProvider()
    app.use(
      '/api/access-management',
      authMiddleware,
      createAccessManagementRouter(new EntraAccessManagementService(storage.accessManagement)),
    )
    app.use(
      '/api/organizations',
      authMiddleware,
      createOrganizationsRouter(new OrganizationAdminService(storage.organizations, organizationAdminAudit)),
    )
  }
  app.use('/api/jobs', authMiddleware, createJobsRouter())
  app.use('/api/jobs', authMiddleware, createPromptsRouter())
  app.use('/api', authMiddleware, applicationsRouter)
  app.use('/api/stats', authMiddleware, createStatsRouter())
  app.use('/api/audit', authMiddleware, createAuditRouter())
  app.use('/api/dlq', authMiddleware, createDLQRouter())

  // Serve the built SPA when available (production packaging places it in ./dist).
  if (existsSync(INDEX_HTML_PATH)) {
    app.use(express.static(DIST_DIR))
    app.get('*', (req, res, next) => {
      if (req.path.startsWith('/api') || req.path === '/healthz') {
        next()
        return
      }
      res.sendFile(INDEX_HTML_PATH)
    })
  }

  // Error handler (must be last)
  app.use(errorHandler)

  app.listen(PORT, () => {
    console.log(`Server running on http://localhost:${PORT}`)
    console.log(`Storage provider: ${process.env.STORAGE_PROVIDER || 'local'}`)
    console.log(`API mode: ${API_MODE}`)
  })

  // Platform-mode reconciler: only starts when AWR_PLATFORM_API_ENDPOINT is
  // configured (see detectScoringMode). Sequential deployments are a no-op.
  const { startPlatformReconciler } = await import('./workers/reconciler.js')
  if (isAzureSql) {
    // Defer start until DB init completes so the first tick has a working pool.
  } else {
    startPlatformReconciler()
  }

  if (isAzureSql) {
    console.log('[startup] Azure SQL detected; continuing startup while database initialization runs in the background')
    void initializeAppState()
      .then(() => {
        console.log('[startup] Background database initialization complete')
        startPlatformReconciler()
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
