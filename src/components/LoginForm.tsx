import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Lock } from '@phosphor-icons/react'
import { toast } from 'sonner'
import { requestPasswordResetFromLogin } from '@/lib/auth'
import type { AuthMode } from '@/lib/auth'

interface LoginFormProps {
  onLogin: (username: string, password: string) => Promise<boolean>
  onEntraLogin: () => Promise<boolean>
  authMode: AuthMode
}

export function LoginForm({ onLogin, onEntraLogin, authMode }: LoginFormProps) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const [showResetDialog, setShowResetDialog] = useState(false)
  const [isSubmittingReset, setIsSubmittingReset] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()

    if (authMode === 'entra') {
      await onEntraLogin()
      return
    }
    
    if (!username || !password) {
      toast.error('Please enter both username and password')
      return
    }

    setIsLoading(true)
    try {
      const success = await onLogin(username, password)
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
                <Label htmlFor="username">{authMode === 'entra' ? 'Entra Username (UPN)' : 'Username'}</Label>
                <Input
                  id="username"
                  type="text"
                  placeholder={authMode === 'entra' ? 'name@yourtenant.onmicrosoft.com' : 'Enter your username'}
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  disabled={isLoading}
                  autoComplete="username"
                />
              </div>
              {authMode === 'local' && (
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
              )}
              <Button type="submit" className="w-full" disabled={isLoading}>
                {isLoading ? 'Signing in...' : authMode === 'entra' ? 'Sign in with Microsoft Entra ID' : 'Sign In'}
              </Button>
              {authMode === 'local' && (
                <Button
                  type="button"
                  variant="ghost"
                  className="w-full text-muted-foreground"
                  disabled={isLoading}
                  onClick={handleOpenResetDialog}
                >
                  forgot password - request password reset
                </Button>
              )}
            </form>
          </CardContent>
        </Card>
      </div>

      <Dialog open={authMode === 'local' && showResetDialog} onOpenChange={setShowResetDialog}>
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
