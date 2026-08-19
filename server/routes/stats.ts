import { Router } from 'express'
import { statsRepo } from '../storage/repos/index.js'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { requireRole } from '../middleware/rbac.js'
import { computeDepartmentAnalytics, computeRecruiterAnalytics } from '../services/analytics.js'

export function createStatsRouter() {
  const router = Router()

  // GET /api/stats - system-wide stats
  router.get('/', async (_req, res, next) => {
    try {
      const stats = await statsRepo.getSystemStats()
      res.json(stats)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/stats/recruiters - per-recruiter analytics (admin/recruiter only)
  router.get('/recruiters', requireRole('admin', 'recruiter'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const allRecruiters = await computeRecruiterAnalytics(req.authorizationContext)

      const scoped = req.authorizationContext || req.user!.role === 'admin'
        ? allRecruiters
        : allRecruiters.filter(r => r.department === req.user!.department)

      res.json(scoped)
    } catch (err) {
      next(err)
    }
  })

  // GET /api/stats/departments - per-department analytics (admin/recruiter only)
  router.get('/departments', requireRole('admin', 'recruiter'), async (req: AuthenticatedRequest, res, next) => {
    try {
      const allRecruiters = await computeRecruiterAnalytics(req.authorizationContext)

      const scoped = req.authorizationContext || req.user!.role === 'admin'
        ? allRecruiters
        : allRecruiters.filter(r => r.department === req.user!.department)

      const departments = computeDepartmentAnalytics(scoped)
      res.json(departments)
    } catch (err) {
      next(err)
    }
  })

  return router
}
