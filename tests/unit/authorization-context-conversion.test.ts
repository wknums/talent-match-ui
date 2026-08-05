import { describe, expect, it } from 'vitest'
import { authorizationContextToUser } from '../../src/lib/auth.js'
import type { AuthorizationContext } from '../../src/types/index.js'

const context: AuthorizationContext = {
  userId: 'user-1',
  tenantId: 'tenant-1',
  objectId: 'object-1',
  username: 'ada@example.com',
  fullName: 'Ada Lovelace',
  email: 'ada@example.com',
  globalRole: null,
  authorizationVersion: 7,
  memberships: [{
    organizationId: 'organization-1',
    organizationName: 'Analytical Engines',
    defaultDepartmentId: 'department-default',
    departments: [
      { departmentId: 'department-first', departmentName: 'First by array order' },
      { departmentId: 'department-default', departmentName: 'Explicit default' },
    ],
  }],
  authorizations: [{
    role: 'recruiter',
    roleLabel: 'Recruiter',
    organizationId: 'organization-1',
    departmentId: 'department-first',
    assignmentSource: 'delegated',
  }],
  tokenIssuedAt: '2026-07-31T10:00:00.000Z',
  refreshRequiredAt: '2026-07-31T10:15:00.000Z',
}

describe('authorization context conversion', () => {
  it('uses the explicit default instead of department or assignment array order', () => {
    const user = authorizationContextToUser(context)

    expect(user.department).toBe('Explicit default')
    expect(user.authorizationVersion).toBe(7)
  })
})
