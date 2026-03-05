import { useEffect, useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { ArrowClockwise, Warning } from '@phosphor-icons/react'
import { api } from '@/lib/api'
import { toast } from 'sonner'
import type { DLQItem } from '@/types'

interface FailureQueueViewProps {
  className?: string
}

export function FailureQueueView({ className }: FailureQueueViewProps) {
  const [items, setItems] = useState<DLQItem[]>([])
  const [loading, setLoading] = useState(true)
  const [retrying, setRetrying] = useState<string | null>(null)

  useEffect(() => {
    loadItems()
    const interval = setInterval(loadItems, 30000)
    return () => clearInterval(interval)
  }, [])

  const loadItems = async () => {
    try {
      const data = await api.getDLQItems()
      setItems(data)
    } finally {
      setLoading(false)
    }
  }

  const handleRetry = async (itemId: string) => {
    setRetrying(itemId)
    try {
      await api.retryDLQItem(itemId)
      toast.success('Item re-queued for processing')
      await loadItems()
    } catch (error) {
      toast.error('Failed to retry item')
    } finally {
      setRetrying(null)
    }
  }

  if (loading) {
    return <div className="text-center py-4 text-muted-foreground">Loading failure queue...</div>
  }

  if (items.length === 0) {
    return (
      <Card className={className}>
        <CardContent className="py-6 text-center text-muted-foreground">
          No failed items in the queue
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className={className}>
      <CardHeader>
        <CardTitle className="text-lg flex items-center gap-2">
          <Warning size={20} className="text-destructive" />
          Failure Queue ({items.length})
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="space-y-3">
          {items.map((item) => (
            <div key={item.itemId} className="flex items-start gap-3 p-3 rounded-lg border">
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 mb-1">
                  <Badge variant="destructive" className="text-xs">
                    {item.failureType}
                  </Badge>
                  <span className="text-xs text-muted-foreground">
                    Attempts: {item.attemptCount}
                  </span>
                </div>
                <p className="text-sm font-medium truncate">{item.failureReason}</p>
                <p className="text-xs text-muted-foreground mt-1">
                  Application: {item.applicationId.slice(0, 12)}... • 
                  Last attempt: {new Date(item.lastAttemptedAt).toLocaleString()}
                </p>
                {item.notes && (
                  <p className="text-xs text-muted-foreground mt-1 italic">{item.notes}</p>
                )}
              </div>
              {item.canRetry && (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => handleRetry(item.itemId)}
                  disabled={retrying === item.itemId}
                >
                  <ArrowClockwise size={14} className={retrying === item.itemId ? 'animate-spin' : ''} />
                  Retry
                </Button>
              )}
            </div>
          ))}
        </div>
      </CardContent>
    </Card>
  )
}
