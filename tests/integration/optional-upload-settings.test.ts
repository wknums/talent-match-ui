import { once } from 'node:events'
import type { AddressInfo } from 'node:net'
import express from 'express'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const settings = {
  fileConcurrency: 4,
  maxIndividualFileBytes: 4194304,
  maxInFlightBytes: 104857600,
  concurrencyVersion: 0,
  persisted: false,
  updatedAt: null,
  updatedBy: null,
}

const service = vi.hoisted(() => ({
  getSettings: vi.fn(),
  updateSettings: vi.fn(),
}))

vi.mock('../../server/services/optional-upload.js', async importOriginal => {
  const actual = await importOriginal<typeof import('../../server/services/optional-upload.js')>()
  return { ...actual, optionalUploadService: service }
})

import { createUploadSettingsRouter } from '../../server/routes/upload-settings.js'

const closeCallbacks: Array<() => Promise<void>> = []

afterEach(async () => {
  await Promise.all(closeCallbacks.splice(0).map(close => close()))
})

beforeEach(() => {
  vi.clearAllMocks()
  service.getSettings.mockResolvedValue(settings)
  service.updateSettings.mockResolvedValue({
    ...settings,
    persisted: true,
    concurrencyVersion: 1,
    updatedAt: new Date().toISOString(),
    updatedBy: 'admin-1',
  })
})

async function createServer(role: 'admin' | 'recruiter' | 'organization_admin') {
  const app = express()
  app.use(express.json())
  app.use((req, _res, next) => {
    ;(req as any).user = {
      userId: `${role}-1`,
      username: `${role}@example.com`,
      role,
      fullName: role,
    }
    ;(req as any).authorizationContext = {
      globalRole: role === 'admin' ? 'admin' : null,
    }
    next()
  })
  app.use('/api/admin/upload-settings', createUploadSettingsRouter())
  const server = app.listen(0)
  await once(server, 'listening')
  const port = (server.address() as AddressInfo).port
  closeCallbacks.push(() => new Promise<void>((resolve, reject) => {
    server.close(error => error ? reject(error) : resolve())
  }))
  return `http://127.0.0.1:${port}/api/admin/upload-settings`
}

describe('optional upload settings endpoint', () => {
  it('returns absent-row defaults without persisting for a global admin', async () => {
    const url = await createServer('admin')

    const response = await fetch(url)

    expect(response.status).toBe(200)
    await expect(response.json()).resolves.toEqual(settings)
    expect(service.updateSettings).not.toHaveBeenCalled()
  })

  it.each(['recruiter', 'organization_admin'] as const)(
    'denies %s direct access with a canonical forbidden response',
    async role => {
      const url = await createServer(role)

      const response = await fetch(url)

      expect(response.status).toBe(403)
      await expect(response.json()).resolves.toMatchObject({
        error: 'forbidden',
        correlationId: expect.any(String),
      })
      expect(service.getSettings).not.toHaveBeenCalled()
    },
  )

  it('saves valid settings with optimistic version and actor identity', async () => {
    const url = await createServer('admin')
    const request = {
      fileConcurrency: 2,
      maxIndividualFileBytes: 2097152,
      maxInFlightBytes: 5242880,
      expectedConcurrencyVersion: 0,
    }

    const response = await fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-Correlation-ID': 'correlation-1' },
      body: JSON.stringify(request),
    })

    expect(response.status).toBe(200)
    expect(response.headers.get('X-Correlation-ID')).toBe('correlation-1')
    expect(service.updateSettings).toHaveBeenCalledWith(
      request,
      'admin-1',
      'correlation-1',
    )
  })

  it.each([
    [{ fileConcurrency: 0, maxIndividualFileBytes: 1, maxInFlightBytes: 1, expectedConcurrencyVersion: 0 }, 'fileConcurrency'],
    [{ fileConcurrency: 1, maxIndividualFileBytes: 0, maxInFlightBytes: 1, expectedConcurrencyVersion: 0 }, 'maxIndividualFileBytes'],
    [{ fileConcurrency: 1, maxIndividualFileBytes: 2, maxInFlightBytes: 1, expectedConcurrencyVersion: 0 }, 'maxInFlightBytes'],
  ])('returns field-specific validation without replacing valid settings', async (request, field) => {
    const url = await createServer('admin')
    service.updateSettings.mockRejectedValue(Object.assign(
      new Error('Optional upload settings validation failed.'),
      {
        code: 'validation_failed',
        statusCode: 422,
        errors: { [field]: ['Invalid value.'] },
      },
    ))

    const response = await fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    })

    expect(response.status).toBe(422)
    await expect(response.json()).resolves.toMatchObject({
      errors: { [field]: ['Invalid value.'] },
    })
  })

  it('returns 409 for a stale optimistic version', async () => {
    const url = await createServer('admin')
    service.updateSettings.mockRejectedValue(Object.assign(
      new Error('The upload settings have changed.'),
      { code: 'stale_version', statusCode: 409 },
    ))

    const response = await fetch(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        fileConcurrency: 2,
        maxIndividualFileBytes: 2097152,
        maxInFlightBytes: 5242880,
        expectedConcurrencyVersion: 4,
      }),
    })

    expect(response.status).toBe(409)
    await expect(response.json()).resolves.toMatchObject({ error: 'stale_version' })
  })
})
