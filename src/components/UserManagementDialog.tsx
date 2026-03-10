import { useState, useEffect } from 'react'
import {
  DraggableResizableDialog,
  DraggableDialogHeader,
  DraggableDialogBody,
} from '@/components/DraggableResizableDialog'
import { DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { UserPlus, Trash, Key, X, Check, Pencil } from '@phosphor-icons/react'
import { toast } from 'sonner'
import type { User, PasswordResetRequest } from '@/types'
import { api } from '@/lib/api'

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

  // Edit user state (T005)
  const [editingUser, setEditingUser] = useState<User | null>(null)
  const [editForm, setEditForm] = useState({ fullName: '', email: '', role: '', department: '' })
  const [editDepartments, setEditDepartments] = useState<string[]>([])
  const [editDeptInput, setEditDeptInput] = useState('')
  const [isEditLoading, setIsEditLoading] = useState(false)

  const [newUser, setNewUser] = useState({
    username: '',
    fullName: '',
    email: '',
    department: '',
    role: 'recruiter' as 'admin' | 'recruiter' | 'business_panel',
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
        api.getAllUsers(),
        api.getPasswordResetRequests(),
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
      await api.createUser(
        newUser.username,
        newUser.role,
        newUser.department || '',
        newUser.password,
        newUser.fullName,
        newUser.email || undefined,
      )

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
      const success = await api.deleteUser(userId)
      if (success !== undefined) {
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
      const success = await api.resetUserPassword(userId, newResetPassword)
      if (success !== undefined) {
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

  // Edit user handlers (T005)
  const handleEditUser = (user: User) => {
    setEditingUser(user)
    const depts = user.department ? user.department.split(',').filter(d => d.trim()) : []
    setEditDepartments(depts)
    setEditDeptInput('')
    setEditForm({
      fullName: user.fullName,
      email: user.email ?? '',
      role: user.role,
      department: user.department ?? '',
    })
    setShowCreateForm(false)
  }

  const handleCancelEdit = () => {
    setEditingUser(null)
  }

  const handleSaveEdit = async () => {
    if (!editingUser) return

    if (!editForm.fullName.trim()) {
      toast.error('Full name is required')
      return
    }
    if (!editForm.email.trim()) {
      toast.error('Email is required')
      return
    }

    setIsEditLoading(true)
    try {
      const department = editDepartments.join(',')
      await api.updateUser(editingUser.userId, editForm.fullName, editForm.email, editForm.role, department)
      toast.success('User updated successfully')
      setEditingUser(null)
      await loadData()
    } catch (error: any) {
      toast.error(error.message || 'Failed to update user')
    } finally {
      setIsEditLoading(false)
    }
  }

  // Tag-style department input handlers (T008)
  const handleAddDepartment = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      const dept = editDeptInput.trim()
      if (!dept) return
      if (dept.includes(',')) {
        toast.error('Department names cannot contain commas')
        return
      }
      if (editDepartments.includes(dept)) {
        toast.error('Department already added')
        return
      }
      setEditDepartments([...editDepartments, dept])
      setEditDeptInput('')
    }
  }

  const handleRemoveDepartment = (dept: string) => {
    setEditDepartments(editDepartments.filter(d => d !== dept))
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
        const success = await api.resolvePasswordResetRequest(requestId, 'approve', password)
        if (success !== undefined) {
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
        const success = await api.resolvePasswordResetRequest(requestId, 'reject')
        if (success !== undefined) {
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
    <DraggableResizableDialog
      open={open}
      onOpenChange={onClose}
      defaultWidth={1200}
      defaultHeight={700}
      minWidth={800}
      minHeight={500}
    >
      <DraggableDialogHeader>
        <DialogTitle>User Management</DialogTitle>
      </DraggableDialogHeader>

      <DraggableDialogBody className="px-6">
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
            <Button onClick={() => setShowCreateForm(!showCreateForm)} disabled={!!editingUser}>
              <UserPlus size={16} />
              Add User
            </Button>
          </div>

          {showCreateForm && !editingUser && (
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
                    onValueChange={(value: 'admin' | 'recruiter' | 'business_panel') => setNewUser({ ...newUser, role: value })}
                    disabled={isLoading}
                  >
                    <SelectTrigger id="new-role">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="recruiter">Recruiter</SelectItem>
                      <SelectItem value="business_panel">Business Panel Member</SelectItem>
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

          {editingUser && (
            <div className="border rounded-lg p-4 space-y-4">
              <h4 className="font-medium">Edit User: {editingUser.username}</h4>
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label htmlFor="edit-fullname">Full Name *</Label>
                  <Input
                    id="edit-fullname"
                    value={editForm.fullName}
                    onChange={(e) => setEditForm({ ...editForm, fullName: e.target.value })}
                    disabled={isEditLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="edit-email">Email *</Label>
                  <Input
                    id="edit-email"
                    type="email"
                    value={editForm.email}
                    onChange={(e) => setEditForm({ ...editForm, email: e.target.value })}
                    disabled={isEditLoading}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="edit-role">Role</Label>
                  <Select
                    value={editForm.role}
                    onValueChange={(value) => setEditForm({ ...editForm, role: value })}
                    disabled={isEditLoading || editingUser.userId === currentUserId}
                  >
                    <SelectTrigger id="edit-role">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="recruiter">Recruiter</SelectItem>
                      <SelectItem value="business_panel">Business Panel Member</SelectItem>
                      <SelectItem value="admin">Admin</SelectItem>
                    </SelectContent>
                  </Select>
                  {editingUser.userId === currentUserId && (
                    <p className="text-xs text-muted-foreground">You cannot change your own role</p>
                  )}
                </div>
                <div className="space-y-2">
                  <Label>Departments</Label>
                  <div className="flex flex-wrap gap-1 mb-2">
                    {editDepartments.map((dept) => (
                      <Badge key={dept} variant="secondary" className="gap-1">
                        {dept}
                        <button
                          type="button"
                          onClick={() => handleRemoveDepartment(dept)}
                          className="ml-1 hover:text-destructive"
                          disabled={isEditLoading}
                        >
                          <X size={12} />
                        </button>
                      </Badge>
                    ))}
                  </div>
                  <Input
                    placeholder="Type a department and press Enter"
                    value={editDeptInput}
                    onChange={(e) => setEditDeptInput(e.target.value)}
                    onKeyDown={handleAddDepartment}
                    disabled={isEditLoading}
                  />
                </div>
              </div>
              <div className="flex gap-3 justify-end">
                <Button type="button" variant="outline" onClick={handleCancelEdit} disabled={isEditLoading}>
                  Cancel
                </Button>
                <Button onClick={handleSaveEdit} disabled={isEditLoading}>
                  {isEditLoading ? 'Saving...' : 'Save Changes'}
                </Button>
              </div>
            </div>
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
                      <Badge variant={user.role === 'admin' ? 'default' : user.role === 'business_panel' ? 'outline' : 'secondary'}>
                        {user.role === 'business_panel' ? 'Business Panel' : user.role}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {user.department && user.department.includes(',')
                        ? user.department.split(',').map((d) => (
                            <Badge key={d} variant="outline" className="mr-1 mb-1">
                              {d.trim()}
                            </Badge>
                          ))
                        : user.department || '—'}
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
                              onClick={() => handleEditUser(user)}
                              disabled={isEditLoading}
                            >
                              <Pencil size={16} />
                            </Button>
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
      </DraggableDialogBody>
    </DraggableResizableDialog>
  )
}
