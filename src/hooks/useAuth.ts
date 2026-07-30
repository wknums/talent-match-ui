import {
  InteractionRequiredAuthError,
  InteractionStatus,
  type AccountInfo,
} from '@azure/msal-browser'
import { useMsal } from '@azure/msal-react'
import {
  createContext,
  createElement,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from 'react'

import {
  authorizationContextToUser,
  getCurrentUser,
  login,
  logout as simpleLogout,
} from '@/lib/auth'
import {
  authApi,
  configureApiAuthentication,
  TalentMatchApiError,
} from '@/lib/api'
import {
  appAuthMode,
  getEntraApiScope,
  getEntraLoginRequest,
} from '@/lib/msal-config'
import type {
  AuthenticationProvider,
  AuthorizationContext,
  User,
} from '@/types'

export type AuthStatus = 'loading' | 'signed-out' | 'authenticated' | 'access-denied' | 'provider-error'

export interface AuthViewError {
  kind: 'access-denied' | 'provider-error'
  message: string
  correlationId?: string
}

interface Credentials {
  username: string
  password: string
}

export interface AuthState {
  authMode: AuthenticationProvider
  status: AuthStatus
  user: User | null
  authorizationContext: AuthorizationContext | null
  error: AuthViewError | null
  signIn: (credentials?: Credentials) => Promise<User | null>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

const safeProviderError: AuthViewError = {
  kind: 'provider-error',
  message: 'Microsoft sign-in is temporarily unavailable. Please try again.',
}

function SimpleAuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<User | null>(null)

  useEffect(() => {
    let active = true
    void getCurrentUser().then((currentUser) => {
      if (!active) return
      setUser(currentUser)
      setStatus(currentUser ? 'authenticated' : 'signed-out')
    })
    return () => {
      active = false
    }
  }, [])

  const signIn = async (credentials?: Credentials) => {
    if (!credentials) return null
    setStatus('loading')
    const currentUser = await login(credentials.username, credentials.password)
    setUser(currentUser)
    setStatus(currentUser ? 'authenticated' : 'signed-out')
    return currentUser
  }

  const logout = async () => {
    await simpleLogout()
    setUser(null)
    setStatus('signed-out')
  }

  return createElement(AuthContext.Provider, {
    value: {
      authMode: 'simple',
      status,
      user,
      authorizationContext: null,
      error: null,
      signIn,
      logout,
    },
  }, children)
}

function selectAccount(accounts: AccountInfo[], activeAccount: AccountInfo | null): AccountInfo | null {
  return activeAccount ?? accounts[0] ?? null
}

function EntraAuthProvider({ children }: { children: ReactNode }) {
  const { instance, accounts, inProgress } = useMsal()
  const [status, setStatus] = useState<AuthStatus>('loading')
  const [user, setUser] = useState<User | null>(null)
  const [authorizationContext, setAuthorizationContext] = useState<AuthorizationContext | null>(null)
  const [error, setError] = useState<AuthViewError | null>(null)
  const account = selectAccount(accounts, instance.getActiveAccount())

  useEffect(() => {
    configureApiAuthentication({
      authMode: 'entra',
      apiOrigin: import.meta.env.VITE_TALENTMATCH_API_ORIGIN?.trim(),
      acquireAccessToken: async ({ forceRefresh }) => {
        const currentAccount = selectAccount(instance.getAllAccounts(), instance.getActiveAccount())
        if (!currentAccount) {
          throw new Error('No active Microsoft Entra account is available.')
        }

        try {
          const response = await instance.acquireTokenSilent({
            account: currentAccount,
            scopes: [getEntraApiScope()],
            forceRefresh,
          })
          return response.accessToken
        } catch (tokenError) {
          if (tokenError instanceof InteractionRequiredAuthError) {
            await instance.loginRedirect(getEntraLoginRequest())
          }
          throw tokenError
        }
      },
    })
  }, [instance, accounts])

  useEffect(() => {
    if (inProgress !== InteractionStatus.None) {
      setStatus('loading')
      return
    }

    if (!account) {
      setUser(null)
      setAuthorizationContext(null)
      setError(null)
      setStatus('signed-out')
      return
    }

    let active = true
    setStatus('loading')
    setError(null)

    void authApi.getAuthorizationContext()
      .then((context) => {
        if (!active) return
        setAuthorizationContext(context)
        setUser(authorizationContextToUser(context))
        setStatus('authenticated')
      })
      .catch((authError: unknown) => {
        if (!active) return
        setUser(null)
        setAuthorizationContext(null)
        if (authError instanceof TalentMatchApiError && authError.status === 403) {
          setError({
            kind: 'access-denied',
            message: authError.message,
            correlationId: authError.correlationId,
          })
          setStatus('access-denied')
          return
        }
        setError(safeProviderError)
        setStatus('provider-error')
      })

    return () => {
      active = false
    }
  }, [account, inProgress])

  const signIn = async () => {
    setError(null)
    setStatus('loading')
    try {
      await instance.loginRedirect(getEntraLoginRequest())
      return null
    } catch {
      setError(safeProviderError)
      setStatus('provider-error')
      return null
    }
  }

  const logout = async () => {
    setStatus('loading')
    try {
      await authApi.logout()
    } finally {
      await instance.logoutRedirect({
        account: account ?? undefined,
        postLogoutRedirectUri: window.location.origin,
      })
    }
  }

  return createElement(AuthContext.Provider, {
    value: {
      authMode: 'entra',
      status,
      user,
      authorizationContext,
      error,
      signIn,
      logout,
    },
  }, children)
}

export function AuthProvider({ children }: { children: ReactNode }) {
  return appAuthMode === 'entra'
    ? createElement(EntraAuthProvider, null, children)
    : createElement(SimpleAuthProvider, null, children)
}

export function useAuth(): AuthState {
  const auth = useContext(AuthContext)
  if (!auth) {
    throw new Error('useAuth must be used within AuthProvider.')
  }
  return auth
}