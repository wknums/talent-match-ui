import { useState } from 'react'
import { Buildings, ShieldCheck, Warning } from '@phosphor-icons/react'
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
import { Button } from '@/components/ui/button'
import { DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { organizationAdminApi, TalentMatchApiError } from '@/lib/api'
import type { GrantOrganizationRoleRequest } from '@/types'

interface OrganizationAdminProps {
  open: boolean
  onClose: () => void
  globalAdmin: boolean
}

type PendingAction =
  | { kind: 'retire'; organizationId: string; departmentId: string }
  | { kind: 'revoke'; organizationId: string; assignmentId: string }

function splitIds(value: string): string[] {
  return [...new Set(value.split(',').map(item => item.trim()).filter(Boolean))]
}

export function OrganizationAdmin({ open, onClose, globalAdmin }: OrganizationAdminProps) {
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'error' | 'success'; text: string } | null>(null)
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null)
  const [organizationId, setOrganizationId] = useState('')

  const [organizationName, setOrganizationName] = useState('')
  const [initialDepartmentName, setInitialDepartmentName] = useState('')
  const [departmentId, setDepartmentId] = useState('')
  const [departmentName, setDepartmentName] = useState('')

  const [memberObjectId, setMemberObjectId] = useState('')
  const [departmentIdsText, setDepartmentIdsText] = useState('')
  const [defaultDepartmentId, setDefaultDepartmentId] = useState('')

  const [roleObjectId, setRoleObjectId] = useState('')
  const [role, setRole] = useState<GrantOrganizationRoleRequest['role']>('recruiter')
  const [roleDepartmentId, setRoleDepartmentId] = useState('')
  const [assignmentId, setAssignmentId] = useState('')

  const departmentIds = splitIds(departmentIdsText)

  const showError = (error: unknown, fallback: string) => {
    const detail = error instanceof Error ? error.message : fallback
    const correlation = error instanceof TalentMatchApiError && error.correlationId
      ? ` Correlation ID: ${error.correlationId}`
      : ''
    setMessage({ kind: 'error', text: `${detail || fallback}${correlation}` })
  }

  const execute = async (operation: () => Promise<unknown>, success: string) => {
    setBusy(true)
    setMessage(null)
    try {
      await operation()
      setMessage({ kind: 'success', text: success })
    } catch (error) {
      showError(error, 'The organization change could not be completed.')
    } finally {
      setBusy(false)
    }
  }

  const createOrganization = () => void execute(async () => {
    const result = await organizationAdminApi.createOrganization({
      name: organizationName,
      initialDepartmentName,
    })
    setOrganizationId(result.id)
    setDepartmentId(result.departments[0]?.id ?? '')
  }, 'Organization and initial department created.')

  const createDepartment = () => void execute(async () => {
    const result = await organizationAdminApi.createDepartment(organizationId, { name: departmentName })
    setDepartmentId(result.id)
  }, 'Department created.')

  const renameDepartment = () => void execute(
    () => organizationAdminApi.updateDepartment(organizationId, departmentId, { name: departmentName }),
    'Department renamed.',
  )

  const registerMembership = () => {
    if (!departmentIds.includes(defaultDepartmentId)) {
      setMessage({ kind: 'error', text: 'The explicit default must be one of the active department memberships.' })
      return
    }
    void execute(
      () => organizationAdminApi.registerMembership(organizationId, {
        userObjectId: memberObjectId,
        departmentIds,
        defaultDepartmentId,
      }),
      'Memberships and explicit default updated.',
    )
  }

  const grantRole = () => void execute(
    () => organizationAdminApi.grantRole(organizationId, {
      userObjectId: roleObjectId,
      role,
      departmentId: role === 'organization_admin' ? null : roleDepartmentId || null,
    }),
    'Delegated role granted.',
  )

  const confirmPendingAction = async () => {
    if (!pendingAction) return
    const action = pendingAction
    setPendingAction(null)
    if (action.kind === 'retire') {
      await execute(
        () => organizationAdminApi.updateDepartment(action.organizationId, action.departmentId, { status: 'retired' }),
        'Department retired.',
      )
      return
    }
    await execute(
      () => organizationAdminApi.revokeRole(action.organizationId, action.assignmentId),
      'Targeted delegated role revoked.',
    )
  }

  return (
    <>
      <DraggableResizableDialog
        open={open}
        onOpenChange={nextOpen => { if (!nextOpen) onClose() }}
        defaultWidth={860}
        defaultHeight={660}
        minWidth={680}
        minHeight={520}
      >
        <DraggableDialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Buildings size={22} /> Organization Administration
          </DialogTitle>
        </DraggableDialogHeader>
        <DraggableDialogBody className="px-6 pb-6">
          {message && (
            <div
              role={message.kind === 'error' ? 'alert' : 'status'}
              className={`mb-4 flex items-start gap-2 border px-3 py-2 text-sm ${message.kind === 'error' ? 'border-destructive text-destructive' : 'border-emerald-600 text-emerald-700'}`}
            >
              {message.kind === 'error' ? <Warning size={18} /> : <ShieldCheck size={18} />}
              <span>{message.text}</span>
            </div>
          )}

          <div className="mb-5 space-y-2">
            <Label htmlFor="organization-admin-id">Organization ID</Label>
            <Input
              id="organization-admin-id"
              value={organizationId}
              onChange={event => setOrganizationId(event.target.value)}
              placeholder="Organization object ID"
            />
          </div>

          <Tabs defaultValue="departments">
            <TabsList className="grid h-auto w-full grid-cols-3">
              <TabsTrigger value="departments">Departments</TabsTrigger>
              <TabsTrigger value="memberships">Memberships</TabsTrigger>
              <TabsTrigger value="roles">Delegated roles</TabsTrigger>
            </TabsList>

            <TabsContent value="departments" className="space-y-6 pt-4">
              {globalAdmin && (
                <form className="grid gap-3 border-b pb-5 sm:grid-cols-2" onSubmit={event => { event.preventDefault(); createOrganization() }}>
                  <div className="space-y-2">
                    <Label htmlFor="organization-admin-name">Organization name</Label>
                    <Input id="organization-admin-name" value={organizationName} onChange={event => setOrganizationName(event.target.value)} />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="organization-admin-initial-department">Initial department</Label>
                    <Input id="organization-admin-initial-department" value={initialDepartmentName} onChange={event => setInitialDepartmentName(event.target.value)} />
                  </div>
                  <Button type="submit" disabled={busy} className="sm:col-span-2 sm:justify-self-start">Create organization</Button>
                </form>
              )}

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="space-y-2">
                  <Label htmlFor="organization-admin-department-id">Department ID</Label>
                  <Input id="organization-admin-department-id" value={departmentId} onChange={event => setDepartmentId(event.target.value)} />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="organization-admin-department-name">Department name</Label>
                  <Input id="organization-admin-department-name" value={departmentName} onChange={event => setDepartmentName(event.target.value)} />
                </div>
                <div className="flex flex-wrap gap-2 sm:col-span-2">
                  <Button type="button" onClick={createDepartment} disabled={busy}>Create department</Button>
                  <Button type="button" variant="outline" onClick={renameDepartment} disabled={busy || !departmentId}>Rename department</Button>
                  <Button
                    type="button"
                    variant="destructive"
                    disabled={busy || !departmentId}
                    onClick={() => setPendingAction({ kind: 'retire', organizationId, departmentId })}
                  >
                    Retire department
                  </Button>
                </div>
              </div>
            </TabsContent>

            <TabsContent value="memberships" className="space-y-4 pt-4">
              <div className="space-y-2">
                <Label htmlFor="organization-admin-member-object">User object ID</Label>
                <Input id="organization-admin-member-object" value={memberObjectId} onChange={event => setMemberObjectId(event.target.value)} />
              </div>
              <div className="space-y-2">
                <Label htmlFor="organization-admin-department-ids">Active department IDs</Label>
                <Input
                  id="organization-admin-department-ids"
                  value={departmentIdsText}
                  onChange={event => setDepartmentIdsText(event.target.value)}
                  placeholder="Comma-separated department IDs"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="organization-admin-default">Explicit default department</Label>
                <select
                  id="organization-admin-default"
                  className="h-9 w-full rounded-md border bg-background px-3 text-sm"
                  value={defaultDepartmentId}
                  onChange={event => setDefaultDepartmentId(event.target.value)}
                >
                  <option value="">Select explicit default</option>
                  {departmentIds.map(id => <option key={id} value={id}>{id}</option>)}
                </select>
              </div>
              <Button type="button" onClick={registerMembership} disabled={busy}>Apply membership and default</Button>
            </TabsContent>

            <TabsContent value="roles" className="space-y-5 pt-4">
              <div className="grid gap-3 sm:grid-cols-2">
                <div className="space-y-2 sm:col-span-2">
                  <Label htmlFor="organization-admin-role-object">User object ID</Label>
                  <Input id="organization-admin-role-object" value={roleObjectId} onChange={event => setRoleObjectId(event.target.value)} />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="organization-admin-role">Delegated role</Label>
                  <select
                    id="organization-admin-role"
                    className="h-9 w-full rounded-md border bg-background px-3 text-sm"
                    value={role}
                    onChange={event => setRole(event.target.value as GrantOrganizationRoleRequest['role'])}
                  >
                    {globalAdmin && <option value="organization_admin">Organization admin</option>}
                    <option value="recruiter">Recruiter</option>
                    <option value="business_panel">Business panel</option>
                  </select>
                </div>
                <div className="space-y-2">
                  <Label htmlFor="organization-admin-role-department">Department ID</Label>
                  <Input
                    id="organization-admin-role-department"
                    value={roleDepartmentId}
                    onChange={event => setRoleDepartmentId(event.target.value)}
                    disabled={role === 'organization_admin'}
                  />
                </div>
                <Button type="button" onClick={grantRole} disabled={busy} className="sm:col-span-2 sm:justify-self-start">Grant delegated role</Button>
              </div>

              <div className="grid gap-3 border-t pt-5 sm:grid-cols-[1fr_auto]">
                <div className="space-y-2">
                  <Label htmlFor="organization-admin-assignment">Assignment ID</Label>
                  <Input id="organization-admin-assignment" value={assignmentId} onChange={event => setAssignmentId(event.target.value)} />
                </div>
                <Button
                  type="button"
                  variant="destructive"
                  className="self-end"
                  disabled={busy || !assignmentId}
                  onClick={() => setPendingAction({ kind: 'revoke', organizationId, assignmentId })}
                >
                  Revoke targeted role
                </Button>
              </div>
            </TabsContent>
          </Tabs>
        </DraggableDialogBody>
      </DraggableResizableDialog>

      <AlertDialog open={pendingAction !== null} onOpenChange={nextOpen => { if (!nextOpen) setPendingAction(null) }}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{pendingAction?.kind === 'retire' ? 'Retire department?' : 'Revoke delegated role?'}</AlertDialogTitle>
            <AlertDialogDescription>
              {pendingAction?.kind === 'retire'
                ? 'Retirement is rejected while jobs, memberships, defaults, or roles still depend on this department.'
                : 'Only this delegated assignment is revoked; unrelated assignments remain active.'}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={busy}>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => void confirmPendingAction()} disabled={busy}>Confirm change</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}