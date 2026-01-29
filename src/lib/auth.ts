import type { User, PasswordResetRequest } from '@/types'

const USERS_KEY = 'auth:users'
const CURRENT_USER_KEY = 'auth:current-user'
const RESET_REQUESTS_KEY = 'auth:reset-requests'

const DEFAULT_ADMIN = {
  userId: 'admin-001',
  username: 'admin',
  role: 'admin' as const,
  fullName: 'System Administrator',
  email: 'admin@company.com',
  createdAt: new Date().toISOString(),
}

const DEFAULT_PASSWORD = 'adm1n99'

async function hashPassword(password: string): Promise<string> {
  const encoder = new TextEncoder()
  const data = encoder.encode(password)
  const hashBuffer = await crypto.subtle.digest('SHA-256', data)
  const hashArray = Array.from(new Uint8Array(hashBuffer))
  return hashArray.map(b => b.toString(16).padStart(2, '0')).join('')
}

interface StoredUser extends User {
  passwordHash: string
}

export async function initializeAuth() {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY)
  
  if (!users || users.length === 0) {
    const adminPasswordHash = await hashPassword(DEFAULT_PASSWORD)
    const defaultAdmin: StoredUser = {
      ...DEFAULT_ADMIN,
      passwordHash: adminPasswordHash,
    }
    await spark.kv.set(USERS_KEY, [defaultAdmin])
  }
}

export async function login(username: string, password: string): Promise<User | null> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  const passwordHash = await hashPassword(password)
  
  const user = users.find(u => u.username === username && u.passwordHash === passwordHash)
  
  if (user) {
    const { passwordHash, ...userWithoutPassword } = user
    const updatedUser = {
      ...userWithoutPassword,
      lastLogin: new Date().toISOString(),
    }
    
    const updatedUsers = users.map(u => 
      u.userId === user.userId 
        ? { ...u, lastLogin: updatedUser.lastLogin } 
        : u
    )
    await spark.kv.set(USERS_KEY, updatedUsers)
    await spark.kv.set(CURRENT_USER_KEY, updatedUser)
    
    return updatedUser
  }
  
  return null
}

export async function logout() {
  await spark.kv.delete(CURRENT_USER_KEY)
}

export async function getCurrentUser(): Promise<User | null> {
  return await spark.kv.get<User>(CURRENT_USER_KEY) || null
}

export async function changePassword(userId: string, oldPassword: string, newPassword: string): Promise<boolean> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  const oldPasswordHash = await hashPassword(oldPassword)
  
  const userIndex = users.findIndex(u => u.userId === userId && u.passwordHash === oldPasswordHash)
  
  if (userIndex === -1) {
    return false
  }
  
  const newPasswordHash = await hashPassword(newPassword)
  users[userIndex].passwordHash = newPasswordHash
  users[userIndex].passwordResetRequired = false
  
  await spark.kv.set(USERS_KEY, users)
  return true
}

export async function createUser(user: Omit<User, 'userId' | 'createdAt' | 'lastLogin'>, password: string): Promise<User> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  
  const existingUser = users.find(u => u.username === user.username)
  if (existingUser) {
    throw new Error('Username already exists')
  }
  
  const passwordHash = await hashPassword(password)
  const newUser: StoredUser = {
    userId: `user-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
    ...user,
    passwordHash,
    createdAt: new Date().toISOString(),
  }
  
  await spark.kv.set(USERS_KEY, [...users, newUser])
  
  const { passwordHash: _, ...userWithoutPassword } = newUser
  return userWithoutPassword
}

export async function getAllUsers(): Promise<User[]> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  return users.map(({ passwordHash, ...user }) => user)
}

export async function resetUserPassword(adminUserId: string, targetUserId: string, newPassword: string): Promise<boolean> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  
  const admin = users.find(u => u.userId === adminUserId && u.role === 'admin')
  if (!admin) {
    return false
  }
  
  const userIndex = users.findIndex(u => u.userId === targetUserId)
  if (userIndex === -1) {
    return false
  }
  
  const newPasswordHash = await hashPassword(newPassword)
  users[userIndex].passwordHash = newPasswordHash
  users[userIndex].passwordResetRequired = false
  
  await spark.kv.set(USERS_KEY, users)
  return true
}

export async function requestPasswordReset(userId: string): Promise<void> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  const user = users.find(u => u.userId === userId)
  
  if (!user) {
    throw new Error('User not found')
  }
  
  const requests = await spark.kv.get<PasswordResetRequest[]>(RESET_REQUESTS_KEY) || []
  
  const newRequest: PasswordResetRequest = {
    requestId: `reset-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
    userId: user.userId,
    username: user.username,
    fullName: user.fullName,
    requestedAt: new Date().toISOString(),
    status: 'pending',
  }
  
  await spark.kv.set(RESET_REQUESTS_KEY, [...requests, newRequest])
}

export async function getPasswordResetRequests(): Promise<PasswordResetRequest[]> {
  return await spark.kv.get<PasswordResetRequest[]>(RESET_REQUESTS_KEY) || []
}

export async function resolvePasswordResetRequest(
  requestId: string, 
  adminUserId: string, 
  newPassword: string,
  status: 'completed' | 'rejected'
): Promise<boolean> {
  const requests = await spark.kv.get<PasswordResetRequest[]>(RESET_REQUESTS_KEY) || []
  const requestIndex = requests.findIndex(r => r.requestId === requestId)
  
  if (requestIndex === -1) {
    return false
  }
  
  if (status === 'completed') {
    const success = await resetUserPassword(adminUserId, requests[requestIndex].userId, newPassword)
    if (!success) {
      return false
    }
  }
  
  requests[requestIndex].status = status
  requests[requestIndex].resolvedAt = new Date().toISOString()
  requests[requestIndex].resolvedBy = adminUserId
  
  await spark.kv.set(RESET_REQUESTS_KEY, requests)
  return true
}

export async function deleteUser(adminUserId: string, targetUserId: string): Promise<boolean> {
  const users = await spark.kv.get<StoredUser[]>(USERS_KEY) || []
  
  const admin = users.find(u => u.userId === adminUserId && u.role === 'admin')
  if (!admin) {
    return false
  }
  
  if (targetUserId === adminUserId) {
    return false
  }
  
  const filteredUsers = users.filter(u => u.userId !== targetUserId)
  await spark.kv.set(USERS_KEY, filteredUsers)
  return true
}
