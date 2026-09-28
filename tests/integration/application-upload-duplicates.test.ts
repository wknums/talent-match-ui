import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const repoMocks = vi.hoisted(() => ({
  jobRepo: {
    getById: vi.fn(),
  },
  applicationRepo: {
    findDuplicateFingerprint: vi.fn(),
    create: vi.fn(),
    createDocument: vi.fn(),
    storeBlob: vi.fn(),
  },
  uploadRepo: {
    createSession: vi.fn(),
    updateItem: vi.fn(),
  },
}))

const auditMock = vi.hoisted(() => ({
  appendEvent: vi.fn(),
}))

vi.mock('../../server/storage/repos/index.js', () => repoMocks)
vi.mock('../../server/services/audit.js', () => ({ auditService: auditMock }))

import { createApplicationsRouter } from '../../server/routes/applications.js'

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

beforeEach(() => {
  vi.clearAllMocks()
  repoMocks.jobRepo.getById.mockResolvedValue({ jobId: 'job-1' })
  repoMocks.applicationRepo.findDuplicateFingerprint.mockResolvedValue({
    applicationId: 'existing-app',
    fileName: 'existing.pdf',
  })
})

async function createServer() {
  const app = express()
  app.use(express.json())
  app.use('/api', createApplicationsRouter())
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  const close = () => new Promise<void>((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve())
  })
  closeCallbacks.push(close)
  return (body: unknown) => fetch(`http://127.0.0.1:${port}/api/jobs/job-1/applications/upload`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

const file = {
  fileName: 'candidate.pdf',
  content: Buffer.from('cv').toString('base64'),
  mimeType: 'application/pdf',
  sizeBytes: 2,
}

describe('application duplicate uploads', () => {
  it('skips an existing fingerprint by default', async () => {
    const request = await createServer()

    const response = await request({ files: [file] })

    expect(response.status).toBe(201)
    await expect(response.json()).resolves.toMatchObject({
      applicationIds: [],
      warnings: [expect.stringContaining('duplicate detected')],
    })
    expect(repoMocks.applicationRepo.create).not.toHaveBeenCalled()
  })

  it('creates a distinct application when duplicates are allowed', async () => {
    const request = await createServer()

    const response = await request({ files: [file], allowDuplicates: true })

    expect(response.status).toBe(201)
    const body = await response.json() as { applicationIds: string[] }
    expect(body.applicationIds).toHaveLength(1)
    expect(repoMocks.applicationRepo.findDuplicateFingerprint).not.toHaveBeenCalled()
    expect(repoMocks.applicationRepo.create).toHaveBeenCalledOnce()
    expect(repoMocks.applicationRepo.create).toHaveBeenCalledWith(
      expect.objectContaining({ status: 'Queued' }),
    )
    expect(repoMocks.uploadRepo.createSession).not.toHaveBeenCalled()
    expect(repoMocks.uploadRepo.updateItem).not.toHaveBeenCalled()
  })

  it('keeps validation warnings and the legacy response shape without optional writes', async () => {
    const request = await createServer()

    const response = await request({
      files: [
        { ...file, fileName: 'unsupported.jpg', mimeType: 'image/jpeg' },
        file,
      ],
      allowDuplicates: true,
    })

    expect(response.status).toBe(201)
    await expect(response.json()).resolves.toEqual({
      applicationIds: [expect.any(String)],
      warnings: ['unsupported.jpg: invalid file type image/jpeg'],
    })
    expect(repoMocks.applicationRepo.create).toHaveBeenCalledOnce()
    expect(repoMocks.applicationRepo.create).toHaveBeenCalledWith(
      expect.objectContaining({ status: 'Queued' }),
    )
    expect(repoMocks.uploadRepo.createSession).not.toHaveBeenCalled()
    expect(repoMocks.uploadRepo.updateItem).not.toHaveBeenCalled()
  })
})
