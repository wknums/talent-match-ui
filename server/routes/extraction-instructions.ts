import { randomUUID } from 'node:crypto'
import { Router } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import { extractionInstructionRepo } from '../storage/repos/extraction-instruction-repo.js'
import { auditService } from '../services/audit.js'
import { ensureCorrelationId, mapAuthorizationError, sendAuthorizationError } from '../services/authorization-errors.js'
import { getProtectedContractMetadata } from '../services/extraction-contract.js'
import { jobSpecExtractionService } from '../services/job-spec-extraction.js'
import { resolveSupportedReasoningProfile } from '../services/reasoning-models.js'

function isAdmin(req: AuthenticatedRequest) {
  return req.authorizationContext?.globalRole === 'admin' || req.user?.role === 'admin'
}

function sendForbidden(req: AuthenticatedRequest, res: import('express').Response) {
  return sendAuthorizationError(
    req,
    res,
    mapAuthorizationError(undefined, {
      code: 'forbidden',
      statusCode: 403,
      message: 'Only admins can manage extraction instructions.',
    }),
  )
}

function sendApiError(
  req: AuthenticatedRequest,
  res: import('express').Response,
  statusCode: number,
  error: 'not_found' | 'stale_version' | 'validation_error' | 'validation_failed',
  message: string,
) {
  const correlationId = ensureCorrelationId(req, res)
  return res.status(statusCode).json({ error, message, correlationId })
}

export function createExtractionInstructionsRouter() {
  const router = Router()

  router.get('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!isAdmin(req)) return sendForbidden(req, res)
      res.json(await extractionInstructionRepo.list())
    } catch (error) {
      next(error)
    }
  })

  router.get('/:versionId', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!isAdmin(req)) return sendForbidden(req, res)
      const version = await extractionInstructionRepo.getById(req.params.versionId)
      if (!version) return sendApiError(req, res, 404, 'not_found', 'Instruction version not found.')
      res.json({ ...version, protectedContract: getProtectedContractMetadata() })
    } catch (error) {
      next(error)
    }
  })

  router.post('/', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!isAdmin(req)) return sendForbidden(req, res)
      const { instructionText, changeNote, modelId, reasoningLevel } = req.body
      if (!instructionText?.trim()) return sendApiError(req, res, 400, 'validation_error', 'instructionText is required.')
      let profile
      try {
        profile = await resolveSupportedReasoningProfile(modelId, reasoningLevel)
      } catch (error) {
        return sendApiError(
          req,
          res,
          400,
          'validation_error',
          error instanceof Error ? error.message : 'Invalid model or reasoning effort.',
        )
      }
      const created = {
        id: randomUUID(),
        versionNumber: await extractionInstructionRepo.getNextVersionNumber(),
        instructionText: instructionText.trim(),
        modelId: profile.modelId,
        reasoningLevel: profile.reasoningLevel,
        protectedContractVersion: getProtectedContractMetadata().version,
        status: 'draft' as const,
        validationStatus: 'unvalidated' as const,
        changeNote: changeNote?.trim() || null,
        validationFindings: [],
        createdAt: new Date().toISOString(),
        createdBy: req.user?.userId || req.user?.username || 'unknown',
        concurrencyVersion: 1,
      }
      await extractionInstructionRepo.create(created)
      await auditService.appendEvent(req.user?.username || 'unknown', 'extraction-instruction.created', 'ExtractionInstructionVersion', created.id, {
        versionId: created.id,
        versionNumber: created.versionNumber,
        status: created.status,
        validationStatus: created.validationStatus,
        hasChangeNote: Boolean(created.changeNote),
        modelId: created.modelId,
        reasoningLevel: created.reasoningLevel,
      })
      res.status(201).json(created)
    } catch (error) {
      next(error)
    }
  })

  router.post('/:versionId/validate', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!isAdmin(req)) return sendForbidden(req, res)
      const version = await extractionInstructionRepo.getById(req.params.versionId)
      if (!version) return sendApiError(req, res, 404, 'not_found', 'Instruction version not found.')

      const outcome = await jobSpecExtractionService.execute({
        fileName: req.body.fileName,
        contentBase64: req.body.content,
        mimeType: req.body.mimeType,
        purpose: 'instruction_validation',
        actor: req.user,
        instructionVersionId: version.id,
      })

      const updated = {
        ...version,
        validationStatus: outcome.ok ? 'valid' as const : 'invalid' as const,
        validationFindings: outcome.result.validationFindings,
        validatedAt: new Date().toISOString(),
        validatedBy: req.user?.userId || req.user?.username || 'unknown',
        concurrencyVersion: version.concurrencyVersion + 1,
      }
      await extractionInstructionRepo.update(updated)
      await auditService.appendEvent(req.user?.username || 'unknown', outcome.ok ? 'extraction-instruction.validated' : 'extraction-instruction.validation-failed', 'ExtractionInstructionVersion', version.id, {
        versionId: version.id,
        extractionId: outcome.record.id,
        validationStatus: updated.validationStatus,
        findingCodes: updated.validationFindings.map(finding => finding.code),
      })
      res.json(outcome.ok ? outcome.result : {
        ...outcome.result,
      })
    } catch (error) {
      next(error)
    }
  })

  router.post('/:versionId/activate', async (req: AuthenticatedRequest, res, next) => {
    try {
      if (!isAdmin(req)) return sendForbidden(req, res)
      const version = await extractionInstructionRepo.getById(req.params.versionId)
      if (!version) return sendApiError(req, res, 404, 'not_found', 'Instruction version not found.')
      if (Number(req.body.expectedConcurrencyVersion) !== version.concurrencyVersion) {
        return sendApiError(req, res, 409, 'stale_version', 'stale_version: reload before activating.')
      }
      if (version.validationStatus !== 'valid') {
        return sendApiError(req, res, 400, 'validation_failed', 'Only validated instruction versions can be activated.')
      }
      const previous = await extractionInstructionRepo.getActive()
      const activatedAt = new Date().toISOString()
      await extractionInstructionRepo.activate(version.id, req.user?.userId || req.user?.username || 'unknown', activatedAt)
      const refreshed = await extractionInstructionRepo.getById(version.id)
      await auditService.appendEvent(req.user?.username || 'unknown', previous?.id === version.id ? 'extraction-instruction.rolled-back' : 'extraction-instruction.activated', 'ExtractionInstructionVersion', version.id, {
        versionId: version.id,
        versionNumber: version.versionNumber,
        previousActiveVersionId: previous?.id ?? null,
      })
      res.json(refreshed)
    } catch (error) {
      next(error)
    }
  })

  return router
}
