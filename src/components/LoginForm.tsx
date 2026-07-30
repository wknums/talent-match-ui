import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Lock, MicrosoftOutlookLogo } from '@phosphor-icons/react'
import { toast } from 'sonner'
import { requestPasswordResetFromLogin } from '@/lib/auth'
import { appAuthMode } from '@/lib/msal-config'
import type { AuthenticationProvider } from '@/types'

interface LoginFormProps {
  authMode?: AuthenticationProvider
  onLogin?: (username: string, password: string) => Promise<boolean>
  onEntraLogin?: () => void | Promise<unknown>
}

export function LoginForm({ authMode = appAuthMode, onLogin, onEntraLogin }: LoginFormProps) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const [showResetDialog, setShowResetDialog] = useState(false)
  const [isSubmittingReset, setIsSubmittingReset] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    
    if (!username || !password) {
      toast.error('Please enter both username and password')
      return
    }

    setIsLoading(true)
    try {
      const success = await onLogin?.(username, password)
      if (!success) {
        toast.error('Invalid username or password')
      }
    } catch (error) {
      toast.error('Login failed. Please try again.')
    } finally {
      setIsLoading(false)
    }
  }

  const handleOpenResetDialog = () => {
    if (!username.trim()) {
      toast.error('Enter your username before requesting a password reset')
      return
    }
    setShowResetDialog(true)
  }

  const handleConfirmReset = async () => {
    setIsSubmittingReset(true)
    try {
      await requestPasswordResetFromLogin(username.trim())
      toast.success('Password reset request submitted. An admin will review it shortly.')
      setShowResetDialog(false)
    } catch {
      toast.error('Failed to submit password reset request. Please try again.')
    } finally {
      setIsSubmittingReset(false)
    }
  }

  if (authMode === 'entra') {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-background via-muted/30 to-accent/10 px-4">
        <Card className="w-full max-w-md">
          <CardHeader className="space-y-4 text-center">
            <div className="flex justify-center">
              <div className="w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center">
                <Lock size={32} className="text-primary" weight="duotone" aria-hidden="true" />
              </div>
            </div>
            <div>
              <CardTitle className="text-2xl font-bold">Talent Matching Platform</CardTitle>
              <CardDescription className="mt-2">Use your organization account to continue</CardDescription>
            </div>
          </CardHeader>
          <CardContent>
            <Button
              type="button"
              className="w-full gap-2"
              disabled={isLoading}
              onClick={async () => {
                setIsLoading(true)
                try {
                  await onEntraLogin?.()
                } finally {
                  setIsLoading(false)
                }
              }}
            >
              <MicrosoftOutlookLogo size={18} aria-hidden="true" />
              {isLoading ? 'Opening Microsoft sign-in...' : 'Sign in with Microsoft'}
            </Button>
          </CardContent>
        </Card>
      </div>
    )
  }

  return (
    <>
      <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-background via-muted/30 to-accent/10 px-4">
        <Card className="w-full max-w-md">
          <CardHeader className="space-y-4 text-center">
            <div className="flex justify-center">
              <div className="w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center">
                <Lock size={32} className="text-primary" weight="duotone" />
              </div>
            </div>
            <div>
              <CardTitle className="text-2xl font-bold">Talent Matching Platform</CardTitle>
              <CardDescription className="mt-2">
                Sign in to access your recruiter dashboard
              </CardDescription>
            </div>
          </CardHeader>
          <CardContent>
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="username">Username</Label>
                <Input
                  id="username"
                  type="text"
                  placeholder="Enter your username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  disabled={isLoading}
                  autoComplete="username"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="password">Password</Label>
                <Input
                  id="password"
                  type="password"
                  placeholder="Enter your password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  disabled={isLoading}
                  autoComplete="current-password"
                />
              </div>
              <Button type="submit" className="w-full" disabled={isLoading}>
                {isLoading ? 'Signing in...' : 'Sign In'}
              </Button>
              <Button
                type="button"
                variant="ghost"
                className="w-full text-muted-foreground"
                disabled={isLoading}
                onClick={handleOpenResetDialog}
              >
                forgot password - request password reset
              </Button>
            </form>
          </CardContent>
        </Card>
      </div>

      <Dialog open={showResetDialog} onOpenChange={setShowResetDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Confirm Password Reset Request</DialogTitle>
            <DialogDescription>
              Submit a password reset request for username <strong>{username}</strong>? An admin will need to approve this request.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowResetDialog(false)} disabled={isSubmittingReset}>
              Cancel
            </Button>
            <Button onClick={handleConfirmReset} disabled={isSubmittingReset}>
              {isSubmittingReset ? 'Submitting...' : 'Confirm'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}
