import { useState } from 'react'
import { CaretDown, CaretRight, UploadSimple } from '@phosphor-icons/react'
import { Progress } from '@/components/ui/progress'
import { useUploadCoordinator } from '@/providers/UploadCoordinatorProvider'

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export function UploadStatusSurface() {
  const { sessions } = useUploadCoordinator()
  const [expanded, setExpanded] = useState<Set<string>>(
    () => new Set(sessions.map(session => session.id)),
  )

  if (sessions.length === 0) return null

  const toggle = (sessionId: string) => {
    setExpanded(current => {
      const next = new Set(current)
      if (next.has(sessionId)) next.delete(sessionId)
      else next.add(sessionId)
      return next
    })
  }

  return (
    <aside
      aria-label="Optional uploads"
      className="sticky top-0 z-20 border-b bg-background/95 px-8 py-3 backdrop-blur"
    >
      <div className="mx-auto max-w-7xl space-y-3">
        <div className="flex items-center gap-2 font-medium">
          <UploadSimple size={20} />
          <span>Optional uploads</span>
        </div>
        {sessions.map(session => {
          const isExpanded = expanded.has(session.id)
          return (
            <section key={session.id} className="rounded-md border p-3">
              <button
                type="button"
                className="flex w-full items-center gap-3 text-left"
                onClick={() => toggle(session.id)}
                aria-expanded={isExpanded}
              >
                {isExpanded ? <CaretDown size={16} /> : <CaretRight size={16} />}
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                    <span>{session.counts.total} files · {session.status}</span>
                    <span>{session.progressPercent}%</span>
                  </div>
                  <Progress
                    value={session.progressPercent}
                    aria-label="Upload progress"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={session.progressPercent}
                    className="mt-2 h-2"
                  />
                  <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                    <span>{session.counts.waitingOrThrottled} waiting</span>
                    <span>{session.counts.activeOrRetrying} active</span>
                    <span>{session.counts.succeeded} succeeded</span>
                    <span>{session.counts.skipped} skipped</span>
                    <span>{session.counts.failed} failed</span>
                    <span>{session.counts.interrupted} interrupted</span>
                  </div>
                </div>
              </button>
              {isExpanded && (
                <ul className="mt-3 space-y-2 border-t pt-3">
                  {session.items.map(item => (
                    <li key={item.id} className="rounded bg-muted/40 p-2 text-sm">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <span className="font-medium">{item.fileName}</span>
                        <span>{item.status}</span>
                      </div>
                      <div className="mt-1 flex flex-wrap gap-x-4 text-xs text-muted-foreground">
                        <span>{formatBytes(item.rawSizeBytes)}</span>
                        <span>{item.attemptCount} {item.attemptCount === 1 ? 'attempt' : 'attempts'}</span>
                      </div>
                      {item.outcomeMessage && (
                        <p className="mt-1 text-xs text-destructive">{item.outcomeMessage}</p>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </section>
          )
        })}
      </div>
    </aside>
  )
}
