import type { organizationRepo } from './repos/organization-repo.js'
import type { roleAssignmentRepo } from './repos/role-assignment-repo.js'
import type { userRepo } from './repos/user-repo.js'

export interface StorageProvider {
  users: typeof userRepo
  organizations: typeof organizationRepo
  roleAssignments: typeof roleAssignmentRepo
}