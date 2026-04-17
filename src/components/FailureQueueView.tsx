import { useEffect, useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { ArrowClockwise, Warning, CaretDown, CaretRight } from '@phosphor-icons/react'
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
  const [collapsed, setCollapsed] = useState(true)
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [bulkProcessing, setBulkProcessing] = useState(false)
  const [lastUpdated, setLastUpdated] = useState<string | null>(null)

  useEffect(() => {
    loadItems()
    const interval = setInterval(loadItems, 30000)
    return () => clearInterval(interval)
  }, [])

  const loadItems = async () => {
    try {
      const data = await api.getDLQItems()
      setItems(data)
      setSelectedIds((current) => current.filter((id) => data.some((item) => item.itemId === id)))
      setLastUpdated(new Date().toISOString())
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

  const handleDelete = async (itemId: string) => {
    setBulkProcessing(true)
    try {
      await api.bulkDeleteDLQItems([itemId])
      toast.success('Item deleted')
      await loadItems()
    } catch {
      toast.error('Failed to delete item')
    } finally {
      setBulkProcessing(false)
    }
  }

  const handleBulkRetry = async () => {
    if (selectedIds.length === 0) return
    setBulkProcessing(true)
    try {
      const result = await api.bulkRetryDLQItems(selectedIds)
      toast.success(`Retried ${result.succeeded} of ${result.total} item(s)`)
      setSelectedIds([])
      await loadItems()
    } catch {
      toast.error('Failed to retry selected items')
    } finally {
      setBulkProcessing(false)
    }
  }

  const handleBulkDelete = async () => {
    if (selectedIds.length === 0) return
    setBulkProcessing(true)
    try {
      const result = await api.bulkDeleteDLQItems(selectedIds)
      toast.success(`Deleted ${result.deleted} of ${result.total} item(s)`)
      setSelectedIds([])
      await loadItems()
    } catch {
      toast.error('Failed to delete selected items')
    } finally {
      setBulkProcessing(false)
    }
  }

  const allSelected = items.length > 0 && selectedIds.length === items.length

  const toggleSelectAll = () => {
    setSelectedIds(allSelected ? [] : items.map((item) => item.itemId))
  }

  const toggleItemSelection = (itemId: string) => {
    setSelectedIds((current) => current.includes(itemId)
      ? current.filter((id) => id !== itemId)
      : [...current, itemId])
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
      <CardHeader className="cursor-pointer select-none" onClick={() => setCollapsed(!collapsed)}>
        <div className="flex items-center justify-between gap-4">
          <CardTitle className="text-lg flex items-center gap-2">
            {collapsed ? <CaretRight size={16} /> : <CaretDown size={16} />}
            <Warning size={20} className="text-destructive" />
            Failure Queue ({items.length})
          </CardTitle>
          {lastUpdated && (
            <span className="text-xs text-muted-foreground">
              Last updated: {new Date(lastUpdated).toLocaleTimeString()}
            </span>
          )}
        </div>
      </CardHeader>
      {!collapsed && (
      <CardContent>
        <div className="space-y-4">
          {selectedIds.length > 0 && (
            <div className="flex items-center gap-2 rounded-lg border bg-blue-50 px-3 py-2 text-sm">
              <span className="font-medium">{selectedIds.length} selected</span>
              <Button size="sm" onClick={handleBulkRetry} disabled={bulkProcessing}>
                Retry Selected
              </Button>
              <Button size="sm" variant="outline" onClick={handleBulkDelete} disabled={bulkProcessing}>
                Delete Selected
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setSelectedIds([])} disabled={bulkProcessing}>
                Clear
              </Button>
            </div>
          )}

          <div className="overflow-x-auto">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="border-b text-left">
                  <th className="w-10 py-2">
                    <input type="checkbox" checked={allSelected} onChange={toggleSelectAll} />
                  </th>
                  <th className="py-2">Entity Type</th>
                  <th className="py-2">Entity ID</th>
                  <th className="py-2">Failure Reason</th>
                  <th className="py-2">Retries</th>
                  <th className="py-2">Created</th>
                  <th className="py-2">Actions</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.itemId} className="border-b align-top">
                    <td className="py-3">
                      <input
                        type="checkbox"
                        checked={selectedIds.includes(item.itemId)}
                        onChange={() => toggleItemSelection(item.itemId)}
                      />
                    </td>
                    <td className="py-3">
                      <Badge variant="destructive" className="text-xs">
                        {item.entityType ?? 'Application'}
                      </Badge>
                    </td>
                    <td className="py-3 font-mono text-xs">{item.entityId ?? item.applicationId}</td>
                    <td className="py-3">
                      <div className="font-medium">{item.failureReason}</div>
                      {item.notes && (
                        <div className="mt-1 text-xs italic text-muted-foreground">{item.notes}</div>
                      )}
                    </td>
                    <td className="py-3">{item.retryCount ?? item.attemptCount}</td>
                    <td className="py-3 text-muted-foreground">
                      {new Date(item.createdAt ?? item.firstFailedAt).toLocaleString()}
                    </td>
                    <td className="py-3">
                      <div className="flex items-center gap-2">
                        {item.canRetry && (
                          <Button
                            size="sm"
                            variant="outline"
                            onClick={() => handleRetry(item.itemId)}
                            disabled={retrying === item.itemId || bulkProcessing}
                          >
                            <ArrowClockwise size={14} className={retrying === item.itemId ? 'animate-spin' : ''} />
                            Retry
                          </Button>
                        )}
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => handleDelete(item.itemId)}
                          disabled={bulkProcessing}
                        >
                          Delete
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </CardContent>
      )}
    </Card>
  )
}
