import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Job, JobStats } from '../../src/types/index.js'
import { createJobsRouter } from '../../server/routes/jobs.js'
import { auditService } from '../../server/services/audit.js'
import { jobRepo, userRepo } from '../../server/storage/repos/index.js'

const organizationId = '11111111-1111-4111-8111-111111111111'
const otherOrganizationId = '22222222-2222-4222-8222-222222222222'
const departmentId = '33333333-3333-4333-8333-333333333333'
const otherDepartmentId = '44444444-4444-4444-8444-444444444444'

const stats: JobStats = {
  totalApplications: 0,
  queued: 0,
  extracting: 0,
  scoring: 0,
  completed: 0,
  failed: 0,
  needsManualReview: 0,
  longlistCount: 0,
  shortlistCount: 0,
  excludedCount: 0,
}

function job(jobId: string, scopeOrganizationId = organizationId, scopeDepartmentId = departmentId): Job {
  return {
    jobId,
    jobCode: jobId.toUpperCase(),
    title: `Job ${jobId}`,
    department: 'Engineering',
    organization: scopeOrganizationId === organizationId ? 'Primary' : 'Other',
    organizationId: scopeOrganizationId,
    departmentId: scopeDepartmentId,
    postingDate: '2026-08-01T00:00:00.000Z',
    createdBy: 'actor-1',
    createdAt: '2026-08-01T00:00:00.000Z',
    status: 'Active',
    currentVersion: {
      versionId: `${jobId}-version`,
      jobId,
      rubric: [],
      mustHaves: [],
      desiredCriteria: [],
      runsPerApplication: 3,
      aggregationStrategy: 'median',
      longlistThreshold: 60,
      shortlistThreshold: 75,
      varianceThreshold: 15,
      rubricApprovalStatus: 'draft',
      rubricSource: 'manual',
      createdAt: '2026-08-01T00:00:00.000Z',
    },
  }
}

type ScopedRole = 'admin' | 'organization_admin' | 'recruiter' | 'business_panel'

function authorization(role: ScopedRole, scopedOrganizationId = organizationId, scopedDepartmentId: string | null = departmentId) {
  return {
    userId: 'actor-1',
    tenantId: '55555555-5555-4555-8555-555555555555',
    objectId: '66666666-6666-4666-8666-666666666666',
    username: 'actor@example.com',
    fullName: 'Authorized Actor',
    email: 'actor@example.com',
    globalRole: role === 'admin' ? 'admin' : null,
    memberships: [{
      organizationId: scopedOrganizationId,
      organizationName: 'Primary',
      defaultDepartmentId: departmentId,
      departments: [{ departmentId, departmentName: 'Engineering' }],
    }],
    authorizations: [{
      role,
      organizationId: role === 'admin' ? null : scopedOrganizationId,
      departmentId: role === 'admin' ? null : scopedDepartmentId,
      assignmentSource: 'delegated',
    }],
    authorizationVersion: 1,
    tokenIssuedAt: '2026-08-01T00:00:00.000Z',
    refreshRequiredAt: '2026-08-01T00:15:00.000Z',
  }
}

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
  vi.restoreAllMocks()
})

beforeEach(() => {
  vi.spyOn(userRepo, 'getAll').mockResolvedValue([])
  vi.spyOn(jobRepo, 'getJobStats').mockResolvedValue(stats)
  vi.spyOn(jobRepo, 'getAll').mockResolvedValue([])
  vi.spyOn(jobRepo, 'getByDepartment').mockResolvedValue([])
  vi.spyOn(jobRepo, 'getByScope').mockResolvedValue([])
  vi.spyOn(jobRepo, 'getById').mockResolvedValue(undefined)
  vi.spyOn(jobRepo, 'isValidScope').mockResolvedValue(true)
  vi.spyOn(jobRepo, 'create').mockResolvedValue(undefined)
  vi.spyOn(jobRepo, 'addConfigVersion').mockResolvedValue(undefined)
  vi.spyOn(auditService, 'appendEvent').mockResolvedValue({} as never)
})

async function createServer(role: ScopedRole, scopedDepartmentId: string | null = departmentId) {
  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    const context = authorization(role, organizationId, scopedDepartmentId)
    ;(req as any).authorizationContext = context
    ;(req as any).user = {
      userId: context.userId,
      username: context.username,
      fullName: context.fullName,
      email: context.email,
      role,
      department: scopedDepartmentId ? 'Engineering' : undefined,
      createdAt: context.tokenIssuedAt,
    }
    next()
  })
  app.use('/api/jobs', createJobsRouter())
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  const close = () => new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
  closeCallbacks.push(close)
  return (path: string, init: RequestInit = {}) => fetch(`http://127.0.0.1:${port}${path}`, init)
}

describe('Stack A normalized job scope', () => {
  it('rejects creation without both organizationId and departmentId', async () => {
    const request = await createServer('recruiter')

    const response = await request('/api/jobs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: 'Scoped role', organization: 'Primary', department: 'Engineering' }),
    })

    expect(response.status).toBe(400)
    const body = await response.json() as Record<string, string>
    expect(body).toEqual({
      error: 'invalid_job_scope',
      message: expect.any(String),
      correlationId: response.headers.get('x-correlation-id'),
    })
    expect(jobRepo.create).not.toHaveBeenCalled()
  })

  it('rejects a department that belongs to another organization', async () => {
    vi.mocked(jobRepo.isValidScope).mockResolvedValue(false)
    const request = await createServer('organization_admin', null)

    const response = await request('/api/jobs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        title: 'Mismatched scope',
        organization: 'Primary',
        department: 'Engineering',
        organizationId,
        departmentId: otherDepartmentId,
      }),
    })

    expect(response.status).toBe(400)
    await expect(response.json()).resolves.toMatchObject({ error: 'invalid_job_scope' })
    expect(jobRepo.create).not.toHaveBeenCalled()
  })
})

describe('Stack A scope-specific job authorization', () => {
  it('lists only jobs in a Recruiter exact organization/department scope', async () => {
    const visible = job('visible')
    const sameDepartmentNameInOtherOrganization = job('other-org', otherOrganizationId, otherDepartmentId)
    vi.mocked(jobRepo.getByDepartment).mockResolvedValue([visible, sameDepartmentNameInOtherOrganization])
    vi.mocked(jobRepo.getByScope).mockResolvedValue([visible])
    const request = await createServer('recruiter')

    const response = await request('/api/jobs')
    const body = await response.json() as Job[]

    expect(response.status).toBe(200)
    expect(body.map(item => item.jobId)).toEqual(['visible'])
  })

  it('returns no job data for an exact-department assignment in another organization', async () => {
    vi.mocked(jobRepo.getById).mockResolvedValue(job('other-org', otherOrganizationId, otherDepartmentId))
    const request = await createServer('recruiter')

    const response = await request('/api/jobs/other-org')

    expect(response.status).toBe(403)
    const body = await response.json() as Record<string, string>
    expect(body).toEqual({
      error: 'forbidden',
      message: expect.any(String),
      correlationId: response.headers.get('x-correlation-id'),
    })
    expect(body).not.toHaveProperty('title')
  })

  it('allows an Organization Admin to mutate a job only in the assigned organization', async () => {
    vi.mocked(jobRepo.getById).mockResolvedValue(job('outside', otherOrganizationId, otherDepartmentId))
    const request = await createServer('organization_admin', null)

    const response = await request('/api/jobs/outside/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longlistThreshold: 65 }),
    })

    expect(response.status).toBe(403)
    expect(jobRepo.addConfigVersion).not.toHaveBeenCalled()
  })

  it('denies Analytics Viewer job mutations inside an otherwise readable scope', async () => {
    vi.mocked(jobRepo.getById).mockResolvedValue(job('visible'))
    const request = await createServer('business_panel')

    const response = await request('/api/jobs/visible/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ longlistThreshold: 65 }),
    })

    expect(response.status).toBe(403)
    expect(jobRepo.addConfigVersion).not.toHaveBeenCalled()
  })
})