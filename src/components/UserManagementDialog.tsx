import { useState, useEffect } from 'react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { UserPlus, Trash, Key, X, Check } from '@phosphor-icons/react'
import { toast } from 'sonner'
import type { User, PasswordResetRequest } from '@/types'
import { getAllUsers, createUser, deleteUser, resetUserPassword, getPasswordResetRequests, resolvePasswordResetRequest } from '@/lib/auth'

interface UserManagementDialogProps {
  open: boolean
  onClose: () => void
  currentUserId: string
}

export function UserManagementDialog({ open, onClose, currentUserId }: UserManagementDialogProps) {
  const [users, setUsers] = useState<User[]>([])
  const [resetRequests, setResetRequests] = useState<PasswordResetRequest[]>([])
  const [showCreateForm, setShowCreateForm] = useState(false)
  const [isLoading, setIsLoading] = useState(false)
  const [resetingUserId, setResetingUserId] = useState<string | null>(null)
  const [newResetPassword, setNewResetPassword] = useState('')

  const [newUser, setNewUser] = useState({
    username: '',
    fullName: '',
    email: '',
    department: '',
    role: 'recruiter' as 'admin' | 'recruiter',
    password: '',
  })

  useEffect(() => {
    if (open) {
      loadData()
    }
  }, [open])

  const loadData = async () => {
    try {
      const [usersData, requestsData] = await Promise.all([
        getAllUsers(),
        getPasswordResetRequests(),
      ])
      setUsers(usersData)
      setResetRequests(requestsData.filter(r => r.status === 'pending'))
    } catch (error) {
      toast.error('Failed to load user data')
    }
  }

  const handleCreateUser = async (e: React.FormEvent) => {
    e.preventDefault()

    if (!newUser.username || !newUser.fullName || !newUser.password) {
      toast.error('Please fill in all required fields')
      return
    }

    if (newUser.password.length < 6) {
      toast.error('Password must be at least 6 characters')
      return
    }

    setIsLoading(true)
    try {
      await createUser({
        username: newUser.username,
        fullName: newUser.fullName,
        email: newUser.email || undefined,
        department: newUser.department || undefined,
        role: newUser.role,
      }, newUser.password)

      toast.success('User created successfully')
      setNewUser({
        username: '',
        fullName: '',
        email: '',
        department: '',
        role: 'recruiter',
        password: '',
      })
      setShowCreateForm(false)
      await loadData()
    } catch (error: any) {
      toast.error(error.message || 'Failed to create user')
    } finally {
      setIsLoading(false)
    }
  }

  const handleDeleteUser = async (userId: string) => {
    if (!confirm('Are you sure you want to delete this user?')) {
      return
    }

    try {
      const success = await deleteUser(currentUserId, userId)
      if (success) {
        toast.success('User deleted successfully')
        await loadData()
      } else {
        toast.error('Failed to delete user')
      }
    } catch (error) {
      toast.error('Failed to delete user')
    }
  }

  const handleResetPassword = async (userId: string) => {
    setResetingUserId(userId)
    setNewResetPassword('')
  }

  const handleConfirmReset = async (userId: string) => {
    if (!newResetPassword) {
      toast.error('Please enter a new password')
      return
    }

    if (newResetPassword.length < 6) {
      toast.error('Password must be at least 6 characters')
      return
    }

    try {
      const success = await resetUserPassword(currentUserId, userId, newResetPassword)
      if (success) {
        toast.success('Password reset successfully')
        setResetingUserId(null)
        setNewResetPassword('')
        await loadData()
      } else {
        toast.error('Failed to reset password')
      }
    } catch (error) {
      toast.error('Failed to reset password')
    }
  }

  const handleResolveResetRequest = async (requestId: string, userId: string, approve: boolean) => {
    if (approve) {
      const password = prompt('Enter new password for user:')
      if (!password) return

      if (password.length < 6) {
        toast.error('Password must be at least 6 characters')
        return
      }

      try {
        const success = await resolvePasswordResetRequest(requestId, currentUserId, password, 'completed')
        if (success) {
          toast.success('Password reset completed')
          await loadData()
        } else {
          toast.error('Failed to reset password')
        }
      } catch (error) {
        toast.error('Failed to reset password')
      }
    } else {
      try {
        const success = await resolvePasswordResetRequest(requestId, currentUserId, '', 'rejected')
        if (success) {
          toast.success('Reset request rejected')
          await loadData()
        } else {
          toast.error('Failed to reject request')
        }
      } catch (error) {
        toast.error('Failed to reject request')
      }
    }
  }

  return (
    <Dialog open={open} onOpenChange={onClose}>
      <DialogContent className="max-w-[96rem] max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>User Management</DialogTitle>
        </DialogHeader>

        <div className="space-y-6">
          {resetRequests.length > 0 && (
            <div className="space-y-3">
              <h3 className="text-sm font-medium">Pending Password Reset Requests</h3>
              <div className="border rounded-lg overflow-hidden">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>User</TableHead>
                      <TableHead>Requested</TableHead>
                      <TableHead className="text-right">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {resetRequests.map((request) => (
                      <TableRow key={request.requestId}>
                        <TableCell>
                          <div>
                            <div className="font-medium">{request.fullName}</div>
                            <div className="text-sm text-muted-foreground">@{request.username}</div>
                          </div>
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground">
                          {new Date(request.requestedAt).toLocaleString()}
                        </TableCell>
                        <TableCell className="text-right">
                          <div className="flex gap-2 justify-end">
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => handleResolveResetRequest(request.requestId, request.userId, true)}
                            >
                              <Check size={16} />
                              Approve
                            </Button>
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => handleResolveResetRequest(request.requestId, request.userId, false)}
                            >
                              <X size={16} />
                              Reject
                            </Button>
                          </div>
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            </div>
          )}

          <div className="flex justify-between items-center">
            <h3 className="text-sm font-medium">Users</h3>
            <Button onClick={() => setShowCreateForm(!showCreateForm)}>
              <UserPlus size={16} />
              Add User
            </Button>
          </div>

          {showCreateForm && (
            <form onSubmit={handleCreateUser} className="border rounded-lg p-4 space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label htmlFor="new-username">Username *</Label>
                  <Input
                    id="new-username"
                    value={newUser.username}
                    onChange={(e) => setNewUser({ ...newUser, username: e.target.value })}
                    disabled={isLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="new-fullname">Full Name *</Label>
                  <Input
                    id="new-fullname"
                    value={newUser.fullName}
                    onChange={(e) => setNewUser({ ...newUser, fullName: e.target.value })}
                    disabled={isLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="new-email">Email</Label>
                  <Input
                    id="new-email"
                    type="email"
                    value={newUser.email}
                    onChange={(e) => setNewUser({ ...newUser, email: e.target.value })}
                    disabled={isLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="new-department">Department</Label>
                  <Input
                    id="new-department"
                    value={newUser.department}
                    onChange={(e) => setNewUser({ ...newUser, department: e.target.value })}
                    disabled={isLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="new-role">Role</Label>
                  <Select
                    value={newUser.role}
                    onValueChange={(value: 'admin' | 'recruiter') => setNewUser({ ...newUser, role: value })}
                    disabled={isLoading}
                  >
                    <SelectTrigger id="new-role">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="recruiter">Recruiter</SelectItem>
                      <SelectItem value="admin">Admin</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                <div className="space-y-2">
                  <Label htmlFor="new-password">Password *</Label>
                  <Input
                    id="new-password"
                    type="password"
                    value={newUser.password}
                    onChange={(e) => setNewUser({ ...newUser, password: e.target.value })}
                    disabled={isLoading}
                  />
                </div>
              </div>
              <div className="flex gap-3 justify-end">
                <Button type="button" variant="outline" onClick={() => setShowCreateForm(false)} disabled={isLoading}>
                  Cancel
                </Button>
                <Button type="submit" disabled={isLoading}>
                  {isLoading ? 'Creating...' : 'Create User'}
                </Button>
              </div>
            </form>
          )}

          <div className="border rounded-lg overflow-hidden">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>User</TableHead>
                  <TableHead>Role</TableHead>
                  <TableHead>Department</TableHead>
                  <TableHead>Last Login</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {users.map((user) => (
                  <TableRow key={user.userId}>
                    <TableCell>
                      <div>
                        <div className="font-medium">{user.fullName}</div>
                        <div className="text-sm text-muted-foreground">@{user.username}</div>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant={user.role === 'admin' ? 'default' : 'secondary'}>
                        {user.role}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {user.department || '—'}
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {user.lastLogin ? new Date(user.lastLogin).toLocaleDateString() : 'Never'}
                    </TableCell>
                    <TableCell className="text-right">
                      <div className="flex gap-2 justify-end">
                        {resetingUserId === user.userId ? (
                          <div className="flex gap-2 items-center">
                            <Input
                              type="password"
                              placeholder="New password"
                              value={newResetPassword}
                              onChange={(e) => setNewResetPassword(e.target.value)}
                              className="w-40"
                            />
                            <Button
                              size="sm"
                              onClick={() => handleConfirmReset(user.userId)}
                            >
                              <Check size={16} />
                            </Button>
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => setResetingUserId(null)}
                            >
                              <X size={16} />
                            </Button>
                          </div>
                        ) : (
                          <>
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => handleResetPassword(user.userId)}
                            >
                              <Key size={16} />
                              Reset Password
                            </Button>
                            {user.userId !== currentUserId && (
                              <Button
                                size="sm"
                                variant="outline"
                                onClick={() => handleDeleteUser(user.userId)}
                              >
                                <Trash size={16} />
                              </Button>
                            )}
                          </>
                        )}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
