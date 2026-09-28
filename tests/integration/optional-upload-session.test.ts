import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const serviceMocks = vi.hoisted(() => ({
  createSession: vi.fn(),
  completeItem: vi.fn(),
  getOwnedSession: vi.fn(),
  reconcileAndGet: vi.fn(),
  heartbeat: vi.fn(),
  updateItemStatus: vi.fn(),
  listOwnedSessions: vi.fn(),
}))
const jobRepo = vi.hoisted(() => ({ getById: vi.fn() }))

vi.mock('../../server/services/optional-upload.js', async importOriginal => {
  const actual = await importOriginal<typeof import('../../server/services/optional-upload.js')>()
  return {
    ...actual,
    optionalUploadService: serviceMocks,
  }
})
vi.mock('../../server/storage/repos/index.js', () => ({ jobRepo }))

import { createUploadsRouter } from '../../server/routes/uploads.js'

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

beforeEach(() => {
  vi.clearAllMocks()
  jobRepo.getById.mockResolvedValue({ jobId: 'job-1' })
  serviceMocks.getOwnedSession.mockResolvedValue({
    id: 'session-1',
    jobId: 'job-1',
    limits: {
      fileConcurrency: 4,
      maxIndividualFileBytes: 4194304,
      maxInFlightBytes: 104857600,
    },
  })
})

async function createServer(authorizationContext?: unknown) {
  const app = express()
  app.use((req, _res, next) => {
    ;(req as any).user = {
      userId: 'owner-1',
      username: 'owner@example.com',
      role: 'recruiter',
      fullName: 'Owner',
    }
    ;(req as any).authorizationContext = authorizationContext
    next()
  })
  app.use(express.json())
  app.use('/api', createUploadsRouter())
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  closeCallbacks.push(() => new Promise<void>((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve())
  }))
  return `http://127.0.0.1:${port}/api`
}

describe('optional upload session contract', () => {
  it('creates every occurrence before any content request', async () => {
    const base = await createServer()
    serviceMocks.createSession.mockResolvedValue({
      id: 'session-1',
      jobId: 'job-1',
      items: [
        { id: 'item-1', occurrenceKey: '11111111-1111-4111-8111-111111111111' },
        { id: 'item-2', occurrenceKey: '22222222-2222-4222-8222-222222222222' },
      ],
    })
    const body = {
      allowDuplicates: false,
      items: [
        {
          occurrenceKey: '11111111-1111-4111-8111-111111111111',
          ordinal: 0,
          fileName: 'one.pdf',
          mimeType: 'application/pdf',
          rawSizeBytes: 4194304,
        },
        {
          occurrenceKey: '22222222-2222-4222-8222-222222222222',
          ordinal: 1,
          fileName: 'two.md',
          mimeType: 'text/markdown',
          rawSizeBytes: 1,
        },
      ],
    }

    const response = await fetch(`${base}/jobs/job-1/upload-sessions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Correlation-ID': 'correlation-1' },
      body: JSON.stringify(body),
    })

    expect(response.status).toBe(201)
    expect(serviceMocks.createSession).toHaveBeenCalledWith(expect.objectContaining({
      jobId: 'job-1',
      ownerActorId: 'owner-1',
      request: body,
    }))
    expect(serviceMocks.completeItem).not.toHaveBeenCalled()
  })

  it('accepts exactly one multipart file and forwards idempotency metadata', async () => {
    const base = await createServer()
    serviceMocks.completeItem.mockResolvedValue({
      id: 'item-1',
      sessionId: 'session-1',
      status: 'succeeded',
      applicationId: 'application-1',
    })
    const form = new FormData()
    form.append('file', new Blob(['cv'], { type: 'application/pdf' }), 'candidate.pdf')

    const response = await fetch(`${base}/upload-sessions/session-1/items/item-1/content`, {
      method: 'POST',
      headers: {
        'Idempotency-Key': '11111111-1111-4111-8111-111111111111',
        'X-Correlation-ID': 'correlation-1',
      },
      body: form,
    })

    expect(response.status).toBe(200)
    expect(serviceMocks.completeItem).toHaveBeenCalledWith(expect.objectContaining({
      sessionId: 'session-1',
      itemId: 'item-1',
      occurrenceKey: '11111111-1111-4111-8111-111111111111',
      ownerActorId: 'owner-1',
      file: expect.objectContaining({
        fileName: 'candidate.pdf',
        mimeType: 'application/pdf',
      }),
    }))
  })

  it.each([
    ['resume.pdf', '', 'application/pdf'],
    ['resume.md', '', 'text/markdown'],
    ['resume.docx', '', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    ['resume.txt', '', 'text/plain'],
    ['resume.jpg', '', 'image/jpeg'],
    ['resume.png', '', 'image/png'],
  ])('normalizes the supported %s type', async (fileName, mimeType, expected) => {
    const base = await createServer()
    serviceMocks.createSession.mockResolvedValue({ id: 'session-1', items: [] })

    const response = await fetch(`${base}/jobs/job-1/upload-sessions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        allowDuplicates: true,
        items: [{
          occurrenceKey: '11111111-1111-4111-8111-111111111111',
          ordinal: 0,
          fileName,
          mimeType,
          rawSizeBytes: 1,
        }],
      }),
    })

    expect(response.status).toBe(201)
    expect(serviceMocks.createSession).toHaveBeenCalledWith(expect.objectContaining({
      request: expect.objectContaining({
        items: [expect.objectContaining({ mimeType: expected })],
      }),
    }))
  })

  it('returns canonical owner/job and validation failures', async () => {
    const base = await createServer()
    serviceMocks.createSession.mockRejectedValue(Object.assign(
      new Error('No selected occurrence is eligible.'),
      {
        statusCode: 422,
        code: 'validation_error',
        errors: { 'items[0].rawSizeBytes': ['Must be at most 4194304 bytes.'] },
      },
    ))

    const response = await fetch(`${base}/jobs/job-1/upload-sessions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        allowDuplicates: false,
        items: [{
          occurrenceKey: '11111111-1111-4111-8111-111111111111',
          ordinal: 0,
          fileName: 'large.pdf',
          mimeType: 'application/pdf',
          rawSizeBytes: 4194305,
        }],
      }),
    })

    expect(response.status).toBe(422)
    await expect(response.json()).resolves.toMatchObject({
      error: 'validation_error',
      errors: { 'items[0].rawSizeBytes': expect.any(Array) },
      correlationId: expect.any(String),
    })
  })

  it('re-authorizes the session job before continuation reads or mutations', async () => {
    const base = await createServer({
      objectId: 'owner-1',
      globalRole: null,
      authorizations: [{
        role: 'recruiter',
        organizationId: 'organization-other',
        departmentId: 'department-other',
      }],
    })
    serviceMocks.getOwnedSession.mockResolvedValue({
      id: 'session-1',
      jobId: 'job-1',
      limits: {
        fileConcurrency: 4,
        maxIndividualFileBytes: 4194304,
        maxInFlightBytes: 104857600,
      },
    })
    jobRepo.getById.mockResolvedValue({
      jobId: 'job-1',
      organizationId: 'organization-1',
      departmentId: 'department-1',
    })

    const responses = await Promise.all([
      fetch(`${base}/upload-sessions/session-1`),
      fetch(`${base}/upload-sessions/session-1/heartbeat`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedConcurrencyVersion: 1 }),
      }),
      fetch(`${base}/upload-sessions/session-1/items/item-1/status`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          occurrenceKey: '11111111-1111-4111-8111-111111111111',
          status: 'interrupted',
          expectedConcurrencyVersion: 1,
        }),
      }),
      fetch(`${base}/upload-sessions/session-1/items/item-1/content`, {
        method: 'POST',
        headers: {
          'Content-Type': 'multipart/form-data; boundary=test',
          'Idempotency-Key': '11111111-1111-4111-8111-111111111111',
        },
        body: '--test--\r\n',
      }),
    ])

    expect(responses.map(response => response.status)).toEqual([403, 403, 403, 403])
    expect(serviceMocks.reconcileAndGet).not.toHaveBeenCalled()
    expect(serviceMocks.heartbeat).not.toHaveBeenCalled()
    expect(serviceMocks.updateItemStatus).not.toHaveBeenCalled()
    expect(serviceMocks.completeItem).not.toHaveBeenCalled()
  })
})
