import { Router } from 'express'
import { auditRepo } from '../storage/repos/index.js'

export function createAuditRouter() {
  const router = Router()

  // GET /api/audit - query audit ledger with filters
  router.get('/', async (req, res, next) => {
    try {
      const { entityType, eventType, startDate, endDate, page, pageSize } = req.query

      const result = await auditRepo.query({
        entityType: entityType as string | undefined,
        eventType: eventType as string | undefined,
        startDate: startDate as string | undefined,
        endDate: endDate as string | undefined,
        page: page ? parseInt(page as string) : undefined,
        pageSize: pageSize ? parseInt(pageSize as string) : undefined,
      })

      res.json({
        events: result.events,
        total: result.total,
        page: page ? parseInt(page as string) : 1,
        pageSize: pageSize ? parseInt(pageSize as string) : 50,
      })
    } catch (err) {
      next(err)
    }
  })

  return router
}
