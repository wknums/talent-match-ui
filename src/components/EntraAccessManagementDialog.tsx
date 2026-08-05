import { useEffect, useState } from 'react'
import { MagnifyingGlass, ShieldCheck, UserCircle, Warning } from '@phosphor-icons/react'
import {
  DraggableDialogBody,
  DraggableDialogHeader,
  DraggableResizableDialog,
} from '@/components/DraggableResizableDialog'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { accessManagementApi, TalentMatchApiError } from '@/lib/api'
import type {
  DesiredDelegatedRole,
  EntraAccessRoleAssignment,
  EntraAccessUser,
  MembershipStatus,
} from '@/types'

interface EntraAccessManagementDialogProps {
  open: boolean
  onClose: () => void
  globalAdmin: boolean
}

type PendingAction =
  | { kind: 'organization' }
  | { kind: 'identity'; activate: boolean }
  | { kind: 'revoke'; organizationId: string; assignment: EntraAccessRoleAssignment }

function splitDepartmentIds(value: string): string[] {
  return [...new Set(value.split(',').map(item => item.trim()).filter(Boolean))]
}

export function EntraAccessManagementDialog({
  open,
  onClose,
  globalAdmin,
}: EntraAccessManagementDialogProps) {
  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState<'all' | 'pending' | 'active' | 'disabled'>('all')
  const [users, setUsers] = useState<EntraAccessUser[]>([])
  const [selected, setSelected] = useState<EntraAccessUser | null>(null)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ kind: 'error' | 'success'; text: string } | null>(null)
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null)

  const [organizationId, setOrganizationId] = useState('')
  const [membershipStatus, setMembershipStatus] = useState<MembershipStatus>('active')
  const [departmentText, setDepartmentText] = useState('')
  const [defaultDepartmentId, setDefaultDepartmentId] = useState('')
  const [desiredRoles, setDesiredRoles] = useState<DesiredDelegatedRole[]>([])
  const [newRole, setNewRole] = useState<DesiredDelegatedRole>({ role: 'recruiter', departmentId: null })

  const departmentIds = splitDepartmentIds(departmentText)

  const loadUsers = async () => {
    setLoading(true)
    setMessage(null)
    try {
      const page = await accessManagementApi.list({
        search: search.trim() || undefined,
        status: statusFilter === 'all' ? undefined : statusFilter,
        limit: 50,
      })
      setUsers(page.items)
      if (selected) {
        const refreshed = page.items.find(item => item.objectId === selected.objectId)
        if (refreshed) setSelected(refreshed)
      }
    } catch (error) {
      showError(error, 'Access profiles could not be loaded.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (open) void loadUsers()
    // Opening is the lifecycle boundary; searches are submitted explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const showError = (error: unknown, fallback: string) => {
    if (error instanceof TalentMatchApiError && error.errorCode === 'version_conflict') {
      setMessage({
        kind: 'error',
        text: `Access changed by another administrator. Refresh and retry.${error.correlationId ? ` Correlation ID: ${error.correlationId}` : ''}`,
      })
      return
    }
    const detail = error instanceof Error ? error.message : fallback
    const correlation = error instanceof TalentMatchApiError && error.correlationId
      ? ` Correlation ID: ${error.correlationId}`
      : ''
    setMessage({ kind: 'error', text: `${detail || fallback}${correlation}` })
  }

  const applyOrganization = (user: EntraAccessUser, requestedOrganizationId?: string) => {
    const organization = user.organizations.find(item => item.organizationId === requestedOrganizationId)
      ?? user.organizations[0]
    setOrganizationId(organization?.organizationId ?? '')
    setMembershipStatus(organization?.status ?? 'active')
    setDepartmentText(organization?.departmentIds.join(', ') ?? '')
    setDefaultDepartmentId(organization?.defaultDepartmentId ?? '')
    setDesiredRoles(organization?.roleAssignments
      .filter(item => item.source === 'delegated')
      .map(item => ({ role: item.role, departmentId: item.departmentId })) ?? [])
    setNewRole({ role: 'recruiter', departmentId: organization?.defaultDepartmentId ?? null })
  }

  const inspectUser = async (objectId: string) => {
    setLoading(true)
    setMessage(null)
    try {
      const user = await accessManagementApi.get(objectId)
      setSelected(user)
      applyOrganization(user)
    } catch (error) {
      showError(error, 'The access profile could not be loaded.')
    } finally {
      setLoading(false)
    }
  }

  const changeOrganization = (value: string) => {
    setOrganizationId(value)
    if (!selected) return
    const organization = selected.organizations.find(item => item.organizationId === value)
    if (organization) applyOrganization(selected, value)
  }

  const reviewOrganization = () => {
    setMessage(null)
    if (!selected || !organizationId.trim()) {
      setMessage({ kind: 'error', text: 'An organization ID is required.' })
      return
    }
    if (membershipStatus === 'active'
      && (departmentIds.length === 0 || !defaultDepartmentId || !departmentIds.includes(defaultDepartmentId))) {
      setMessage({ kind: 'error', text: 'Active access requires an explicit default from the active departments.' })
      return
    }
    if (membershipStatus === 'revoked' && (departmentIds.length > 0 || defaultDepartmentId || desiredRoles.length > 0)) {
      setMessage({ kind: 'error', text: 'Revoked access cannot retain departments, a default, or delegated roles.' })
      return
    }
    if (desiredRoles.some(role => role.departmentId && !departmentIds.includes(role.departmentId))) {
      setMessage({ kind: 'error', text: 'Every role department must be an active department membership.' })
      return
    }
    setPendingAction({ kind: 'organization' })
  }

  const addDesiredRole = () => {
    if (newRole.role === 'recruiter' && !newRole.departmentId) {
      setMessage({ kind: 'error', text: 'Recruiter access requires a department.' })
      return
    }
    const key = `${newRole.role}:${newRole.departmentId ?? ''}`
    if (desiredRoles.some(role => `${role.role}:${role.departmentId ?? ''}` === key)) {
      setMessage({ kind: 'error', text: 'That delegated role is already in the desired state.' })
      return
    }
    setDesiredRoles(current => [...current, newRole])
    setMessage(null)
  }

  const confirmAction = async () => {
    if (!selected || !pendingAction) return
    setSaving(true)
    setMessage(null)
    try {
      let updated: EntraAccessUser
      if (pendingAction.kind === 'organization') {
        updated = await accessManagementApi.putOrganizationAccess(selected.objectId, organizationId.trim(), {
          expectedVersion: selected.authorizationVersion,
          profile: {
            username: selected.username,
            fullName: selected.fullName,
            email: selected.email,
          },
          membership: {
            status: membershipStatus,
            departmentIds: membershipStatus === 'active' ? departmentIds : [],
            defaultDepartmentId: membershipStatus === 'active' ? defaultDepartmentId : null,
          },
          roleAssignments: membershipStatus === 'active' ? desiredRoles : [],
        })
      } else if (pendingAction.kind === 'identity') {
        updated = await accessManagementApi.updateUser(selected.objectId, {
          expectedVersion: selected.authorizationVersion,
          isActive: pendingAction.activate,
        })
      } else {
        updated = await accessManagementApi.revokeRoleAssignment(
          selected.objectId,
          pendingAction.organizationId,
          pendingAction.assignment.id,
          selected.authorizationVersion,
        )
      }
      setSelected(updated)
      setUsers(current => current.map(item => item.objectId === updated.objectId ? updated : item))
      applyOrganization(updated, organizationId)
      setMessage({ kind: 'success', text: pendingAction.kind === 'revoke' ? 'Role access revoked.' : 'Access updated.' })
    } catch (error) {
      showError(error, 'The access change could not be completed.')
    } finally {
      setSaving(false)
      setPendingAction(null)
    }
  }

  const confirmation = pendingAction?.kind === 'identity'
    ? {
        title: pendingAction.activate ? 'Reactivate identity?' : 'Disable identity?',
        description: pendingAction.activate
          ? 'The identity can use its effective assignments again.'
          : 'The identity will be denied even if assignments remain active.',
        action: 'Confirm identity change',
      }
    : pendingAction?.kind === 'revoke'
      ? {
          title: 'Revoke delegated role?',
          description: 'Only this delegated assignment will be revoked. Group and bootstrap assignments are preserved.',
          action: 'Confirm role revocation',
        }
      : {
          title: 'Apply organization access?',
          description: 'This replaces the desired memberships, explicit default, and delegated roles for this organization only.',
          action: 'Confirm access change',
        }

  return (
    <>
      <DraggableResizableDialog
        open={open}
        onOpenChange={nextOpen => { if (!nextOpen) onClose() }}
        defaultWidth={1120}
        defaultHeight={720}
        minWidth={760}
        minHeight={520}
      >
        <DraggableDialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <ShieldCheck size={22} /> Entra Access Management
          </DialogTitle>
        </DraggableDialogHeader>
        <DraggableDialogBody className="px-6 pb-6">
          <div className="grid min-h-[480px] grid-cols-1 gap-6 lg:grid-cols-[minmax(240px,0.72fr)_minmax(0,1.6fr)]">
            <section className="border-r-0 pr-0 lg:border-r lg:pr-6" aria-label="Entra profiles">
              <form
                className="grid gap-3"
                onSubmit={event => { event.preventDefault(); void loadUsers() }}
              >
                <Label htmlFor="entra-access-search">Search profiles</Label>
                <div className="flex gap-2">
                  <Input
                    id="entra-access-search"
                    value={search}
                    onChange={event => setSearch(event.target.value)}
                    placeholder="Name, email, or username"
                  />
                  <Button type="submit" size="icon" title="Search profiles" aria-label="Search profiles">
                    <MagnifyingGlass size={18} />
                  </Button>
                </div>
                <Label htmlFor="entra-access-status">Status</Label>
                <select
                  id="entra-access-status"
                  className="h-9 rounded-md border bg-background px-3 text-sm"
                  value={statusFilter}
                  onChange={event => setStatusFilter(event.target.value as typeof statusFilter)}
                >
                  <option value="all">All profiles</option>
                  <option value="pending">Pending</option>
                  <option value="active">Active</option>
                  <option value="disabled">Disabled</option>
                </select>
              </form>

              <div className="mt-5 min-h-48 space-y-2" aria-live="polite">
                {loading && <p className="text-sm text-muted-foreground">Loading access profiles...</p>}
                {!loading && users.length === 0 && (
                  <p className="text-sm text-muted-foreground">No Entra profiles match this search.</p>
                )}
                {!loading && users.map(user => (
                  <button
                    key={user.objectId}
                    type="button"
                    onClick={() => void inspectUser(user.objectId)}
                    className="flex w-full items-center gap-3 border-b px-2 py-3 text-left hover:bg-muted/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  >
                    <UserCircle size={24} className="shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium">{user.fullName}</span>
                      <span className="block truncate text-xs text-muted-foreground">{user.username}</span>
                    </span>
                    <Badge variant={user.isActive ? 'secondary' : 'destructive'}>
                      {user.organizations.length === 0 ? 'pending' : user.isActive ? 'active' : 'disabled'}
                    </Badge>
                  </button>
                ))}
              </div>
            </section>

            <section className="min-w-0" aria-label="Access profile details">
              {message && (
                <div
                  role={message.kind === 'error' ? 'alert' : 'status'}
                  className={`mb-4 flex items-start gap-2 border px-3 py-2 text-sm ${message.kind === 'error' ? 'border-destructive text-destructive' : 'border-emerald-600 text-emerald-700'}`}
                >
                  {message.kind === 'error' ? <Warning size={18} className="mt-0.5 shrink-0" /> : <ShieldCheck size={18} className="mt-0.5 shrink-0" />}
                  <span>{message.text}</span>
                </div>
              )}

              {!selected ? (
                <div className="flex min-h-64 items-center justify-center text-sm text-muted-foreground">
                  Select a profile to inspect access.
                </div>
              ) : (
                <div className="space-y-6">
                  <header className="flex flex-wrap items-start justify-between gap-3 border-b pb-4">
                    <div>
                      <h2 className="text-lg font-semibold">{selected.fullName}</h2>
                      <p className="text-sm text-muted-foreground">{selected.username}</p>
                      <p className="mt-1 font-mono text-xs text-muted-foreground">{selected.objectId}</p>
                    </div>
                    <div className="flex items-center gap-2">
                      <Badge variant="outline">Version {selected.authorizationVersion}</Badge>
                      {globalAdmin && (
                        <Button
                          type="button"
                          variant={selected.isActive ? 'destructive' : 'default'}
                          size="sm"
                          onClick={() => setPendingAction({ kind: 'identity', activate: !selected.isActive })}
                        >
                          {selected.isActive ? 'Disable identity' : 'Reactivate identity'}
                        </Button>
                      )}
                    </div>
                  </header>

                  <div className="grid gap-4 md:grid-cols-2">
                    <div className="space-y-2 md:col-span-2">
                      <Label htmlFor="entra-organization-id">Organization ID</Label>
                      <Input
                        id="entra-organization-id"
                        value={organizationId}
                        onChange={event => changeOrganization(event.target.value)}
                        placeholder="Organization object ID"
                      />
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="entra-membership-status">Membership state</Label>
                      <select
                        id="entra-membership-status"
                        className="h-9 w-full rounded-md border bg-background px-3 text-sm"
                        value={membershipStatus}
                        onChange={event => setMembershipStatus(event.target.value as MembershipStatus)}
                      >
                        <option value="active">Active</option>
                        <option value="revoked">Revoked</option>
                      </select>
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="entra-default-department">Default department</Label>
                      <select
                        id="entra-default-department"
                        className="h-9 w-full rounded-md border bg-background px-3 text-sm"
                        value={defaultDepartmentId}
                        onChange={event => setDefaultDepartmentId(event.target.value)}
                        disabled={membershipStatus === 'revoked'}
                      >
                        <option value="">Select explicit default</option>
                        {departmentIds.map(departmentId => (
                          <option key={departmentId} value={departmentId}>{departmentId}</option>
                        ))}
                      </select>
                    </div>
                    <div className="space-y-2 md:col-span-2">
                      <Label htmlFor="entra-department-ids">Department IDs</Label>
                      <Input
                        id="entra-department-ids"
                        value={departmentText}
                        onChange={event => setDepartmentText(event.target.value)}
                        placeholder="Comma-separated active department IDs"
                        disabled={membershipStatus === 'revoked'}
                      />
                    </div>
                  </div>

                  <div className="space-y-3 border-t pt-4">
                    <h3 className="text-sm font-semibold">Desired delegated roles</h3>
                    {desiredRoles.length === 0 && <p className="text-sm text-muted-foreground">No delegated roles selected.</p>}
                    {desiredRoles.map((role, index) => (
                      <div key={`${role.role}:${role.departmentId ?? ''}`} className="flex items-center justify-between gap-3 border-b pb-2 text-sm">
                        <span>{role.role.replace('_', ' ')}{role.departmentId ? ` / ${role.departmentId}` : ''}</span>
                        <Button type="button" variant="ghost" size="sm" onClick={() => setDesiredRoles(current => current.filter((_, itemIndex) => itemIndex !== index))}>
                          Remove
                        </Button>
                      </div>
                    ))}
                    {membershipStatus === 'active' && (
                      <div className="grid gap-2 sm:grid-cols-[1fr_1.4fr_auto]">
                        <select
                          aria-label="Delegated role"
                          className="h-9 rounded-md border bg-background px-3 text-sm"
                          value={newRole.role}
                          onChange={event => setNewRole(current => ({ ...current, role: event.target.value as DesiredDelegatedRole['role'] }))}
                        >
                          {globalAdmin && <option value="organization_admin">Organization admin</option>}
                          <option value="recruiter">Recruiter</option>
                          <option value="business_panel">Business panel</option>
                        </select>
                        <select
                          aria-label="Role department"
                          className="h-9 rounded-md border bg-background px-3 text-sm"
                          value={newRole.departmentId ?? ''}
                          onChange={event => setNewRole(current => ({ ...current, departmentId: event.target.value || null }))}
                          disabled={newRole.role === 'organization_admin'}
                        >
                          <option value="">Organization-wide</option>
                          {departmentIds.map(departmentId => (
                            <option key={departmentId} value={departmentId}>{departmentId}</option>
                          ))}
                        </select>
                        <Button type="button" variant="outline" onClick={addDesiredRole}>Add role</Button>
                      </div>
                    )}
                  </div>

                  {selected.organizations.flatMap(organization => organization.roleAssignments
                    .filter(assignment => assignment.source === 'delegated')
                    .map(assignment => (
                      <div key={assignment.id} className="flex flex-wrap items-center justify-between gap-3 border-t pt-3 text-sm">
                        <span>{assignment.role.replace('_', ' ')} / {organization.organizationId}</span>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => setPendingAction({ kind: 'revoke', organizationId: organization.organizationId, assignment })}
                        >
                          Revoke {assignment.role.replace('_', ' ')}
                        </Button>
                      </div>
                    ))) }

                  <div className="flex justify-end border-t pt-4">
                    <Button type="button" onClick={reviewOrganization} disabled={saving}>
                      Review organization access
                    </Button>
                  </div>
                </div>
              )}
            </section>
          </div>
        </DraggableDialogBody>
      </DraggableResizableDialog>

      <AlertDialog open={pendingAction !== null} onOpenChange={nextOpen => { if (!nextOpen) setPendingAction(null) }}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{confirmation.title}</AlertDialogTitle>
            <AlertDialogDescription>{confirmation.description}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={saving}>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => void confirmAction()} disabled={saving}>
              {saving ? 'Applying...' : confirmation.action}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}
