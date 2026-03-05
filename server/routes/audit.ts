import { Router } from 'express'
import type { StorageProvider } from '../storage/types.js'
import { LEDGER } from '../storage/kv-keys.js'
import { getArray } from '../storage/kv-helpers.js'
import type { ProcessingEvent } from '../../src/types/index.js'

export function createAuditRouter(storage: StorageProvider) {
  const router = Router()

  // GET /api/audit - query audit ledger with filters
  router.get('/', async (req, res, next) => {
    try {
      const { entityType, eventType, startDate, endDate, page, pageSize } = req.query
      let events = await getArray<ProcessingEvent>(storage, LEDGER)

      if (entityType) {
        events = events.filter(e => e.entityType === entityType)
      }
      if (eventType) {
        events = events.filter(e => e.action === eventType)
      }
      if (startDate) {
        const start = new Date(startDate as string).getTime()
        events = events.filter(e => new Date(e.timestamp).getTime() >= start)
      }
      if (endDate) {
        const end = new Date(endDate as string).getTime()
        events = events.filter(e => new Date(e.timestamp).getTime() <= end)
      }

      // Sort by timestamp descending
      events.sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime())

      // Pagination
      const p = parseInt(page as string) || 1
      const ps = parseInt(pageSize as string) || 50
      const start = (p - 1) * ps
      const paged = events.slice(start, start + ps)

      res.json({
        events: paged,
        total: events.length,
        page: p,
        pageSize: ps,
      })
    } catch (err) {
      next(err)
    }
  })

  return router
}
