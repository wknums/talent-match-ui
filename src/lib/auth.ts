import type { AuthorizationContext, PasswordResetRequest, User, UserRole } from '@/types'
import { realAPI } from '@/lib/api-real'

const rolePriority: UserRole[] = ['admin', 'organization_admin', 'recruiter', 'business_panel']

export function authorizationContextToUser(context: AuthorizationContext): User {
  const role = context.globalRole
    ?? rolePriority.find((candidate) => context.authorizations.some((authorization) => authorization.role === candidate))
    ?? 'business_panel'
  const department = context.memberships
    .map((membership) => membership.departments.find(
      (candidate) => candidate.departmentId === membership.defaultDepartmentId,
    ))
    .find((candidate) => candidate !== undefined)
    ?.departmentName

  return {
    userId: context.userId,
    username: context.username,
    role,
    authenticationProvider: 'entra',
    entraTenantId: context.tenantId,
    entraObjectId: context.objectId,
    isActive: true,
    authorizationVersion: context.authorizationVersion,
    department,
    fullName: context.fullName,
    email: context.email,
    createdAt: context.tokenIssuedAt,
    lastLogin: context.tokenIssuedAt,
    passwordResetRequired: false,
  }
}

export async function initializeAuth(): Promise<void> {
  // Auth bootstrap is now server-owned. Keep this as a no-op so callers do not
  // trigger legacy KV reads during startup.
}

export async function login(username: string, password: string): Promise<User | null> {
  try {
    return await realAPI.login(username, password)
  } catch {
    return null
  }
}

export async function logout(): Promise<void> {
  await realAPI.logout()
}

export async function getCurrentUser(): Promise<User | null> {
  return realAPI.getCurrentUser()
}

export async function changePassword(_userId: string, oldPassword: string, newPassword: string): Promise<boolean> {
  try {
    await realAPI.changePassword(oldPassword, newPassword)
    return true
  } catch {
    return false
  }
}

export async function createUser(user: Omit<User, 'userId' | 'createdAt' | 'lastLogin'>, password: string): Promise<User> {
  return realAPI.createUser(
    user.username,
    user.role,
    user.department ?? '',
    password,
    user.fullName,
    user.email,
  )
}

export async function getAllUsers(): Promise<User[]> {
  return realAPI.getAllUsers()
}

export async function resetUserPassword(_adminUserId: string, targetUserId: string, newPassword: string): Promise<boolean> {
  try {
    await realAPI.resetUserPassword(targetUserId, newPassword)
    return true
  } catch {
    return false
  }
}

export async function requestPasswordReset(_userId: string): Promise<void> {
  await realAPI.requestPasswordReset()
}

export async function requestPasswordResetFromLogin(username: string): Promise<void> {
  await realAPI.requestPasswordResetFromLogin(username, 'Requested from login screen')
}

export async function getPasswordResetRequests(): Promise<PasswordResetRequest[]> {
  return realAPI.getPasswordResetRequests()
}

export async function resolvePasswordResetRequest(
  requestId: string,
  _adminUserId: string,
  newPassword: string,
  status: 'completed' | 'rejected',
): Promise<boolean> {
  try {
    await realAPI.resolvePasswordResetRequest(
      requestId,
      status === 'completed' ? 'approve' : 'reject',
      status === 'completed' ? newPassword : undefined,
    )
    return true
  } catch {
    return false
  }
}

export async function deleteUser(_adminUserId: string, targetUserId: string): Promise<boolean> {
  try {
    await realAPI.deleteUser(targetUserId)
    return true
  } catch {
    return false
  }
}
