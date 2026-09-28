import { Router } from 'express'
import { getReasoningModels } from '../services/reasoning-models.js'

export function createReasoningModelsRouter() {
  const router = Router()

  router.get('/', async (_req, res, next) => {
    try {
      res.json(await getReasoningModels())
    } catch (error) {
      next(error)
    }
  })

  return router
}
