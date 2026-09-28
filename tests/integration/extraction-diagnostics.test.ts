import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createJobsRouter } from '../../server/routes/jobs.js'
import { jobRepo, jobSpecExtractionRepo, userRepo } from '../../server/storage/repos/index.js'
import type { Job, JobSpecExtractionRecord, JobStats } from '@/types'
import { loadDynamicRubricFixtureJson } from '../setup'

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

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
  vi.restoreAllMocks()
})

beforeEach(() => {
  vi.spyOn(userRepo, 'getAll').mockResolvedValue([])
  vi.spyOn(jobRepo, 'getJobStats').mockResolvedValue(stats)
})

function sampleJob(extractionId = 'extract-1'): Job {
  return {
    jobId: 'job-1',
    jobCode: 'JOB-1',
    title: 'Senior Data Platform Engineer',
    department: 'Engineering',
    organization: 'Northwind Analytics',
    organizationId: 'org-1',
    departmentId: 'dept-1',
    postingDate: '2026-08-01T00:00:00.000Z',
    createdBy: 'admin-1',
    createdAt: '2026-08-01T00:00:00.000Z',
    status: 'Active',
    currentVersion: {
      versionId: 'cfg-1',
      jobId: 'job-1',
      rubric: [
        { id: 'cat-1', name: 'Delivery and Quality', description: 'Execution and quality', weight: 1 },
      ],
      rubricEnvelope: loadDynamicRubricFixtureJson('expected-ambiguous-rubric-v2.json'),
      mustHaves: [],
      desiredCriteria: [],
      runsPerApplication: 3,
      aggregationStrategy: 'median',
      longlistThreshold: 60,
      shortlistThreshold: 75,
      varianceThreshold: 15,
      rubricApprovalStatus: 'draft',
      rubricSource: 'extracted',
      extractionId,
      extractionInstructionVersionId: 'instruction-v3',
      createdAt: '2026-08-01T00:00:00.000Z',
    },
  }
}

async function createServer(job: Job, extraction?: JobSpecExtractionRecord) {
  vi.spyOn(jobRepo, 'getById').mockResolvedValue(job)
  vi.spyOn(jobRepo, 'isValidScope').mockResolvedValue(true)
  vi.spyOn(jobSpecExtractionRepo, 'getByConfigVersionId').mockResolvedValue(extraction)

  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).authorizationContext = {
      globalRole: 'admin',
      authorizations: [],
    }
    ;(req as any).user = {
      userId: 'admin-1',
      username: 'admin',
      fullName: 'Admin',
      role: 'admin',
      createdAt: '2026-08-01T00:00:00.000Z',
    }
    next()
  })
  app.use('/api/jobs', createJobsRouter())
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  closeCallbacks.push(() => new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve())))
  return (path: string, init?: RequestInit) => fetch(`http://127.0.0.1:${port}${path}`, init)
}

describe('dynamic rubric diagnostics fixtures', () => {
  it('preserves needs_review and source trace details', () => {
    const ambiguous = loadDynamicRubricFixtureJson<any>('ambiguous-category.json')
    expect(ambiguous.requirements[0].needs_review).toBe(true)
    expect(ambiguous.requirements[0].source_text).toContain('translate business requirements')
  })

  it('supports persisted extraction diagnostics records', () => {
    const sample: JobSpecExtractionRecord = {
      id: 'extract-1',
      purpose: 'job_creation',
      instructionVersionId: 'instruction-v1',
      protectedContractVersion: 'extraction-rubric-v1',
      sourceFileName: 'sample-spec.md',
      sourceMimeType: 'text/markdown',
      sourceSha256: 'hash',
      rawResponse: '{}',
      normalizedResponseJson: '{}',
      validationStatus: 'valid',
      validationFindings: [{ code: 'needs_review', severity: 'warning', path: '$.requirements[0]', message: 'Review it' }],
      createdAt: new Date().toISOString(),
      createdBy: 'admin',
      completedAt: new Date().toISOString(),
      correlationId: 'corr-1',
    }
    expect(sample.validationFindings[0].severity).toBe('warning')
  })

  it('returns extraction diagnostics, version traceability, and source details on job reads', async () => {
    const extraction: JobSpecExtractionRecord = {
      id: 'extract-1',
      purpose: 'job_creation',
      instructionVersionId: 'instruction-v3',
      protectedContractVersion: 'extraction-rubric-v1',
      sourceFileName: 'sample-spec.md',
      sourceMimeType: 'text/markdown',
      sourceSha256: 'hash',
      rawResponse: '{}',
      normalizedResponseJson: '{}',
      validationStatus: 'valid',
      validationFindings: [{ code: 'needs_review', severity: 'warning', path: '$.requirements[0]', message: 'Review category mapping' }],
      jobId: 'job-1',
      jobConfigVersionId: 'cfg-1',
      createdAt: '2026-08-01T00:00:00.000Z',
      createdBy: 'admin',
      completedAt: '2026-08-01T00:00:02.000Z',
      correlationId: 'corr-1',
    }
    const request = await createServer(sampleJob(), extraction)

    const response = await request('/api/jobs/job-1')
    expect(response.status).toBe(200)
    await expect(response.json()).resolves.toMatchObject({
      currentVersion: {
        extractionInstructionVersionId: 'instruction-v3',
        extraction: {
          instructionVersionId: 'instruction-v3',
          sourceFileName: 'sample-spec.md',
          correlationId: 'corr-1',
          validationFindings: [{
            code: 'needs_review',
            message: 'Review category mapping',
          }],
        },
        rubricEnvelope: {
          items: [{
            reviewStatus: 'needs_review',
            sourceText: expect.stringContaining('translate business requirements'),
          }],
        },
      },
    })
  })
})
