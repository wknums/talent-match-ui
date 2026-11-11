import { useState } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
} from '@/components/DraggableResizableDialog'
import { DialogTitle, DialogDescription } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { toast } from 'sonner'

interface ChangePasswordDialogProps {
  open: boolean
  onClose: () => void
  onSuccess: () => void
  userId: string
}

export function ChangePasswordDialog({ open, onClose, onSuccess, userId }: ChangePasswordDialogProps) {
  const [oldPassword, setOldPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [isLoading, setIsLoading] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()

    if (!oldPassword || !newPassword || !confirmPassword) {
      toast.error('Please fill in all fields')
      return
    }

    if (newPassword !== confirmPassword) {
      toast.error('New passwords do not match')
      return
    }

    if (newPassword.length < 6) {
      toast.error('Password must be at least 6 characters')
      return
    }

    setIsLoading(true)
    try {
      const { changePassword } = await import('@/lib/auth')
      const success = await changePassword(userId, oldPassword, newPassword)
      
      if (success) {
        toast.success('Password changed successfully')
        setOldPassword('')
        setNewPassword('')
        setConfirmPassword('')
        onSuccess()
        onClose()
      } else {
        toast.error('Current password is incorrect')
      }
    } catch {
      toast.error('Failed to change password')
    } finally {
      setIsLoading(false)
    }
  }

  return (
    <DraggableResizableDialog
      open={open}
      onOpenChange={onClose}
      defaultWidth={500}
      defaultHeight={450}
      minWidth={400}
      minHeight={350}
    >
      <DraggableDialogHeader>
        <DialogTitle>Change Password</DialogTitle>
        <DialogDescription>
          Enter your current password and choose a new one
        </DialogDescription>
      </DraggableDialogHeader>
      <DraggableDialogBody className="px-6">
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="old-password">Current Password</Label>
            <Input
              id="old-password"
              type="password"
              value={oldPassword}
              onChange={(e) => setOldPassword(e.target.value)}
              disabled={isLoading}
              autoComplete="current-password"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="new-password">New Password</Label>
            <Input
              id="new-password"
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              disabled={isLoading}
              autoComplete="new-password"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="confirm-password">Confirm New Password</Label>
            <Input
              id="confirm-password"
              type="password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              disabled={isLoading}
              autoComplete="new-password"
            />
          </div>
          <div className="flex gap-3 justify-end">
            <Button type="button" variant="outline" onClick={onClose} disabled={isLoading}>
              Cancel
            </Button>
            <Button type="submit" disabled={isLoading}>
              {isLoading ? 'Changing...' : 'Change Password'}
            </Button>
          </div>
        </form>
      </DraggableDialogBody>
    </DraggableResizableDialog>
  )
}
