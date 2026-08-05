// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EntraAccessManagementDialog } from '@/components/EntraAccessManagementDialog'
import { accessManagementApi, TalentMatchApiError } from '@/lib/api'

vi.mock('@/lib/api', async importOriginal => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return {
    ...actual,
    accessManagementApi: {
      list: vi.fn(),
      get: vi.fn(),
      updateUser: vi.fn(),
      putOrganizationAccess: vi.fn(),
      revokeRoleAssignment: vi.fn(),
    },
  }
})

const user = {
  objectId: '20000000-0000-4000-8000-000000000001',
  username: 'target@example.com',
  fullName: 'Target User',
  email: 'target@example.com',
  isActive: true,
  authorizationVersion: 0,
  organizations: [],
}

describe('Entra access management dialog', () => {
  afterEach(cleanup)

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(accessManagementApi.list).mockResolvedValue({ items: [user], nextCursor: null })
    vi.mocked(accessManagementApi.get).mockResolvedValue(user)
    vi.mocked(accessManagementApi.putOrganizationAccess).mockResolvedValue({ ...user, authorizationVersion: 1 })
    vi.mocked(accessManagementApi.updateUser).mockResolvedValue({ ...user, isActive: false, authorizationVersion: 1 })
    vi.mocked(accessManagementApi.revokeRoleAssignment).mockResolvedValue({ ...user, authorizationVersion: 1 })
  })

  it('searches pending profiles and exposes an empty state', async () => {
    vi.mocked(accessManagementApi.list).mockResolvedValue({ items: [], nextCursor: null })
    render(<EntraAccessManagementDialog open onClose={vi.fn()} globalAdmin />)

    expect(screen.getByText(/loading access profiles/i)).toBeTruthy()
    await waitFor(() => expect(screen.getByText(/no entra profiles match/i)).toBeTruthy())
    expect(accessManagementApi.list).toHaveBeenCalledWith(expect.objectContaining({ limit: 50 }))
  })

  it('requires an explicit default before opening confirmation', async () => {
    render(<EntraAccessManagementDialog open onClose={vi.fn()} globalAdmin />)
    fireEvent.click(await screen.findByRole('button', { name: /target user/i }))
    fireEvent.change(await screen.findByLabelText(/organization id/i), { target: { value: '30000000-0000-4000-8000-000000000001' } })
    fireEvent.change(screen.getByLabelText(/department ids/i), { target: { value: '40000000-0000-4000-8000-000000000001' } })
    fireEvent.click(screen.getByRole('button', { name: /review organization access/i }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/explicit default/i)
    expect(accessManagementApi.putOrganizationAccess).not.toHaveBeenCalled()
  })

  it('confirms onboarding and shows the successful outcome', async () => {
    render(<EntraAccessManagementDialog open onClose={vi.fn()} globalAdmin />)
    fireEvent.click(await screen.findByRole('button', { name: /target user/i }))
    fireEvent.change(await screen.findByLabelText(/organization id/i), { target: { value: '30000000-0000-4000-8000-000000000001' } })
    fireEvent.change(screen.getByLabelText(/department ids/i), { target: { value: '40000000-0000-4000-8000-000000000001' } })
    fireEvent.change(screen.getByLabelText(/default department/i), { target: { value: '40000000-0000-4000-8000-000000000001' } })
    fireEvent.click(screen.getByRole('button', { name: /review organization access/i }))
    fireEvent.click(await screen.findByRole('button', { name: /confirm access change/i }))

    await waitFor(() => expect(accessManagementApi.putOrganizationAccess).toHaveBeenCalledOnce())
    expect(screen.getByRole('status').textContent).toMatch(/access updated/i)
  })

  it('keeps a version conflict visible with its correlation ID', async () => {
    vi.mocked(accessManagementApi.updateUser).mockRejectedValue(
      new TalentMatchApiError('Refresh and retry.', 409, 'version_conflict', 'correlation-409'),
    )
    render(<EntraAccessManagementDialog open onClose={vi.fn()} globalAdmin />)
    fireEvent.click(await screen.findByRole('button', { name: /target user/i }))
    fireEvent.click(await screen.findByRole('button', { name: /disable identity/i }))
    fireEvent.click(await screen.findByRole('button', { name: /confirm identity change/i }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/changed by another administrator/i)
    expect(screen.getByRole('alert').textContent).toMatch(/correlation-409/i)
  })

  it('targets one delegated assignment for revocation', async () => {
    const assigned = {
      ...user,
      authorizationVersion: 3,
      organizations: [{
        organizationId: '30000000-0000-4000-8000-000000000001',
        status: 'active' as const,
        departmentIds: ['40000000-0000-4000-8000-000000000001'],
        defaultDepartmentId: '40000000-0000-4000-8000-000000000001',
        roleAssignments: [{
          id: 'assignment-1', role: 'recruiter' as const,
          organizationId: '30000000-0000-4000-8000-000000000001',
          departmentId: '40000000-0000-4000-8000-000000000001',
          source: 'delegated' as const, status: 'active' as const,
        }],
      }],
    }
    vi.mocked(accessManagementApi.list).mockResolvedValue({ items: [assigned], nextCursor: null })
    vi.mocked(accessManagementApi.get).mockResolvedValue(assigned)
    render(<EntraAccessManagementDialog open onClose={vi.fn()} globalAdmin />)
    fireEvent.click(await screen.findByRole('button', { name: /target user/i }))
    fireEvent.click(await screen.findByRole('button', { name: /revoke recruiter/i }))
    fireEvent.click(await screen.findByRole('button', { name: /confirm role revocation/i }))

    await waitFor(() => expect(accessManagementApi.revokeRoleAssignment).toHaveBeenCalledWith(
      assigned.objectId,
      assigned.organizations[0].organizationId,
      'assignment-1',
      3,
    ))
  })
})