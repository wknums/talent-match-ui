import { beforeEach, describe, expect, it, vi } from 'vitest'

const uploadRepo = vi.hoisted(() => ({
  getSettings: vi.fn(),
  saveSettings: vi.fn(),
  createSession: vi.fn(),
  getOwnedSession: vi.fn(),
  listOwnedSessions: vi.fn(),
  getOwnedItem: vi.fn(),
  startItemAttempt: vi.fn(),
  completeItem: vi.fn(),
  recordItemOutcome: vi.fn(),
  updateItemStatus: vi.fn(),
  heartbeat: vi.fn(),
  reconcileStale: vi.fn(),
}))

vi.mock('../../server/storage/repos/index.js', () => ({ uploadRepo }))

import {
  isLegalUploadTransition,
  isTransientUploadStatus,
  optionalUploadService,
} from '../../server/services/optional-upload.js'

const terminalItem = {
  id: 'item-1',
  sessionId: 'session-1',
  occurrenceKey: '11111111-1111-4111-8111-111111111111',
  ordinal: 0,
  fileName: 'candidate.pdf',
  mimeType: 'application/pdf',
  rawSizeBytes: 2,
  status: 'succeeded',
  attemptCount: 1,
  contentFingerprint: 'a'.repeat(64),
  applicationId: 'application-1',
  outcomeCode: null,
  outcomeMessage: null,
  nextRetryAt: null,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  completedAt: new Date().toISOString(),
  concurrencyVersion: 2,
} as const

describe('optional upload lifecycle', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it.each([408, 429, 500, 502, 503])('classifies HTTP %s as transient', status => {
    expect(isTransientUploadStatus(status)).toBe(true)
  })

  it.each([400, 401, 403, 404, 409, 422])('classifies HTTP %s as permanent', status => {
    expect(isTransientUploadStatus(status)).toBe(false)
  })

  it('matches the proven legal transition state machine', () => {
    expect(isLegalUploadTransition('waiting', 'throttled')).toBe(true)
    expect(isLegalUploadTransition('throttled', 'uploading')).toBe(true)
    expect(isLegalUploadTransition('uploading', 'retrying')).toBe(true)
    expect(isLegalUploadTransition('retrying', 'uploading')).toBe(true)
    expect(isLegalUploadTransition('uploading', 'succeeded')).toBe(true)
    expect(isLegalUploadTransition('succeeded', 'waiting')).toBe(false)
    expect(isLegalUploadTransition('failed', 'retrying')).toBe(false)
  })

  it('retries session creation against the settings version that wins the commit race', async () => {
    uploadRepo.getSettings
      .mockResolvedValueOnce({
        fileConcurrency: 4,
        maxIndividualFileBytes: 4_194_304,
        maxInFlightBytes: 104_857_600,
        concurrencyVersion: 0,
        persisted: false,
        updatedAt: null,
        updatedBy: null,
      })
      .mockResolvedValueOnce({
        fileConcurrency: 2,
        maxIndividualFileBytes: 20_000_000,
        maxInFlightBytes: 40_000_000,
        concurrencyVersion: 1,
        persisted: true,
        updatedAt: new Date().toISOString(),
        updatedBy: 'admin-1',
      })
    uploadRepo.createSession
      .mockRejectedValueOnce(Object.assign(new Error('settings changed'), { code: 'settings_stale' }))
      .mockImplementationOnce(async session => ({
        ...session,
        status: 'active',
        limits: {
          fileConcurrency: session.fileConcurrency,
          maxIndividualFileBytes: session.maxIndividualFileBytes,
          maxInFlightBytes: session.maxInFlightBytes,
        },
        items: [],
      }))

    const result = await optionalUploadService.createSession({
      jobId: 'job-1',
      ownerActorId: 'owner-1',
      ownerDisplayName: 'Owner',
      correlationId: 'correlation-1',
      request: {
        allowDuplicates: false,
        items: [{
          occurrenceKey: '11111111-1111-4111-8111-111111111111',
          ordinal: 0,
          fileName: 'candidate.pdf',
          mimeType: 'application/pdf',
          rawSizeBytes: 1,
        }],
      },
    })

    expect(uploadRepo.createSession).toHaveBeenCalledTimes(2)
    expect(uploadRepo.createSession.mock.calls[0][0]).toMatchObject({
      settingsConcurrencyVersion: 0,
      maxIndividualFileBytes: 4_194_304,
    })
    expect(uploadRepo.createSession.mock.calls[1][0]).toMatchObject({
      settingsConcurrencyVersion: 1,
      maxIndividualFileBytes: 20_000_000,
    })
    expect(result.limits.maxIndividualFileBytes).toBe(20_000_000)
  })

  it('accepts only a validated four-attempt transport exhaustion as a durable client failure', async () => {
    const waiting = {
      ...terminalItem,
      status: 'waiting' as const,
      attemptCount: 0,
      applicationId: null,
      contentFingerprint: null,
      completedAt: null,
      concurrencyVersion: 1,
    }
    const failed = {
      ...waiting,
      status: 'failed' as const,
      outcomeCode: 'retry_exhausted',
      outcomeMessage: 'Upload failed after four browser transport attempts.',
      completedAt: new Date().toISOString(),
      concurrencyVersion: 2,
    }
    uploadRepo.getOwnedItem.mockResolvedValue(waiting)
    uploadRepo.updateItemStatus.mockResolvedValue(failed)

    const result = await optionalUploadService.updateItemStatus(
      'session-1',
      'item-1',
      'owner-1',
      {
        occurrenceKey: waiting.occurrenceKey,
        status: 'failed',
        expectedConcurrencyVersion: 1,
        outcomeCode: 'retry_exhausted',
        outcomeMessage: 'Upload failed after four browser transport attempts.',
        transportAttemptCount: 4,
      },
    )

    expect(result.status).toBe('failed')
    expect(uploadRepo.updateItemStatus).toHaveBeenCalledOnce()
    await expect(optionalUploadService.updateItemStatus(
      'session-1',
      'item-1',
      'owner-1',
      {
        occurrenceKey: waiting.occurrenceKey,
        status: 'failed',
        expectedConcurrencyVersion: 1,
        outcomeCode: 'retry_exhausted',
        outcomeMessage: 'Too soon.',
        transportAttemptCount: 3,
      },
    )).rejects.toMatchObject({ code: 'validation_failed', statusCode: 422 })
  })

  it('returns the canonical terminal item without starting or completing another attempt', async () => {
    uploadRepo.getOwnedItem.mockResolvedValue(terminalItem)

    const result = await optionalUploadService.completeItem({
      sessionId: 'session-1',
      itemId: 'item-1',
      occurrenceKey: terminalItem.occurrenceKey,
      ownerActorId: 'owner-1',
      correlationId: 'correlation-1',
      file: {
        fileName: 'candidate.pdf',
        mimeType: 'application/pdf',
        bytes: Buffer.from('cv'),
      },
    })

    expect(result).toEqual(terminalItem)
    expect(uploadRepo.startItemAttempt).not.toHaveBeenCalled()
    expect(uploadRepo.completeItem).not.toHaveBeenCalled()
  })

  it('preserves per-occurrence identity when duplicate content is allowed', async () => {
    const first = { ...terminalItem, id: 'item-1', applicationId: 'application-1' }
    const second = {
      ...terminalItem,
      id: 'item-2',
      occurrenceKey: '22222222-2222-4222-8222-222222222222',
      applicationId: 'application-2',
    }
    uploadRepo.getOwnedItem
      .mockResolvedValueOnce({ ...first, status: 'waiting', applicationId: null, completedAt: null })
      .mockResolvedValueOnce({ ...second, status: 'waiting', applicationId: null, completedAt: null })
    uploadRepo.getOwnedSession.mockResolvedValue({
      id: 'session-1',
      limits: { maxIndividualFileBytes: 4194304 },
    })
    uploadRepo.startItemAttempt
      .mockResolvedValueOnce({ ...first, status: 'uploading', applicationId: null })
      .mockResolvedValueOnce({ ...second, status: 'uploading', applicationId: null })
    uploadRepo.completeItem
      .mockResolvedValueOnce(first)
      .mockResolvedValueOnce(second)

    const firstResult = await optionalUploadService.completeItem({
      sessionId: 'session-1',
      itemId: first.id,
      occurrenceKey: first.occurrenceKey,
      ownerActorId: 'owner-1',
      correlationId: 'correlation-1',
      file: { fileName: 'candidate.pdf', mimeType: 'application/pdf', bytes: Buffer.from('cv') },
    })
    const secondResult = await optionalUploadService.completeItem({
      sessionId: 'session-1',
      itemId: second.id,
      occurrenceKey: second.occurrenceKey,
      ownerActorId: 'owner-1',
      correlationId: 'correlation-1',
      file: { fileName: 'candidate.pdf', mimeType: 'application/pdf', bytes: Buffer.from('cv') },
    })

    expect(firstResult.applicationId).toBe('application-1')
    expect(secondResult.applicationId).toBe('application-2')
    expect(uploadRepo.completeItem).toHaveBeenCalledTimes(2)
    expect(uploadRepo.completeItem.mock.calls[0][0].occurrenceKey)
      .not.toBe(uploadRepo.completeItem.mock.calls[1][0].occurrenceKey)
  })
})
