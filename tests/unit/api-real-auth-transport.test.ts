import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  configureAuthenticatedTransport,
  fetchWithAuthentication,
  realAPI,
  resetAuthenticatedTransport,
} from '@/lib/api-real'

describe('Entra API transport', () => {
  afterEach(() => {
    resetAuthenticatedTransport()
    vi.unstubAllGlobals()
  })

  it('refreshes a stale token exactly once for an idempotent request', async () => {
    const acquireAccessToken = vi
      .fn()
      .mockResolvedValueOnce('stale-token')
      .mockResolvedValueOnce('fresh-token')
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({
        error: 'token_stale',
        message: 'Refresh the access token.',
        correlationId: 'correlation-1',
      }), { status: 401, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response('[]', {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }))
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({ authMode: 'entra', acquireAccessToken })

    await expect(realAPI.getJobs()).resolves.toEqual([])

    expect(acquireAccessToken).toHaveBeenNthCalledWith(1, { forceRefresh: false })
    expect(acquireAccessToken).toHaveBeenNthCalledWith(2, { forceRefresh: true })
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(new Headers(fetchMock.mock.calls[0]?.[1]?.headers).get('Authorization')).toBe('Bearer stale-token')
    expect(new Headers(fetchMock.mock.calls[1]?.[1]?.headers).get('Authorization')).toBe('Bearer fresh-token')
  })

  it('does not retry a non-idempotent stale-token response', async () => {
    const acquireAccessToken = vi.fn().mockResolvedValue('stale-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      error: 'token_stale',
      message: 'Refresh the access token.',
      correlationId: 'correlation-2',
    }), { status: 401, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({ authMode: 'entra', acquireAccessToken })

    await expect(realAPI.logout()).rejects.toMatchObject({ errorCode: 'token_stale' })

    expect(acquireAccessToken).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenCalledOnce()
  })

  it('never attaches a token to an unconfigured cross-origin URL', async () => {
    const acquireAccessToken = vi.fn().mockResolvedValue('access-token')
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({ authMode: 'entra', acquireAccessToken })

    await fetchWithAuthentication('https://example.test/api/jobs')

    expect(acquireAccessToken).not.toHaveBeenCalled()
    expect(new Headers(fetchMock.mock.calls[0]?.[1]?.headers).has('Authorization')).toBe(false)
  })

  it('sends typed access-management filters and desired organization state', async () => {
    const fetchMock = vi.fn().mockImplementation(async () => new Response(JSON.stringify({
      objectId: 'target', organizations: [], authorizationVersion: 1,
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)

    await realAPI.listEntraAccessUsers({ search: 'A B', status: 'pending', limit: 25 })
    await realAPI.putEntraOrganizationAccess('target-id', 'organization-id', {
      expectedVersion: 0,
      profile: { username: 'target@example.com', fullName: 'Target', email: null },
      membership: { status: 'active', departmentIds: ['department-id'], defaultDepartmentId: 'department-id' },
      roleAssignments: [{ role: 'recruiter', departmentId: 'department-id' }],
    })

    expect(fetchMock.mock.calls[0][0]).toBe('/api/access-management/users?search=A+B&status=pending&limit=25')
    expect(fetchMock.mock.calls[1][0]).toBe('/api/access-management/users/target-id/organizations/organization-id')
    expect(fetchMock.mock.calls[1][1]).toMatchObject({ method: 'PUT' })
  })

  it('preserves canonical access-management conflict details', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      error: 'version_conflict',
      message: 'Authorization state changed. Refresh and retry.',
      correlationId: 'correlation-conflict',
    }), { status: 409, headers: { 'Content-Type': 'application/json' } })))

    await expect(realAPI.updateEntraAccessUser('target-id', { expectedVersion: 0, isActive: false }))
      .rejects.toMatchObject({
        status: 409,
        errorCode: 'version_conflict',
        correlationId: 'correlation-conflict',
      })
  })
})