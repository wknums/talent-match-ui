import { Router } from 'express'
import { applicationRepo, dlqRepo } from '../storage/repos/index.js'
import { auditService } from '../services/audit.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'

export function createDLQRouter() {
  const router = Router()

  const retryItem = async (itemId: string) => {
    const item = await dlqRepo.getById(itemId)
    if (!item || !item.canRetry) {
      return false
    }

    await dlqRepo.remove(itemId)

    try {
      const { createPipelineOrchestrator } = await import('../services/pipeline.js')
      const pipeline = createPipelineOrchestrator()
      pipeline.processApplication(item.applicationId, item.jobId).catch(err => {
        console.error(`Retry pipeline error for ${item.applicationId}:`, err)
      })
    } catch {
      // Pipeline import may fail in some contexts, item is already removed from DLQ
    }

    return item
  }

  // GET /api/dlq - list all failed items
  router.get('/', async (_req, res, next) => {
    try {
      const items = await dlqRepo.getAll()
      res.json(items)
    } catch (err) {
      next(err)
    }
  })

  // POST /api/dlq/:itemId/retry - retry a DLQ item
  router.post('/:itemId/retry', async (req: AuthenticatedRequest, res, next) => {
    try {
      const { itemId } = req.params
      const item = await retryItem(itemId)

      if (!item) {
        return res.status(404).json({ error: 'Not Found', message: 'DLQ item not found or cannot be retried' })
      }

      await auditService.appendEvent(
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

  // POST /api/dlq/bulk-retry - retry multiple DLQ items
  router.post('/bulk-retry', async (req: AuthenticatedRequest, res, next) => {
    try {
      const ids = Array.isArray(req.body?.ids) ? req.body.ids.filter((id: unknown): id is string => typeof id === 'string' && id.length > 0) : []
      let succeeded = 0

      for (const id of ids) {
        const item = await retryItem(id)
        if (!item) {
          continue
        }

        succeeded++
        await auditService.appendEvent(
          req.user?.username || 'unknown',
          'dlq.retry',
          'Application', item.applicationId,
          { itemId: id, failureType: item.failureType, bulk: true }
        )
      }

      res.json({ succeeded, total: ids.length })
    } catch (err) {
      next(err)
    }
  })

  // POST /api/dlq/bulk-delete - remove multiple DLQ items and associated application rows
  router.post('/bulk-delete', async (req: AuthenticatedRequest, res, next) => {
    try {
      const ids = Array.isArray(req.body?.ids) ? req.body.ids.filter((id: unknown): id is string => typeof id === 'string' && id.length > 0) : []
      let deleted = 0

      for (const id of ids) {
        const item = await dlqRepo.getById(id)
        if (!item) {
          continue
        }

        if (item.applicationId) {
          await applicationRepo.delete(item.applicationId)
        }

        await dlqRepo.remove(id)
        deleted++

        await auditService.appendEvent(
          req.user?.username || 'unknown',
          'dlq.deleted',
          'Application', item.applicationId || id,
          { itemId: id, failureType: item.failureType, bulk: true }
        )
      }

      res.json({ deleted, total: ids.length })
    } catch (err) {
      next(err)
    }
  })

  return router
}
