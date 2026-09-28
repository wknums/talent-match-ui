import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { api } from '@/lib/api'
import { useAuth } from '@/hooks/useAuth'
import type {
  CreateUploadItem,
  SupportedUploadMimeType,
  UploadItem,
  UploadSessionDetail,
} from '@/types'

const terminalStatuses = new Set<UploadItem['status']>([
  'succeeded',
  'skipped_duplicate',
  'failed',
  'interrupted',
])

const extensionMimeTypes: Record<string, SupportedUploadMimeType> = {
  '.pdf': 'application/pdf',
  '.md': 'text/markdown',
  '.docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  '.txt': 'text/plain',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.png': 'image/png',
}

function normalizeMime(file: File): SupportedUploadMimeType {
  const extension = `.${file.name.split('.').pop()?.toLowerCase() ?? ''}`
  return (file.type || extensionMimeTypes[extension] || 'application/octet-stream') as SupportedUploadMimeType
}

function aggregate(session: UploadSessionDetail): UploadSessionDetail {
  const counts = {
    total: session.items.length,
    waitingOrThrottled: session.items.filter(item => item.status === 'waiting' || item.status === 'throttled').length,
    activeOrRetrying: session.items.filter(item => item.status === 'uploading' || item.status === 'retrying').length,
    succeeded: session.items.filter(item => item.status === 'succeeded').length,
    skipped: session.items.filter(item => item.status === 'skipped_duplicate').length,
    failed: session.items.filter(item => item.status === 'failed').length,
    interrupted: session.items.filter(item => item.status === 'interrupted').length,
    terminal: session.items.filter(item => terminalStatuses.has(item.status)).length,
  }
  const completed = counts.total > 0 && counts.terminal === counts.total
  return {
    ...session,
    status: completed ? 'completed' : 'active',
    counts,
    progressPercent: counts.total === 0 ? 0 : Math.floor(counts.terminal / counts.total * 100),
    completedAt: completed ? session.completedAt ?? new Date().toISOString() : null,
  }
}

interface UploadCoordinatorContextValue {
  sessions: UploadSessionDetail[]
  startUpload(jobId: string, files: File[], allowDuplicates: boolean): Promise<UploadSessionDetail>
  waitForIdle(sessionId: string): Promise<void>
}

const UploadCoordinatorContext = createContext<UploadCoordinatorContextValue | null>(null)

export function UploadCoordinatorProvider({ children }: { children: ReactNode }) {
  const { status: authStatus, user } = useAuth()
  const [sessions, setSessions] = useState<UploadSessionDetail[]>([])
  const filesBySession = useRef(new Map<string, Map<string, File>>())
  const workBySession = useRef(new Map<string, Promise<void>>())
  const sessionsRef = useRef<UploadSessionDetail[]>([])

  useEffect(() => {
    sessionsRef.current = sessions
  }, [sessions])

  const updateItem = useCallback((sessionId: string, item: UploadItem) => {
    setSessions(current => current.map(session =>
      session.id === sessionId
        ? aggregate({
            ...session,
            items: session.items.map(currentItem => currentItem.id === item.id ? item : currentItem),
          })
        : session))
  }, [])

  const updateLocalStatus = useCallback((
    sessionId: string,
    itemId: string,
    status: UploadItem['status'],
    outcome?: { code: string; message: string },
  ) => {
    setSessions(current => current.map(session => {
      if (session.id !== sessionId) return session
      return aggregate({
        ...session,
        items: session.items.map(item => item.id === itemId
          ? {
              ...item,
              status,
              outcomeCode: outcome?.code ?? item.outcomeCode,
              outcomeMessage: outcome?.message ?? item.outcomeMessage,
              updatedAt: new Date().toISOString(),
              completedAt: terminalStatuses.has(status) ? new Date().toISOString() : null,
            }
          : item),
      })
    }))
  }, [])

  const runSession = useCallback(async (session: UploadSessionDetail) => {
    const fileMap = filesBySession.current.get(session.id)
    if (!fileMap) return
    const queue = session.items
      .filter(item => !terminalStatuses.has(item.status))
      .sort((left, right) => left.ordinal - right.ordinal)
    const active = new Map<string, { bytes: number; promise: Promise<void> }>()
    const transportAttempts = new Map<string, number>()
    let activeBytes = 0
    let pendingRetries = 0
    let wakeRetry: (() => void) | null = null
    const waitForRetry = () => new Promise<void>(resolve => { wakeRetry = resolve })
    const scheduleRetry = (item: UploadItem, transportAttemptCount: number) => {
      pendingRetries += 1
      const delayMs = Math.min(2000, 250 * 2 ** Math.max(0, transportAttemptCount - 1))
      window.setTimeout(() => {
        queue.push(item)
        queue.sort((left, right) => left.ordinal - right.ordinal)
        pendingRetries -= 1
        wakeRetry?.()
        wakeRetry = null
      }, delayMs)
    }
    const canonicalItem = async (itemId: string) => {
      try {
        const refreshed = await api.getUploadSession(session.id)
        setSessions(current => [
          refreshed,
          ...current.filter(existing => existing.id !== refreshed.id),
        ])
        return refreshed.items.find(item => item.id === itemId)
      } catch {
        return undefined
      }
    }
    const start = (item: UploadItem, file: File) => {
      const transportAttemptCount = (transportAttempts.get(item.id) ?? 0) + 1
      transportAttempts.set(item.id, transportAttemptCount)
      activeBytes += item.rawSizeBytes
      updateLocalStatus(session.id, item.id, 'uploading')
      const promise = api.uploadItemContent(
        session.id,
        item.id,
        item.occurrenceKey,
        file,
        session.correlationId,
      )
        .then(result => updateItem(session.id, result))
        .catch(async error => {
          const canonical = await canonicalItem(item.id)
          if (canonical && terminalStatuses.has(canonical.status)) {
            updateItem(session.id, canonical)
            return
          }
          const status = Number((error as { status?: number }).status)
          const transient = !Number.isFinite(status)
            || status === 408
            || status === 429
            || status >= 500
          const retryItem = canonical ?? {
            ...item,
            status: 'retrying' as const,
          }
          if (transient && transportAttemptCount < 4) {
            updateItem(session.id, retryItem)
            scheduleRetry(retryItem, transportAttemptCount)
            return
          }
          if (transient) {
            const persistExhaustion = (current: UploadItem) => api.updateUploadItemStatus(
              session.id,
              item.id,
              {
                occurrenceKey: item.occurrenceKey,
                status: 'failed',
                expectedConcurrencyVersion: current.concurrencyVersion,
                outcomeCode: 'retry_exhausted',
                outcomeMessage: 'Upload failed after four browser transport attempts. Try selecting the file again.',
                transportAttemptCount,
              },
            )
            try {
              const failed = await persistExhaustion(canonical ?? item)
              updateItem(session.id, failed)
              return
            } catch {
              const refreshed = await canonicalItem(item.id)
              if (refreshed && terminalStatuses.has(refreshed.status)) {
                updateItem(session.id, refreshed)
                return
              }
              if (refreshed) {
                try {
                  const failed = await persistExhaustion(refreshed)
                  updateItem(session.id, failed)
                  return
                } catch {
                  // The local terminal state remains visible until durable polling reconciles.
                }
              }
            }
          }
          updateLocalStatus(session.id, item.id, 'failed', {
            code: transient ? 'retry_exhausted' : 'upload_failed',
            message: transient
              ? 'Upload failed after four attempts. Try selecting the file again.'
              : error instanceof Error ? error.message : 'The file could not be uploaded.',
          })
        })
        .finally(() => {
          activeBytes -= item.rawSizeBytes
          active.delete(item.id)
        })
      active.set(item.id, { bytes: item.rawSizeBytes, promise })
    }

    while (queue.length > 0 || active.size > 0 || pendingRetries > 0) {
      let started = false
      while (queue.length > 0 && active.size < session.limits.fileConcurrency) {
        const item = queue[0]
        if (activeBytes + item.rawSizeBytes > session.limits.maxInFlightBytes) {
          if (item.status !== 'throttled') {
            try {
              const throttled = await api.updateUploadItemStatus(session.id, item.id, {
                occurrenceKey: item.occurrenceKey,
                status: 'throttled',
                expectedConcurrencyVersion: item.concurrencyVersion,
              })
              queue[0] = throttled
              updateItem(session.id, throttled)
            } catch {
              updateLocalStatus(session.id, item.id, 'throttled')
            }
          }
          break
        }
        queue.shift()
        const file = fileMap.get(item.occurrenceKey)
        if (!file) {
          updateLocalStatus(session.id, item.id, 'interrupted', {
            code: 'source_unavailable',
            message: 'The selected local file is no longer available.',
          })
          continue
        }
        started = true
        start(item, file)
      }
      if (active.size === 0) {
        if (queue.length === 0 && pendingRetries > 0) {
          await waitForRetry()
          continue
        }
        if (!started && queue.length > 0) {
          const item = queue.shift()!
          updateLocalStatus(session.id, item.id, 'failed', {
            code: 'capacity_configuration',
            message: 'The file cannot fit within the session byte limit.',
          })
        }
        continue
      }
      if (!started || active.size >= session.limits.fileConcurrency || queue.length === 0) {
        await Promise.race([...active.values()].map(entry => entry.promise))
      }
    }
    filesBySession.current.delete(session.id)
  }, [updateItem, updateLocalStatus])

  useEffect(() => {
    if (authStatus !== 'authenticated' || !user) {
      setSessions([])
      return
    }
    let cancelled = false
    const load = async () => {
      try {
        const summaries = await api.listUploadSessions(undefined, true)
        const details = await Promise.all(summaries.map(summary => api.getUploadSession(summary.id)))
        if (!cancelled) {
          setSessions(current => [
            ...details,
            ...current.filter(existing =>
              !details.some(detail => detail.id === existing.id)),
          ])
        }
      } catch {
        // Durable discovery retries on the next authenticated mount.
      }
    }
    void load()
    return () => { cancelled = true }
  }, [authStatus, user])

  useEffect(() => {
    const timer = window.setInterval(() => {
      void Promise.all(sessionsRef.current
        .filter(session => session.status === 'active')
        .map(async session => {
          try {
            const refreshed = await api.getUploadSession(session.id)
            setSessions(current => [
              refreshed,
              ...current.filter(existing => existing.id !== refreshed.id),
            ])
          } catch {
            // The durable state remains visible; the next poll retries.
          }
        }))
    }, 1500)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    const timer = window.setInterval(() => {
      void Promise.all([...workBySession.current.keys()].map(async sessionId => {
        const session = sessionsRef.current.find(current => current.id === sessionId)
        if (!session || session.status !== 'active') return
        try {
          const summary = await api.heartbeatUploadSession(
            sessionId,
            session.concurrencyVersion,
          )
          setSessions(current => current.map(existing =>
            existing.id === summary.id ? { ...existing, ...summary } : existing))
        } catch {
          // Item transitions can race the heartbeat version; polling reconciles it.
        }
      }))
    }, 5000)
    return () => window.clearInterval(timer)
  }, [])

  useEffect(() => {
    const interrupt = () => {
      for (const sessionId of workBySession.current.keys()) {
        const session = sessionsRef.current.find(current => current.id === sessionId)
        for (const item of session?.items ?? []) {
          if (terminalStatuses.has(item.status)) continue
          void api.updateUploadItemStatus(sessionId, item.id, {
            occurrenceKey: item.occurrenceKey,
            status: 'interrupted',
            expectedConcurrencyVersion: item.concurrencyVersion,
            outcomeCode: 'tab_interrupted',
            outcomeMessage: 'The originating browser tab is no longer uploading this file.',
          }).catch(() => undefined)
        }
      }
    }
    window.addEventListener('beforeunload', interrupt)
    return () => window.removeEventListener('beforeunload', interrupt)
  }, [])

  const startUpload = useCallback(async (
    jobId: string,
    files: File[],
    allowDuplicates: boolean,
  ) => {
    const itemFiles = new Map<string, File>()
    const items: CreateUploadItem[] = files.map((file, ordinal) => {
      const occurrenceKey = crypto.randomUUID()
      itemFiles.set(occurrenceKey, file)
      return {
        occurrenceKey,
        ordinal,
        fileName: file.name,
        mimeType: normalizeMime(file),
        rawSizeBytes: file.size,
      }
    })
    const session = await api.createUploadSession(jobId, { allowDuplicates, items })
    sessionStorage.setItem('optional-upload-sessions', 'true')
    filesBySession.current.set(session.id, itemFiles)
    setSessions(current => [session, ...current.filter(existing => existing.id !== session.id)])
    const work = runSession(session)
    workBySession.current.set(session.id, work)
    void work.finally(() => workBySession.current.delete(session.id))
    return session
  }, [runSession])

  const waitForIdle = useCallback(async (sessionId: string) => {
    await workBySession.current.get(sessionId)
  }, [])

  const value = useMemo(() => ({
    sessions,
    startUpload,
    waitForIdle,
  }), [sessions, startUpload, waitForIdle])

  return (
    <UploadCoordinatorContext.Provider value={value}>
      {children}
    </UploadCoordinatorContext.Provider>
  )
}

export function useUploadCoordinator(): UploadCoordinatorContextValue {
  const context = useContext(UploadCoordinatorContext)
  if (!context) {
    throw new Error('useUploadCoordinator must be used within UploadCoordinatorProvider.')
  }
  return context
}
