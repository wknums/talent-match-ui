import type { organizationRepo } from './repos/organization-repo.js'
import type { roleAssignmentRepo } from './repos/role-assignment-repo.js'
import type { userRepo } from './repos/user-repo.js'
import type { accessManagementRepo } from './repos/access-management-repo.js'
import type {
  CanonicalApiError,
  EntraAccessUser,
  EntraAccessUserPage,
  NavigationAuditOutcome,
  NavigationAuditRequest,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
} from '../../src/types/index.js'

export type {
  CanonicalApiError,
  EntraAccessUser,
  EntraAccessUserPage,
  NavigationAuditOutcome,
  NavigationAuditRequest,
  PutOrganizationAccessRequest,
  UpdateEntraAccessUserRequest,
}

export interface EntraAccessSearchOptions {
  search?: string
  organizationId?: string
  status?: 'pending' | 'active' | 'disabled'
  cursor?: string
  limit: number
}

export interface AccessMutationContext {
  actorObjectId: string
  correlationId: string
}

export interface AccessMutationResult {
  aggregate: EntraAccessUser
  changed: boolean
}

export interface StorageQueryResult {
  // Repository rows are driver-shaped until mapped at the storage boundary.
   
  recordset: any[]
  rowsAffected?: number[]
}

export interface StorageRequest {
  input(name: string, type: unknown, value: unknown): StorageRequest
  query(sqlText: string): Promise<StorageQueryResult>
}

export interface StorageExecutor {
  request(): StorageRequest
}

export interface StorageProvider {
  users: typeof userRepo
  organizations: typeof organizationRepo
  roleAssignments: typeof roleAssignmentRepo
  accessManagement: typeof accessManagementRepo
}