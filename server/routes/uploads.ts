import { Buffer } from 'node:buffer'
import { Router, type Response } from 'express'
import type { AuthenticatedRequest } from '../middleware/auth.js'
import {
  normalizeOptionalUploadMimeType,
  OptionalUploadError,
  optionalUploadService,
  type OptionalUploadFile,
} from '../services/optional-upload.js'
import { ensureCorrelationId } from '../services/authorization-errors.js'
import { jobRepo } from '../storage/repos/index.js'
import type { Job } from '../../src/types/index.js'

function ownerId(req: AuthenticatedRequest): string {
  return req.authorizationContext?.objectId
    ?? req.user?.userId
    ?? ''
}

function hasJobAccess(
  req: AuthenticatedRequest,
  job: Pick<Job, 'organizationId' | 'departmentId'>,
): boolean {
  const context = req.authorizationContext
  if (!context || context.globalRole === 'admin') return true
  if (!job.organizationId || !job.departmentId) return false
  return context.authorizations.some(authorization =>
    authorization.role !== 'business_panel'
    && authorization.organizationId === job.organizationId
    && (authorization.departmentId === null || authorization.departmentId === job.departmentId))
}

async function requireAuthorizedOwnedSession(
  req: AuthenticatedRequest,
  sessionId: string,
) {
  const session = await optionalUploadService.getOwnedSession(
    sessionId,
    ownerId(req),
    false,
  )
  if (!session) {
    throw new OptionalUploadError('not_found', 'Upload session was not found.', 404)
  }
  const job = await jobRepo.getById(session.jobId)
  if (!job) throw new OptionalUploadError('not_found', 'Job was not found.', 404)
  if (!hasJobAccess(req, job)) {
    throw new OptionalUploadError(
      'forbidden',
      'Access denied to this upload session.',
      403,
    )
  }
  return session
}

function sendError(
  req: AuthenticatedRequest,
  res: Response,
  statusCode: number,
  error: string,
  message: string,
  errors?: Record<string, string[]>,
) {
  const correlationId = ensureCorrelationId(req, res)
  return res.status(statusCode).json({
    error,
    message,
    correlationId,
    ...(errors ? { errors } : {}),
  })
}

function mapError(req: AuthenticatedRequest, res: Response, error: unknown) {
  const candidate = error as {
    code?: string
    statusCode?: number
    message?: string
    errors?: Record<string, string[]>
  }
  const statusCode = Number.isInteger(candidate?.statusCode)
    ? candidate.statusCode!
    : 500
  return sendError(
    req,
    res,
    statusCode,
    candidate?.code ?? 'internal_error',
    statusCode === 500
      ? 'The optional upload operation could not be completed.'
      : candidate?.message ?? 'The optional upload operation failed.',
    candidate?.errors,
  )
}

function boundaryFromContentType(contentType: string | undefined): string | null {
  const match = contentType?.match(/multipart\/form-data\s*;\s*boundary=(?:"([^"]+)"|([^;]+))/i)
  return match?.[1] ?? match?.[2]?.trim() ?? null
}

async function readBody(req: AuthenticatedRequest, maxBytes: number): Promise<Buffer> {
  const chunks: Buffer[] = []
  let total = 0
  for await (const chunk of req) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)
    total += buffer.length
    if (total > maxBytes) {
      throw new OptionalUploadError(
        'invalid_multipart',
        'The multipart request exceeds the session file limit.',
        400,
      )
    }
    chunks.push(buffer)
  }
  return Buffer.concat(chunks)
}

function parseSingleFileMultipart(body: Buffer, boundary: string): OptionalUploadFile {
  const delimiter = Buffer.from(`--${boundary}`)
  const files: OptionalUploadFile[] = []
  let cursor = 0
  while (cursor < body.length) {
    const boundaryIndex = body.indexOf(delimiter, cursor)
    if (boundaryIndex < 0) break
    let partStart = boundaryIndex + delimiter.length
    if (body.subarray(partStart, partStart + 2).equals(Buffer.from('--'))) break
    if (body.subarray(partStart, partStart + 2).equals(Buffer.from('\r\n'))) partStart += 2
    const headerEnd = body.indexOf(Buffer.from('\r\n\r\n'), partStart)
    if (headerEnd < 0) break
    const nextBoundary = body.indexOf(delimiter, headerEnd + 4)
    if (nextBoundary < 0) break
    const headers = body.subarray(partStart, headerEnd).toString('utf8')
    const disposition = headers.match(/content-disposition:\s*form-data\s*;([^\r\n]+)/i)?.[1] ?? ''
    const name = disposition.match(/(?:^|;)\s*name="([^"]*)"/i)?.[1]
    const fileName = disposition.match(/(?:^|;)\s*filename="([^"]*)"/i)?.[1]
    if (name === 'file' && fileName !== undefined) {
      const mimeType = headers.match(/content-type:\s*([^\r\n]+)/i)?.[1]?.trim() ?? ''
      let contentEnd = nextBoundary
      if (body.subarray(contentEnd - 2, contentEnd).equals(Buffer.from('\r\n'))) contentEnd -= 2
      files.push({
        fileName,
        mimeType,
        bytes: body.subarray(headerEnd + 4, contentEnd),
      })
    }
    cursor = nextBoundary
  }
  if (files.length !== 1) {
    throw new OptionalUploadError(
      'invalid_multipart',
      'Exactly one file is required.',
      400,
    )
  }
  return files[0]
}

export function createUploadsRouter() {
  const router = Router()

  router.post('/jobs/:jobId/upload-sessions', async (req: AuthenticatedRequest, res) => {
    const correlationId = ensureCorrelationId(req, res)
    try {
      const job = await jobRepo.getById(req.params.jobId)
      if (!job) return sendError(req, res, 404, 'not_found', 'Job was not found.')
      if (!hasJobAccess(req, job)) {
        return sendError(req, res, 403, 'forbidden', 'Access denied to mutate this job.')
      }
      const request = {
        allowDuplicates: req.body?.allowDuplicates === true,
        items: Array.isArray(req.body?.items)
          ? req.body.items.map((item: any) => ({
              ...item,
              mimeType: normalizeOptionalUploadMimeType(item.fileName ?? '', item.mimeType ?? '')
                ?? item.mimeType,
            }))
          : [],
      }
      const created = await optionalUploadService.createSession({
        jobId: req.params.jobId,
        ownerActorId: ownerId(req),
        ownerDisplayName: req.user?.username ?? null,
        request,
        correlationId,
      })
      res.location(`/api/upload-sessions/${created.id}`)
      return res.status(201).json(created)
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  router.post('/upload-sessions/:sessionId/items/:itemId/content', async (req: AuthenticatedRequest, res) => {
    const startedAt = Date.now()
    const correlationId = ensureCorrelationId(req, res)
    try {
      const occurrenceKey = req.get('Idempotency-Key')?.trim()
      if (!occurrenceKey) {
        return sendError(req, res, 400, 'invalid_idempotency_key', 'A UUID Idempotency-Key is required.')
      }
      const boundary = boundaryFromContentType(req.get('Content-Type'))
      if (!boundary) {
        return sendError(req, res, 400, 'invalid_multipart', 'Exactly one file is required.')
      }
      const session = await requireAuthorizedOwnedSession(req, req.params.sessionId)
      const rawLimit = session.limits.maxIndividualFileBytes
      const body = await readBody(req, rawLimit + 1024 * 1024)
      const file = parseSingleFileMultipart(body, boundary)
      const item = await optionalUploadService.completeItem({
        sessionId: req.params.sessionId,
        itemId: req.params.itemId,
        occurrenceKey,
        ownerActorId: ownerId(req),
        file,
        correlationId,
      })
      console.info('Optional upload item completed', {
        sessionId: req.params.sessionId,
        itemId: req.params.itemId,
        jobId: session.jobId,
        attempt: item.attemptCount,
        status: item.status,
        rawSizeBytes: item.rawSizeBytes,
        durationMs: Date.now() - startedAt,
        reason: item.outcomeCode,
        correlationId,
      })
      return res.json(item)
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  router.get('/upload-sessions', async (req: AuthenticatedRequest, res) => {
    try {
      const jobId = typeof req.query.jobId === 'string' ? req.query.jobId : undefined
      const includeTerminal = req.query.includeTerminal !== 'false'
      const sessions = await optionalUploadService.listOwnedSessions(
        ownerId(req),
        jobId,
        includeTerminal,
      )
      const authorized: typeof sessions = []
      for (const session of sessions) {
        const job = await jobRepo.getById(session.jobId)
        if (job && hasJobAccess(req, job)) authorized.push(session)
      }
      return res.json(authorized)
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  router.get('/upload-sessions/:sessionId', async (req: AuthenticatedRequest, res) => {
    try {
      await requireAuthorizedOwnedSession(req, req.params.sessionId)
      const session = await optionalUploadService.reconcileAndGet(
        req.params.sessionId,
        ownerId(req),
      )
      if (!session) return sendError(req, res, 404, 'not_found', 'Upload session was not found.')
      return res.json(session)
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  router.post('/upload-sessions/:sessionId/heartbeat', async (req: AuthenticatedRequest, res) => {
    try {
      await requireAuthorizedOwnedSession(req, req.params.sessionId)
      const expectedConcurrencyVersion = Number(req.body?.expectedConcurrencyVersion)
      if (!Number.isSafeInteger(expectedConcurrencyVersion) || expectedConcurrencyVersion < 1) {
        return sendError(
          req,
          res,
          422,
          'validation_failed',
          'Optional upload validation failed.',
          { expectedConcurrencyVersion: ['Expected concurrency version must be a positive whole number.'] },
        )
      }
      return res.json(await optionalUploadService.heartbeat(
        req.params.sessionId,
        ownerId(req),
        expectedConcurrencyVersion,
      ))
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  router.patch('/upload-sessions/:sessionId/items/:itemId/status', async (req: AuthenticatedRequest, res) => {
    try {
      await requireAuthorizedOwnedSession(req, req.params.sessionId)
      const allowed = new Set(['waiting', 'throttled', 'retrying', 'interrupted'])
      if (!allowed.has(req.body?.status)) {
        return sendError(
          req,
          res,
          422,
          'validation_failed',
          'Optional upload validation failed.',
          { status: ['Status must be waiting, throttled, retrying, or interrupted.'] },
        )
      }
      return res.json(await optionalUploadService.updateItemStatus(
        req.params.sessionId,
        req.params.itemId,
        ownerId(req),
        req.body,
      ))
    } catch (error) {
      return mapError(req, res, error)
    }
  })

  return router
}
