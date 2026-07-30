import { createHash } from 'node:crypto'
import { userRepo } from '../storage/repos/index.js'

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export async function initializeUsers(): Promise<void> {
  if (process.env.APP_AUTH_MODE === 'entra') return

  const count = await userRepo.count()
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
