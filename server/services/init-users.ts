import { createHash, randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import { AUTH_USERS } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'

interface StoredUser {
  userId: string
  username: string
  role: 'admin' | 'recruiter' | 'business_panel'
  department?: string
  fullName: string
  email?: string
  createdAt: string
  lastLogin?: string
  passwordHash: string
  passwordResetRequired?: boolean
}

function sha256(input: string): string {
  return createHash('sha256').update(input).digest('hex')
}

export async function initializeUsers(storage: StorageProvider): Promise<void> {
  const users = await getArray<StoredUser>(storage, AUTH_USERS)
  if (users.length === 0) {
    const admin: StoredUser = {
      userId: 'admin-001',
      username: 'admin',
      role: 'admin',
      department: 'all',
      fullName: 'System Administrator',
      email: 'admin@company.com',
      createdAt: new Date().toISOString(),
      passwordHash: sha256('adm1n99'),
    }
    await storage.set(AUTH_USERS, [admin])
    console.log('Default admin user seeded')
  }
}
