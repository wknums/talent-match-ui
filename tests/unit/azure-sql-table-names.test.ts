import { describe, expect, it, vi } from 'vitest'

vi.mock('../../server/storage/db.js', () => ({
  isAzureSql: true,
}))

import { lockedTable, T } from '../../server/storage/table-names.js'

describe('Azure SQL table references', () => {
  it('places aliases before locking hints', () => {
    expect(lockedTable('RoleAssignments', 'ra'))
      .toBe('[talentmatch].[RoleAssignments] ra WITH (UPDLOCK, HOLDLOCK)')
  })

  it('keeps unaliased table references schema-qualified', () => {
    expect(lockedTable('Users'))
      .toBe('[talentmatch].[Users] WITH (UPDLOCK, HOLDLOCK)')
    expect(T('Users')).toBe('[talentmatch].[Users]')
  })
})
