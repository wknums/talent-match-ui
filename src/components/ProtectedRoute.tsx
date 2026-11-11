import type { ReactNode } from 'react'
import { ShieldWarning, SpinnerGap, WarningCircle } from '@phosphor-icons/react'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import type { AuthStatus, AuthViewError } from '@/hooks/useAuth'

interface ProtectedRouteProps {
  status: AuthStatus
  error?: AuthViewError | null
  signedOut?: ReactNode
  onSignIn?: () => void | Promise<unknown>
  onLogout?: () => void | Promise<unknown>
  children: ReactNode
}

function StateLayout({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-background via-muted/30 to-accent/10 px-4">
      <Card className="w-full max-w-md">
        {children}
      </Card>
    </div>
  )
}

export function ProtectedRoute({
  status,
  error,
  signedOut = null,
  onSignIn,
  onLogout,
  children,
}: ProtectedRouteProps) {
  if (status === 'loading') {
    return (
      <StateLayout>
        <CardContent className="flex flex-col items-center gap-4 py-12" role="status" aria-live="polite">
          <SpinnerGap size={32} className="animate-spin text-primary" aria-hidden="true" />
          <span className="text-muted-foreground">Signing you in...</span>
        </CardContent>
      </StateLayout>
    )
  }

  if (status === 'access-denied') {
    return (
      <StateLayout>
        <CardHeader className="text-center">
          <ShieldWarning size={40} className="mx-auto text-destructive" aria-hidden="true" />
          <CardTitle><h1>Access denied</h1></CardTitle>
          <CardDescription>{error?.message ?? 'Your account does not have access to Talent Matching.'}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4 text-center">
          {error?.correlationId && (
            <p className="text-xs text-muted-foreground">Reference: {error.correlationId}</p>
          )}
          {onLogout && <Button variant="outline" onClick={onLogout}>Sign out</Button>}
        </CardContent>
      </StateLayout>
    )
  }

  if (status === 'provider-error') {
    return (
      <StateLayout>
        <CardHeader className="text-center">
          <WarningCircle size={40} className="mx-auto text-destructive" aria-hidden="true" />
          <CardTitle><h1>Sign-in unavailable</h1></CardTitle>
          <CardDescription>{error?.message ?? 'Microsoft sign-in is temporarily unavailable.'}</CardDescription>
        </CardHeader>
        {onSignIn && (
          <CardContent>
            <Button className="w-full" onClick={onSignIn}>Try Microsoft sign-in again</Button>
          </CardContent>
        )}
      </StateLayout>
    )
  }

  if (status === 'signed-out') {
    return signedOut
  }

  return children
}