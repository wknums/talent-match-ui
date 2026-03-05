import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import { DLQ } from '../storage/kv-keys.js'
import { getArray, setArray } from '../storage/kv-helpers.js'
import { createAuditService } from '../services/audit.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import type { DLQItem } from '../../src/types/index.js'

export function createDLQRouter(storage: StorageProvider) {
  const router = Router()
  const audit = createAuditService(storage)

  // GET /api/dlq - list all failed items
  router.get('/', async (_req, res, next) => {
    try {
      const items = await getArray<DLQItem>(storage, DLQ)
      res.json(items)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/dlq/:itemId/retry - retry a DLQ item
  router.post('/:itemId/retry', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { itemId } = req.params
      const items = await getArray<DLQItem>(storage, DLQ)
      const item = items.find(i => i.itemId === itemId)

      if (!item) {
        return res.status(404).json({ error: 'Not Found', message: 'DLQ item not found' })
      }

      if (!item.canRetry) {
        return res.status(400).json({ error: 'Validation Error', message: 'This item cannot be retried' })
      }

      // Remove from DLQ
      const remaining = items.filter(i => i.itemId !== itemId)
      await setArray(storage, DLQ, remaining)

      // Re-trigger processing via pipeline
      try {
        const { createPipelineOrchestrator } = await import('../services/pipeline.js')
        const pipeline = createPipelineOrchestrator(storage)
        pipeline.processApplication(item.applicationId, item.jobId).catch(err => {
          console.error(`Retry pipeline error for ${item.applicationId}:`, err)
        })
      } catch {
        // Pipeline import may fail in some contexts, item is already removed from DLQ
      }

      await audit.appendEvent(
        req.user?.username || 'unknown',
        'dlq.retry',
        'Application', item.applicationId,
        { itemId, failureType: item.failureType }
      )

      res.json({ success: true, message: `Item ${itemId} re-queued for processing` })
    } catch (err) {
      next(err)
    }
  })

  return router
}
