// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationAdmin } from '@/components/OrganizationAdmin'
import { organizationAdminApi, TalentMatchApiError } from '@/lib/api'

vi.mock('@/lib/api', async importOriginal => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return {
    ...actual,
    organizationAdminApi: {
      createOrganization: vi.fn(),
      createDepartment: vi.fn(),
      updateDepartment: vi.fn(),
      registerMembership: vi.fn(),
      grantRole: vi.fn(),
      revokeRole: vi.fn(),
    },
  }
})

const organizationId = '40000000-0000-4000-8000-000000000001'
const departmentId = '50000000-0000-4000-8000-000000000001'
const objectId = '60000000-0000-4000-8000-000000000001'

function activateTab(name: RegExp) {
  fireEvent.focus(screen.getByRole('tab', { name }))
}

describe('Organization Admin dialog', () => {
  afterEach(cleanup)

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(organizationAdminApi.registerMembership).mockResolvedValue({
      userObjectId: objectId,
      organizationId,
      departmentIds: [departmentId],
      defaultDepartmentId: departmentId,
    })
    vi.mocked(organizationAdminApi.revokeRole).mockResolvedValue()
  })

  it('hides global organization creation from a scoped Organization Admin', () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin={false} />)

    expect(screen.queryByRole('button', { name: /create organization/i })).toBeNull()
    activateTab(/delegated roles/i)
    expect(screen.queryByRole('option', { name: /organization admin/i })).toBeNull()
  })

  it('requires an explicit default from the requested memberships', () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    fireEvent.change(screen.getByLabelText(/organization id/i), { target: { value: organizationId } })
    activateTab(/memberships/i)
    fireEvent.change(screen.getByLabelText(/user object id/i), { target: { value: objectId } })
    fireEvent.change(screen.getByLabelText(/active department ids/i), { target: { value: departmentId } })
    fireEvent.click(screen.getByRole('button', { name: /apply membership and default/i }))

    expect(screen.getByRole('alert').textContent).toMatch(/explicit default/i)
    expect(organizationAdminApi.registerMembership).not.toHaveBeenCalled()
  })

  it('applies a membership with its explicit default', async () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    fireEvent.change(screen.getByLabelText(/organization id/i), { target: { value: organizationId } })
    activateTab(/memberships/i)
    fireEvent.change(screen.getByLabelText(/user object id/i), { target: { value: objectId } })
    fireEvent.change(screen.getByLabelText(/active department ids/i), { target: { value: departmentId } })
    fireEvent.change(screen.getByLabelText(/explicit default department/i), { target: { value: departmentId } })
    fireEvent.click(screen.getByRole('button', { name: /apply membership and default/i }))

    await waitFor(() => expect(organizationAdminApi.registerMembership).toHaveBeenCalledWith(
      organizationId,
      { userObjectId: objectId, departmentIds: [departmentId], defaultDepartmentId: departmentId },
    ))
    expect(screen.getByRole('status').textContent).toMatch(/explicit default updated/i)
  })

  it('confirms one targeted revocation and keeps a correlated failure visible', async () => {
    vi.mocked(organizationAdminApi.revokeRole).mockRejectedValue(
      new TalentMatchApiError('Assignment was not found.', 404, 'not_found', 'correlation-404'),
    )
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    fireEvent.change(screen.getByLabelText(/organization id/i), { target: { value: organizationId } })
    activateTab(/delegated roles/i)
    fireEvent.change(screen.getByLabelText(/assignment id/i), { target: { value: '70000000-0000-4000-8000-000000000001' } })
    fireEvent.click(screen.getByRole('button', { name: /revoke targeted role/i }))
    fireEvent.click(screen.getByRole('button', { name: /confirm change/i }))

    await waitFor(() => expect(organizationAdminApi.revokeRole).toHaveBeenCalledWith(
      organizationId,
      '70000000-0000-4000-8000-000000000001',
    ))
    expect(screen.getByRole('alert').textContent).toMatch(/correlation-404/i)
  })
})