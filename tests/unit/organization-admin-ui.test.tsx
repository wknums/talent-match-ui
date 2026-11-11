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
      listOrganizations: vi.fn(),
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
const organization = {
  id: organizationId,
  name: 'Contoso',
  status: 'active' as const,
  departments: [{ id: departmentId, organizationId, name: 'Engineering', status: 'active' as const }],
}

function activateTab(name: RegExp) {
  fireEvent.focus(screen.getByRole('tab', { name }))
}

describe('Organization Admin dialog', () => {
  afterEach(cleanup)

  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(organizationAdminApi.listOrganizations).mockResolvedValue([organization])
    vi.mocked(organizationAdminApi.registerMembership).mockResolvedValue({
      userObjectId: objectId,
      organizationId,
      departmentIds: [departmentId],
      defaultDepartmentId: departmentId,
    })
    vi.mocked(organizationAdminApi.revokeRole).mockResolvedValue()
  })

  it('loads authorized organizations into a named selector', async () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)

    expect(await screen.findByRole('option', { name: 'Contoso' })).toBeTruthy()
    expect((screen.getByLabelText(/^organization$/i) as HTMLSelectElement).value).toBe(organizationId)
    expect(organizationAdminApi.listOrganizations).toHaveBeenCalledOnce()
  })

  it('creates an organization by name and selects it', async () => {
    const newOrganization = {
      id: '40000000-0000-4000-8000-000000000002',
      name: 'Fabrikam',
      status: 'active' as const,
      departments: [{
        id: '50000000-0000-4000-8000-000000000002',
        organizationId: '40000000-0000-4000-8000-000000000002',
        name: 'People',
        status: 'active' as const,
      }],
    }
    vi.mocked(organizationAdminApi.createOrganization).mockResolvedValue(newOrganization)
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    await screen.findByRole('option', { name: 'Contoso' })

    fireEvent.change(screen.getByLabelText(/organization name/i), { target: { value: 'Fabrikam' } })
    fireEvent.change(screen.getByLabelText(/initial department/i), { target: { value: 'People' } })
    fireEvent.click(screen.getByRole('button', { name: /create organization/i }))

    await waitFor(() => expect(organizationAdminApi.createOrganization).toHaveBeenCalledWith({
      name: 'Fabrikam',
      initialDepartmentName: 'People',
    }))
    expect(await screen.findByRole('option', { name: 'Fabrikam' })).toBeTruthy()
    expect((screen.getByLabelText(/^organization$/i) as HTMLSelectElement).value).toBe(newOrganization.id)
  })

  it('hides global organization creation from a scoped Organization Admin', async () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin={false} />)
    await screen.findByRole('option', { name: 'Contoso' })

    expect(screen.queryByRole('button', { name: /create organization/i })).toBeNull()
    activateTab(/delegated roles/i)
    expect(screen.queryByRole('option', { name: /organization admin/i })).toBeNull()
  })

  it('requires an explicit default from the requested memberships', async () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    await screen.findByRole('option', { name: 'Contoso' })
    activateTab(/memberships/i)
    fireEvent.change(screen.getByLabelText(/user object id/i), { target: { value: objectId } })
    fireEvent.change(screen.getByLabelText(/active department ids/i), { target: { value: departmentId } })
    fireEvent.click(screen.getByRole('button', { name: /apply membership and default/i }))

    expect(screen.getByRole('alert').textContent).toMatch(/explicit default/i)
    expect(organizationAdminApi.registerMembership).not.toHaveBeenCalled()
  })

  it('applies a membership with its explicit default', async () => {
    render(<OrganizationAdmin open onClose={vi.fn()} globalAdmin />)
    await screen.findByRole('option', { name: 'Contoso' })
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
    await screen.findByRole('option', { name: 'Contoso' })
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