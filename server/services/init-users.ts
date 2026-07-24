import { randomUUID } from 'node:crypto'
import { createHash } from 'node:crypto'
import { userRepo } from '../storage/repos/index.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export async function initializeUsers(): Promise<void> {
  const appAuthMode = (process.env.APP_AUTH_MODE || 'local').trim().toLowerCase()
  const count = await userRepo.count()

  if (appAuthMode === 'entra') {
    await ensureEntraAdminSeed()
    return
  }

  if (count === 0) {
    await userRepo.create({
      userId: 'admin-001',
      username: 'admin',
      role: 'admin',
      department: 'all',
      fullName: 'System Administrator',
      email: 'admin@company.com',
      createdAt: new Date().toISOString(),
      passwordHash: sha256('adm1n99'),
    })
    console.log('Default admin user seeded')
  }
}

async function ensureEntraAdminSeed(): Promise<void> {
  const adminUsername = process.env.ENTRA_ADMIN_USERNAME?.trim()
  if (!adminUsername) {
    console.warn('[startup] APP_AUTH_MODE=entra but ENTRA_ADMIN_USERNAME is not set; admin seed skipped')
    return
  }

  const adminFullName = process.env.ENTRA_ADMIN_FULL_NAME?.trim() || 'Entra Administrator'
  const adminEmail = process.env.ENTRA_ADMIN_EMAIL?.trim() || (adminUsername.includes('@') ? adminUsername : '')
  const now = new Date().toISOString()

  const users = await userRepo.getAll()
  const existing = users.find(u =>
    u.username.toLowerCase() === adminUsername.toLowerCase()
    || (!!adminEmail && !!u.email && u.email.toLowerCase() === adminEmail.toLowerCase()),
  )

  if (!existing) {
    await userRepo.create({
      userId: randomUUID(),
      username: adminUsername,
      role: 'admin',
      department: 'all',
      fullName: adminFullName,
      email: adminEmail || undefined,
      createdAt: now,
      passwordHash: sha256(randomUUID()),
      passwordResetRequired: false,
    })
    console.log(`[startup] Seeded Entra admin user: ${adminUsername}`)
    return
  }

  const update: Parameters<typeof userRepo.update>[1] = {}
  if (existing.role !== 'admin') update.role = 'admin'
  if (!existing.fullName && adminFullName) update.fullName = adminFullName
  if (!existing.email && adminEmail) update.email = adminEmail
  if (Object.keys(update).length > 0) {
    await userRepo.update(existing.userId, update)
    console.log(`[startup] Updated Entra admin user role/profile: ${existing.username}`)
  }
}
