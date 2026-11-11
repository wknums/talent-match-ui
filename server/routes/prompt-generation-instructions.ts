import { randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { auditService } from '../services/audit.js'
import { promptRepo } from '../storage/repos/index.js'
import { resolveSupportedReasoningProfile } from '../services/reasoning-models.js'

function isAdmin(req: AuthenticatedRequest): boolean {
  return req.authorizationContext?.globalRole === 'admin' || req.user?.role === 'admin'
}

export function createPromptGenerationInstructionsRouter() {
  const router = Router()

  router.use((req: AuthenticatedRequest, res, next) => {
    if (!isAdmin(req)) {
      res.status(403).json({
        error: 'Forbidden',
        message: 'Only admins can manage system scoring prompt generation instructions.',
      })
      return
    }
    next()
  })

  router.get('/', async (_req, res, next) => {
    try {
      res.json(await promptRepo.listInstructions())
    } catch (error) {
      next(error)
    }
  })

  router.post('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      const instructionText = req.body.instructionText?.trim()
      if (!instructionText) {
        return res.status(400).json({ error: 'Validation Error', message: 'instructionText is required.' })
      }
      let profile
      try {
        profile = await resolveSupportedReasoningProfile(req.body.modelId, req.body.reasoningLevel)
      } catch (error) {
        return res.status(400).json({
          error: 'Validation Error',
          message: error instanceof Error ? error.message : 'Invalid model or reasoning effort.',
        })
      }
      const versions = await promptRepo.listInstructions()
      const created = {
        id: randomUUID(),
        versionNumber: Math.max(0, ...versions.map(version => version.versionNumber)) + 1,
        instructionText,
        modelId: profile.modelId,
        reasoningLevel: profile.reasoningLevel,
        status: 'draft' as const,
        changeNote: req.body.changeNote?.trim() || undefined,
        createdAt: new Date().toISOString(),
        createdBy: req.user?.userId || req.user?.username || 'unknown',
      }
      await promptRepo.createInstruction(created)
      await auditService.appendEvent(
        req.user?.username || 'unknown',
        'scoring-generation-instruction.created',
        'PromptGenerationInstruction',
        created.id,
        {
          versionNumber: created.versionNumber,
          scope: 'system',
          modelId: created.modelId,
          reasoningLevel: created.reasoningLevel,
        },
      )
      res.status(201).json(created)
    } catch (error) {
      next(error)
    }
  })

  router.post('/:instructionId/activate', async (req: AuthenticatedRequest, res, next) => {
    try {
      const instruction = await promptRepo.getInstructionById(req.params.instructionId)
      if (!instruction || instruction.jobId) {
        return res.status(404).json({ error: 'Not Found', message: 'System instruction not found.' })
      }
      await promptRepo.activateInstruction(instruction.id, undefined, req.user?.userId || 'unknown')
      await auditService.appendEvent(
        req.user?.username || 'unknown',
        'scoring-generation-instruction.activated',
        'PromptGenerationInstruction',
        instruction.id,
        { versionNumber: instruction.versionNumber, scope: 'system' },
      )
      res.json(await promptRepo.getInstructionById(instruction.id))
    } catch (error) {
      next(error)
    }
  })

  return router
}
