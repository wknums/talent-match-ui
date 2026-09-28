// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { LoginForm } from '@/components/LoginForm'
import { ProtectedRoute } from '@/components/ProtectedRoute'
import { UserMenu } from '@/components/UserMenu'
import {
  configureAuthenticatedTransport,
  fetchWithAuthentication,
  resetAuthenticatedTransport,
} from '@/lib/api-real'
import { authorizationContextToUser } from '@/lib/auth'
import type { AuthorizationContext, User } from '@/types'

const entraUser: User = {
  userId: 'user-1',
  username: 'ada@example.com',
  role: 'admin',
  authenticationProvider: 'entra',
  fullName: 'Ada Lovelace',
  email: 'ada@example.com',
  createdAt: '2026-07-25T00:00:00.000Z',
}

afterEach(() => {
  cleanup()
  resetAuthenticatedTransport()
  vi.unstubAllGlobals()
})

describe('Entra authentication UI', () => {
  it('uses the explicit default for initial context without broadening scoped authorization', () => {
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
          { departmentId: 'department-authorized', departmentName: 'Authorized first' },
          { departmentId: 'department-default', departmentName: 'Explicit default' },
        ],
      }],
      authorizations: [{
        role: 'business_panel',
        roleLabel: 'Business Panel',
        organizationId: 'organization-1',
        departmentId: 'department-authorized',
        assignmentSource: 'delegated',
      }],
      tokenIssuedAt: '2026-07-31T10:00:00.000Z',
      refreshRequiredAt: '2026-07-31T10:15:00.000Z',
    }

    const user = authorizationContextToUser(context)

    expect(user.department).toBe('Explicit default')
    expect(user.role).toBe('business_panel')
    expect(context.authorizations).toHaveLength(1)
    expect(context.authorizations[0].departmentId).toBe('department-authorized')
  })

  it('offers Microsoft sign-in without password or reset controls', () => {
    const onEntraLogin = vi.fn()

    render(
      <LoginForm
        authMode="entra"
        onEntraLogin={onEntraLogin}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: /sign in with microsoft/i }))

    expect(onEntraLogin).toHaveBeenCalledOnce()
    expect(screen.queryByLabelText(/username/i)).toBeNull()
    expect(screen.queryByLabelText(/password/i)).toBeNull()
    expect(screen.queryByText(/password reset/i)).toBeNull()
  })

  it('renders a dedicated loading state', () => {
    render(
      <ProtectedRoute status="loading">
        <div>Protected content</div>
      </ProtectedRoute>,
    )

    expect(screen.getByRole('status').textContent).toMatch(/signing you in/i)
    expect(screen.queryByText('Protected content')).toBeNull()
  })

  it('renders a safe access-denied state with its correlation ID', () => {
    render(
      <ProtectedRoute
        status="access-denied"
        error={{
          kind: 'access-denied',
          message: 'Your account is signed in but has not been assigned access.',
          correlationId: 'correlation-123',
        }}
      >
        <div>Protected content</div>
      </ProtectedRoute>,
    )

    expect(screen.getByRole('heading', { name: /access denied/i })).toBeTruthy()
    expect(screen.getByText(/has not been assigned access/i)).toBeTruthy()
    expect(screen.getByText(/correlation-123/i)).toBeTruthy()
  })

  it('renders an identity-provider error that can start sign-in again', () => {
    const onSignIn = vi.fn()

    render(
      <ProtectedRoute
        status="provider-error"
        error={{ kind: 'provider-error', message: 'Microsoft sign-in is temporarily unavailable.' }}
        onSignIn={onSignIn}
      >
        <div>Protected content</div>
      </ProtectedRoute>,
    )

    fireEvent.click(screen.getByRole('button', { name: /try microsoft sign-in again/i }))

    expect(onSignIn).toHaveBeenCalledOnce()
  })

  it('keeps logout while hiding password workflows in the Entra user menu', async () => {
    const onLogout = vi.fn()

    render(
      <UserMenu
        authMode="entra"
        user={entraUser}
        onLogout={onLogout}
      />,
    )

    fireEvent.pointerDown(screen.getByRole('button', { name: /ada lovelace/i }), {
      button: 0,
      ctrlKey: false,
    })

    await waitFor(() => expect(screen.getByText('Sign Out')).toBeTruthy())
    expect(screen.queryByText('Change Password')).toBeNull()
    expect(screen.queryByText('Request Password Reset')).toBeNull()

    fireEvent.click(screen.getByText('Sign Out'))
    expect(onLogout).toHaveBeenCalledOnce()
  })

  it('offers Entra access management only to Entra administration roles', async () => {
    const onManageEntraAccess = vi.fn()
    const view = render(
      <UserMenu
        authMode="entra"
        user={{ ...entraUser, role: 'organization_admin' }}
        onManageEntraAccess={onManageEntraAccess}
        onLogout={vi.fn()}
      />,
    )

    fireEvent.pointerDown(view.getByRole('button', { name: /ada lovelace/i }), { button: 0 })
    fireEvent.click(await screen.findByText('Manage Entra Access'))
    expect(onManageEntraAccess).toHaveBeenCalledOnce()

    view.rerender(
      <UserMenu
        authMode="entra"
        user={{ ...entraUser, role: 'recruiter' }}
        onManageEntraAccess={onManageEntraAccess}
        onLogout={vi.fn()}
      />,
    )
    fireEvent.pointerDown(view.getByRole('button', { name: /ada lovelace/i }), { button: 0 })
    await waitFor(() => expect(screen.queryByText('Manage Entra Access')).toBeNull())
  })
})

describe('Entra authenticated transport', () => {
  it('attaches bearer tokens only to the configured TalentMatch API', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }))
    const acquireAccessToken = vi.fn(async () => 'access-token')
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({
      authMode: 'entra',
      apiOrigin: 'https://talent.example',
      acquireAccessToken,
    })

    await fetchWithAuthentication('https://talent.example/api/jobs')
    await fetchWithAuthentication('https://other.example/api/jobs')

    expect(acquireAccessToken).toHaveBeenCalledOnce()
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer access-token')
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).has('Authorization')).toBe(false)
  })

  it('forces one token refresh and retries one idempotent stale request', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ error: 'token_stale' }), {
        status: 401,
        headers: { 'Content-Type': 'application/json' },
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }))
    const acquireAccessToken = vi.fn(async ({ forceRefresh }: { forceRefresh: boolean }) => (
      forceRefresh ? 'fresh-token' : 'cached-token'
    ))
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({ authMode: 'entra', acquireAccessToken })

    const response = await fetchWithAuthentication('/api/jobs')

    expect(response.status).toBe(200)
    expect(acquireAccessToken).toHaveBeenNthCalledWith(1, { forceRefresh: false })
    expect(acquireAccessToken).toHaveBeenNthCalledWith(2, { forceRefresh: true })
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('does not replay a non-idempotent request after token_stale', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ error: 'token_stale' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    }))
    const acquireAccessToken = vi.fn(async () => 'cached-token')
    vi.stubGlobal('fetch', fetchMock)
    configureAuthenticatedTransport({ authMode: 'entra', acquireAccessToken })

    const response = await fetchWithAuthentication('/api/jobs', { method: 'POST' })

    expect(response.status).toBe(401)
    expect(fetchMock).toHaveBeenCalledOnce()
    expect(acquireAccessToken).toHaveBeenCalledOnce()
  })
})