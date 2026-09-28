// @vitest-environment jsdom

import { act, renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  createUploadSession: vi.fn(),
  uploadItemContent: vi.fn(),
  updateUploadItemStatus: vi.fn(),
  heartbeatUploadSession: vi.fn(),
  getUploadSession: vi.fn(),
  listUploadSessions: vi.fn(),
}))
const authState = vi.hoisted(() => ({
  status: 'authenticated' as const,
  user: { userId: 'owner-1' },
}))

let currentSizes: number[] = []
let currentSession: ReturnType<typeof session> | null = null

vi.mock('@/lib/api', () => ({ api }))
vi.mock('@/hooks/useAuth', () => ({
  useAuth: () => authState,
}))

import {
  UploadCoordinatorProvider,
  useUploadCoordinator,
} from '@/providers/UploadCoordinatorProvider'

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

const item = (ordinal: number, size: number) => ({
  id: `item-${ordinal}`,
  sessionId: 'session-1',
  occurrenceKey: `00000000-0000-4000-8000-${String(ordinal).padStart(12, '0')}`,
  ordinal,
  fileName: `file-${ordinal}.pdf`,
  mimeType: 'application/pdf' as const,
  rawSizeBytes: size,
  status: 'waiting' as const,
  attemptCount: 0,
  contentFingerprint: null,
  applicationId: null,
  outcomeCode: null,
  outcomeMessage: null,
  nextRetryAt: null,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  completedAt: null,
  concurrencyVersion: 1,
})

function session(sizes: number[], countLimit: number, byteLimit: number) {
  return {
    id: 'session-1',
    jobId: 'job-1',
    status: 'active' as const,
    allowDuplicates: false,
    limits: {
      fileConcurrency: countLimit,
      maxIndividualFileBytes: 4194304,
      maxInFlightBytes: byteLimit,
    },
    counts: {
      total: sizes.length,
      waitingOrThrottled: sizes.length,
      activeOrRetrying: 0,
      succeeded: 0,
      skipped: 0,
      failed: 0,
      interrupted: 0,
      terminal: 0,
    },
    progressPercent: 0,
    correlationId: 'correlation-1',
    createdAt: new Date().toISOString(),
    startedAt: null,
    lastHeartbeatAt: new Date().toISOString(),
    completedAt: null,
    concurrencyVersion: 1,
    items: sizes.map((size, ordinal) => item(ordinal, size)),
  }
}

function echoOccurrenceKeys(
  value: ReturnType<typeof session>,
  request: { items: Array<{ occurrenceKey: string }> },
) {
  return {
    ...value,
    items: value.items.map((uploadItem, index) => ({
      ...uploadItem,
      occurrenceKey: request.items[index].occurrenceKey,
    })),
  }
}

describe('UploadCoordinatorProvider', () => {
  beforeEach(() => {
    vi.useRealTimers()
    vi.clearAllMocks()
    sessionStorage.clear()
    currentSizes = []
    currentSession = null
    api.updateUploadItemStatus.mockImplementation(async (_sessionId, _itemId, request) => ({
      ...(currentSession?.items.find(uploadItem => uploadItem.id === _itemId)
        ?? item(
          Number(String(_itemId).split('-')[1]),
          currentSizes[Number(String(_itemId).split('-')[1])] ?? 1,
        )),
      status: request.status,
      outcomeCode: request.outcomeCode ?? null,
      outcomeMessage: request.outcomeMessage ?? null,
    }))
    api.listUploadSessions.mockResolvedValue([])
    api.getUploadSession.mockRejectedValue(new Error('detail unavailable'))
  })

  it('loads owned durable sessions after authentication without a tab-local marker', async () => {
    const durable = {
      ...session([1], 1, 1),
      status: 'completed' as const,
      items: [{ ...item(0, 1), status: 'interrupted' as const }],
    }
    api.listUploadSessions.mockResolvedValue([durable])
    api.getUploadSession.mockResolvedValue(durable)
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )

    const { result } = renderHook(() => useUploadCoordinator(), { wrapper })

    await waitFor(() => expect(result.current.sessions).toHaveLength(1))
    expect(sessionStorage.getItem('optional-upload-sessions')).toBeNull()
    expect(result.current.sessions[0].items[0].status).toBe('interrupted')
  })

  it('keeps the heartbeat timer stable while polling replaces session state', async () => {
    vi.useFakeTimers()
    const upload = deferred<any>()
    currentSizes = [1]
    api.createUploadSession.mockImplementation(async (_jobId, request) => {
      currentSession = echoOccurrenceKeys(session(currentSizes, 1, 1), request)
      return currentSession
    })
    api.uploadItemContent.mockReturnValue(upload.promise)
    api.getUploadSession.mockImplementation(async () => currentSession)
    api.heartbeatUploadSession.mockImplementation(async () => currentSession)
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )
    const { result, unmount } = renderHook(() => useUploadCoordinator(), { wrapper })

    await act(async () => {
      await result.current.startUpload(
        'job-1',
        [new File(['a'], 'candidate.pdf', { type: 'application/pdf' })],
        false,
      )
      await Promise.resolve()
    })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5100)
    })

    expect(api.getUploadSession).toHaveBeenCalledTimes(3)
    expect(api.heartbeatUploadSession).toHaveBeenCalledOnce()
    unmount()
    vi.useRealTimers()
  })

  it('creates durable intent before starting content and enforces count plus raw-byte permits in order', async () => {
    const releases = [deferred<any>(), deferred<any>(), deferred<any>()]
    const active = new Set<number>()
    let maxCount = 0
    let maxBytes = 0
    const sizes = [3, 3, 1]
    currentSizes = sizes
    api.createUploadSession.mockImplementation(async (_jobId, request) => {
      currentSession = echoOccurrenceKeys(session(sizes, 2, 4), request)
      return currentSession
    })
    api.uploadItemContent.mockImplementation(async (_sessionId, itemId) => {
      const ordinal = Number(String(itemId).split('-')[1])
      active.add(ordinal)
      maxCount = Math.max(maxCount, active.size)
      maxBytes = Math.max(maxBytes, [...active].reduce((total, index) => total + sizes[index], 0))
      const result = await releases[ordinal].promise
      active.delete(ordinal)
      return result
    })
    const files = sizes.map((size, index) => new File(
      [new Uint8Array(size)],
      `file-${index}.pdf`,
      { type: 'application/pdf' },
    ))
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )
    const { result } = renderHook(() => useUploadCoordinator(), { wrapper })

    let sessionId = ''
    await act(async () => {
      const created = await result.current.startUpload('job-1', files, false)
      sessionId = created.id
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(api.createUploadSession).toHaveBeenCalledOnce()
    expect(api.uploadItemContent).toHaveBeenCalledTimes(1)
    expect(api.updateUploadItemStatus).toHaveBeenCalledWith(
      'session-1',
      'item-1',
      expect.objectContaining({ status: 'throttled' }),
    )
    expect(api.uploadItemContent.mock.calls.map(call => call[1])).toEqual(['item-0'])

    await act(async () => {
      releases[0].resolve({ ...item(0, 3), status: 'succeeded' })
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(api.uploadItemContent.mock.calls.map(call => call[1])).toEqual(['item-0', 'item-1', 'item-2'])
    expect(maxCount).toBeLessThanOrEqual(2)
    expect(maxBytes).toBeLessThanOrEqual(4)

    await act(async () => {
      releases[1].resolve({ ...item(1, 3), status: 'succeeded' })
      releases[2].resolve({ ...item(2, 1), status: 'succeeded' })
      await result.current.waitForIdle(sessionId)
    })
  })

  it('releases permits after failure and does not stop the remaining queue', async () => {
    currentSizes = [1, 1]
    api.createUploadSession.mockImplementation(async (_jobId, request) => {
      currentSession = echoOccurrenceKeys(session(currentSizes, 1, 1), request)
      return currentSession
    })
    api.uploadItemContent
      .mockRejectedValueOnce(Object.assign(new Error('permanent'), { status: 422 }))
      .mockResolvedValueOnce({ ...item(1, 1), status: 'succeeded' })
    const files = [
      new File(['a'], 'file-0.pdf', { type: 'application/pdf' }),
      new File(['b'], 'file-1.pdf', { type: 'application/pdf' }),
    ]
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )
    const { result } = renderHook(() => useUploadCoordinator(), { wrapper })

    await act(async () => {
      const created = await result.current.startUpload('job-1', files, false)
      await result.current.waitForIdle(created.id)
    })

    expect(api.uploadItemContent).toHaveBeenCalledTimes(2)
  })

  it('counts browser transport attempts independently and durably fails after four requests never reach the server', async () => {
    vi.useFakeTimers()
    currentSizes = [1]
    api.createUploadSession.mockImplementation(async (_jobId, request) => {
      currentSession = echoOccurrenceKeys(session(currentSizes, 1, 1), request)
      return currentSession
    })
    api.uploadItemContent.mockRejectedValue(new TypeError('transport unavailable'))
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )
    const { result } = renderHook(() => useUploadCoordinator(), { wrapper })

    let sessionId = ''
    await act(async () => {
      const created = await result.current.startUpload(
        'job-1',
        [new File(['a'], 'candidate.pdf', { type: 'application/pdf' })],
        false,
      )
      sessionId = created.id
      await vi.advanceTimersByTimeAsync(2000)
      await result.current.waitForIdle(created.id)
    })

    expect(api.uploadItemContent).toHaveBeenCalledTimes(4)
    expect(api.updateUploadItemStatus).toHaveBeenCalledWith(
      sessionId,
      'item-0',
      expect.objectContaining({
        status: 'failed',
        outcomeCode: 'retry_exhausted',
        transportAttemptCount: 4,
      }),
    )
    expect(result.current.sessions[0].items[0]).toMatchObject({
      status: 'failed',
      outcomeCode: 'retry_exhausted',
    })
    vi.useRealTimers()
  })

  it('handles a 100-file queue without exceeding configured count', async () => {
    const sizes = Array.from({ length: 100 }, () => 1)
    currentSizes = sizes
    api.createUploadSession.mockImplementation(async (_jobId, request) => {
      currentSession = echoOccurrenceKeys(session(sizes, 4, 100), request)
      return currentSession
    })
    let active = 0
    let maxActive = 0
    api.uploadItemContent.mockImplementation(async (_sessionId, itemId) => {
      active += 1
      maxActive = Math.max(maxActive, active)
      await Promise.resolve()
      active -= 1
      const ordinal = Number(String(itemId).split('-')[1])
      return { ...item(ordinal, 1), status: 'succeeded' }
    })
    const files = sizes.map((_, index) => new File(['x'], `file-${index}.pdf`, { type: 'application/pdf' }))
    const wrapper = ({ children }: { children: ReactNode }) => (
      <UploadCoordinatorProvider>{children}</UploadCoordinatorProvider>
    )
    const { result } = renderHook(() => useUploadCoordinator(), { wrapper })

    await act(async () => {
      const created = await result.current.startUpload('job-1', files, false)
      await result.current.waitForIdle(created.id)
    })

    expect(api.uploadItemContent).toHaveBeenCalledTimes(100)
    expect(maxActive).toBeLessThanOrEqual(4)
  })
})
