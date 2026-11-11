import { createHash, randomUUID } from 'node:crypto'
import path from 'node:path'
import {
  SUPPORTED_UPLOAD_MIME_TYPES,
  type CreateUploadSessionRequest,
  type SupportedUploadMimeType,
  type UploadItem,
  type UploadSessionDetail,
  type UploadSessionSummary,
  type UpdateUploadItemStatusRequest,
  type UpdateUploadSettingsRequest,
  type UploadSettings,
  type UploadItemStatus,
} from '../../src/types/index.js'
import { uploadRepo } from '../storage/repos/index.js'
import { auditService } from './audit.js'

const extensionMimeTypes: Record<string, SupportedUploadMimeType> = {
  '.pdf': 'application/pdf',
  '.md': 'text/markdown',
  '.docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  '.txt': 'text/plain',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.png': 'image/png',
}

const legalTransitions: Record<UploadItemStatus, ReadonlySet<UploadItemStatus>> = {
  waiting: new Set(['throttled', 'uploading', 'failed', 'interrupted']),
  throttled: new Set(['waiting', 'uploading', 'failed', 'interrupted']),
  uploading: new Set(['retrying', 'succeeded', 'skipped_duplicate', 'failed', 'interrupted']),
  retrying: new Set(['uploading', 'failed', 'interrupted']),
  succeeded: new Set(),
  skipped_duplicate: new Set(),
  failed: new Set(),
  interrupted: new Set(),
}

export function isLegalUploadTransition(
  from: UploadItemStatus,
  to: UploadItemStatus,
): boolean {
  return from === to || legalTransitions[from].has(to)
}

export function isTransientUploadStatus(status: number): boolean {
  return status === 408 || status === 429 || status >= 500
}

export class OptionalUploadError extends Error {
  constructor(
    public readonly code: string,
    message: string,
    public readonly statusCode: number,
    public readonly errors?: Record<string, string[]>,
  ) {
    super(message)
    this.name = 'OptionalUploadError'
  }
}

export function normalizeOptionalUploadMimeType(
  fileName: string,
  mimeType: string,
): SupportedUploadMimeType | null {
  const normalized = mimeType.trim().toLowerCase()
  if (SUPPORTED_UPLOAD_MIME_TYPES.includes(normalized as SupportedUploadMimeType)) {
    return normalized as SupportedUploadMimeType
  }
  if (normalized !== '' && normalized !== 'application/octet-stream') return null
  return extensionMimeTypes[path.extname(fileName).toLowerCase()] ?? null
}

function actorRequired(ownerActorId: string): void {
  if (!ownerActorId.trim()) {
    throw new OptionalUploadError('auth_required', 'Authentication is required.', 401)
  }
}

function safeFileName(fileName: string): string {
  return path.basename(fileName).slice(0, 500)
}

function isPositiveWhole(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0
}

export interface OptionalUploadFile {
  fileName: string
  mimeType: string
  bytes: Buffer
}

export const optionalUploadService = {
  async getSettings(): Promise<UploadSettings> {
    return uploadRepo.getSettings()
  },

  async updateSettings(
    request: UpdateUploadSettingsRequest,
    actorId: string,
    correlationId: string = randomUUID(),
  ): Promise<UploadSettings> {
    actorRequired(actorId)
    const errors: Record<string, string[]> = {}
    if (!isPositiveWhole(request.fileConcurrency)) {
      errors.fileConcurrency = ['File concurrency must be a positive whole number.']
    }
    if (!isPositiveWhole(request.maxIndividualFileBytes)) {
      errors.maxIndividualFileBytes = ['Maximum individual file bytes must be a positive whole number.']
    }
    if (!isPositiveWhole(request.maxInFlightBytes)) {
      errors.maxInFlightBytes = ['Maximum in-flight bytes must be a positive whole number.']
    } else if (
      isPositiveWhole(request.maxIndividualFileBytes)
      && request.maxInFlightBytes < request.maxIndividualFileBytes
    ) {
      errors.maxInFlightBytes = ['Maximum in-flight bytes must be at least the individual file limit.']
    }
    if (!Number.isSafeInteger(request.expectedConcurrencyVersion) || request.expectedConcurrencyVersion < 0) {
      errors.expectedConcurrencyVersion = ['Expected concurrency version must be a non-negative whole number.']
    }
    if (Object.keys(errors).length > 0) {
      throw new OptionalUploadError(
        'validation_failed',
        'Optional upload settings validation failed.',
        422,
        errors,
      )
    }
    const previous = await uploadRepo.getSettings()
    const saved = await uploadRepo.saveSettings({
      ...request,
      actorId,
      now: new Date().toISOString(),
    })
    await auditService.appendEvent(
      actorId,
      'upload-settings.updated',
      'UploadSettings',
      'optional-file-upload',
      {
        previous: {
          fileConcurrency: previous.fileConcurrency,
          maxIndividualFileBytes: previous.maxIndividualFileBytes,
          maxInFlightBytes: previous.maxInFlightBytes,
          concurrencyVersion: previous.concurrencyVersion,
        },
        current: {
          fileConcurrency: saved.fileConcurrency,
          maxIndividualFileBytes: saved.maxIndividualFileBytes,
          maxInFlightBytes: saved.maxInFlightBytes,
          concurrencyVersion: saved.concurrencyVersion,
        },
      },
      correlationId,
    )
    return saved
  },

  async createSession(input: {
    jobId: string
    ownerActorId: string
    ownerDisplayName: string | null
    request: CreateUploadSessionRequest
    correlationId: string
  }): Promise<UploadSessionDetail> {
    actorRequired(input.ownerActorId)
    const createWithLatestSettings = async (): Promise<UploadSessionDetail> => {
    const settings = await uploadRepo.getSettings()
    const requestItems = Array.isArray(input.request.items) ? input.request.items : []
    const duplicateOccurrences = new Set(
      requestItems
        .filter((item, index) => requestItems.findIndex(other => other.occurrenceKey === item.occurrenceKey) !== index)
        .map(item => item.occurrenceKey),
    )
    const duplicateOrdinals = new Set(
      requestItems
        .filter((item, index) => requestItems.findIndex(other => other.ordinal === item.ordinal) !== index)
        .map(item => item.ordinal),
    )
    const now = new Date().toISOString()
    const validationErrors: Record<string, string[]> = {}
    const items = requestItems
      .slice()
      .sort((left, right) => left.ordinal - right.ordinal)
      .map(requested => {
        const errors: string[] = []
        const normalizedMime = normalizeOptionalUploadMimeType(requested.fileName, requested.mimeType)
        if (!requested.fileName?.trim()) errors.push('File name is required.')
        if (requested.fileName.length > 500) errors.push('File name must be 500 characters or fewer.')
        if (!normalizedMime) errors.push('The file type is not supported.')
        if (!Number.isSafeInteger(requested.rawSizeBytes) || requested.rawSizeBytes < 0) {
          errors.push('Raw size must be a non-negative whole number.')
        } else if (requested.rawSizeBytes > settings.maxIndividualFileBytes) {
          errors.push(`The file exceeds the ${settings.maxIndividualFileBytes} byte limit.`)
        }
        if (!Number.isSafeInteger(requested.ordinal) || requested.ordinal < 0) {
          errors.push('Ordinal must be a non-negative whole number.')
        }
        if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(requested.occurrenceKey)) {
          errors.push('Occurrence key must be a UUID.')
        }
        if (duplicateOccurrences.has(requested.occurrenceKey)) {
          errors.push('Occurrence keys must be unique within a session.')
        }
        if (duplicateOrdinals.has(requested.ordinal)) {
          errors.push('Ordinals must be unique within a session.')
        }
        if (errors.length > 0) validationErrors[`items[${requested.ordinal}]`] = errors
        return {
          id: randomUUID(),
          sessionId: '',
          occurrenceKey: requested.occurrenceKey,
          ordinal: requested.ordinal,
          fileName: safeFileName(requested.fileName),
          mimeType: normalizedMime ?? requested.mimeType as SupportedUploadMimeType,
          rawSizeBytes: requested.rawSizeBytes,
          status: errors.length > 0 ? 'failed' as const : 'waiting' as const,
          outcomeCode: errors.some(error => error.includes('byte limit'))
            ? 'size_limit'
            : errors.length > 0 ? 'validation_failed' : null,
          outcomeMessage: errors.length > 0 ? errors.join(' ') : null,
          completedAt: errors.length > 0 ? now : null,
          createdAt: now,
        }
      })
    if (items.length === 0 || items.every(item => item.status === 'failed')) {
      throw new OptionalUploadError(
        'validation_failed',
        'Optional upload validation failed.',
        422,
        Object.keys(validationErrors).length > 0
          ? validationErrors
          : { items: ['Select at least one eligible file.'] },
      )
    }
    const sessionId = randomUUID()
    for (const item of items) item.sessionId = sessionId
    return uploadRepo.createSession({
      id: sessionId,
      jobId: input.jobId,
      ownerActorId: input.ownerActorId,
      ownerDisplayName: input.ownerDisplayName,
      allowDuplicates: input.request.allowDuplicates === true,
      fileConcurrency: settings.fileConcurrency,
      maxIndividualFileBytes: settings.maxIndividualFileBytes,
      maxInFlightBytes: settings.maxInFlightBytes,
      settingsConcurrencyVersion: settings.concurrencyVersion,
      correlationId: input.correlationId,
      createdAt: now,
    }, items)
    }
    for (let attempt = 1; attempt <= 4; attempt += 1) {
      try {
        return await createWithLatestSettings()
      } catch (error) {
        if ((error as { code?: string }).code !== 'settings_stale' || attempt === 4) {
          throw error
        }
      }
    }
    throw new OptionalUploadError(
      'settings_stale',
      'Upload settings changed while the session was being created.',
      409,
    )
  },

  async getOwnedSession(
    sessionId: string,
    ownerActorId: string,
    includeItems = true,
  ): Promise<UploadSessionDetail | undefined> {
    actorRequired(ownerActorId)
    return uploadRepo.getOwnedSession(sessionId, ownerActorId, includeItems)
  },

  async listOwnedSessions(
    ownerActorId: string,
    jobId?: string,
    includeTerminal = true,
  ): Promise<UploadSessionSummary[]> {
    actorRequired(ownerActorId)
    return uploadRepo.listOwnedSessions(ownerActorId, jobId, includeTerminal)
  },

  async completeItem(input: {
    sessionId: string
    itemId: string
    occurrenceKey: string
    ownerActorId: string
    file: OptionalUploadFile
    correlationId: string
  }): Promise<UploadItem> {
    actorRequired(input.ownerActorId)
    const existing = await uploadRepo.getOwnedItem(
      input.sessionId,
      input.itemId,
      input.ownerActorId,
    )
    if (!existing) throw new OptionalUploadError('not_found', 'Upload item was not found.', 404)
    if (['succeeded', 'skipped_duplicate', 'failed', 'interrupted'].includes(existing.status)) {
      return existing
    }
    const session = await uploadRepo.getOwnedSession(input.sessionId, input.ownerActorId, false)
    if (!session) throw new OptionalUploadError('not_found', 'Upload session was not found.', 404)
    const active = await uploadRepo.startItemAttempt(
      input.sessionId,
      input.itemId,
      input.ownerActorId,
      input.occurrenceKey,
      new Date().toISOString(),
    )
    if (['succeeded', 'skipped_duplicate', 'failed', 'interrupted'].includes(active.status)) {
      return active
    }
    const normalizedMime = normalizeOptionalUploadMimeType(
      input.file.fileName,
      input.file.mimeType,
    )
    let validationCode: string | null = null
    let validationMessage: string | null = null
    if (!normalizedMime || normalizedMime !== existing.mimeType) {
      validationCode = 'unsupported_type'
      validationMessage = 'The uploaded content type does not match the selected file.'
    } else if (input.file.bytes.byteLength !== existing.rawSizeBytes) {
      validationCode = 'size_mismatch'
      validationMessage = 'The uploaded raw byte length does not match the selected file metadata.'
    } else if (input.file.bytes.byteLength > session.limits.maxIndividualFileBytes) {
      validationCode = 'size_limit'
      validationMessage = `The file exceeds the ${session.limits.maxIndividualFileBytes} byte session limit.`
    }
    if (validationCode && validationMessage) {
      return uploadRepo.recordItemOutcome(
        input.sessionId,
        input.itemId,
        input.ownerActorId,
        'failed',
        validationCode,
        validationMessage,
        new Date().toISOString(),
        422,
      )
    }
    const fingerprint = createHash('sha256').update(input.file.bytes).digest('hex')
    try {
      return await uploadRepo.completeItem({
        sessionId: input.sessionId,
        itemId: input.itemId,
        ownerActorId: input.ownerActorId,
        occurrenceKey: input.occurrenceKey,
        fingerprint,
        fileName: safeFileName(input.file.fileName),
        mimeType: normalizedMime!,
        rawSizeBytes: input.file.bytes.byteLength,
        contentBase64: input.file.bytes.toString('base64'),
        now: new Date().toISOString(),
      })
    } catch (error) {
      const status = Number((error as { statusCode?: number }).statusCode ?? 503)
      if (isTransientUploadStatus(status)) {
        if (active.attemptCount >= 4) {
          return uploadRepo.recordItemOutcome(
            input.sessionId,
            input.itemId,
            input.ownerActorId,
            'failed',
            'retry_exhausted',
            'Upload failed after four attempts. Try selecting the file again.',
            new Date().toISOString(),
            status,
          )
        }
        const nextRetryAt = new Date(
          Date.now() + Math.min(2000, 250 * 2 ** Math.max(0, active.attemptCount - 1)),
        ).toISOString()
        await uploadRepo.recordItemOutcome(
          input.sessionId,
          input.itemId,
          input.ownerActorId,
          'retrying',
          'transient_failure',
          'A temporary upload problem occurred. The file will be retried.',
          new Date().toISOString(),
          status,
          nextRetryAt,
        )
      } else {
        return uploadRepo.recordItemOutcome(
          input.sessionId,
          input.itemId,
          input.ownerActorId,
          'failed',
          'upload_failed',
          'The file could not be uploaded.',
          new Date().toISOString(),
          status,
        )
      }
      throw error
    }
  },

  async updateItemStatus(
    sessionId: string,
    itemId: string,
    ownerActorId: string,
    request: UpdateUploadItemStatusRequest,
  ): Promise<UploadItem> {
    actorRequired(ownerActorId)
    if (request.transportAttemptCount !== undefined
      && request.transportAttemptCount !== null
      && (!Number.isSafeInteger(request.transportAttemptCount)
        || request.transportAttemptCount < 1
        || request.transportAttemptCount > 4)) {
      throw new OptionalUploadError(
        'validation_failed',
        'The browser transport attempt count is invalid.',
        422,
        { transportAttemptCount: ['Transport attempt count must be a whole number from 1 through 4.'] },
      )
    }
    if (request.status === 'failed' && (
      request.outcomeCode !== 'retry_exhausted'
      || request.transportAttemptCount !== 4
      || !request.outcomeMessage?.trim()
      || request.nextRetryAt
    )) {
      throw new OptionalUploadError(
        'validation_failed',
        'A failed client transition must record exhausted browser transport retries.',
        422,
        { status: ['Failed is allowed only after exactly four browser transport attempts.'] },
      )
    }
    const current = await uploadRepo.getOwnedItem(sessionId, itemId, ownerActorId)
    if (!current) throw new OptionalUploadError('not_found', 'Upload item was not found.', 404)
    if (current.occurrenceKey !== request.occurrenceKey) {
      throw new OptionalUploadError('state_conflict', 'Occurrence key does not match.', 409)
    }
    if (!isLegalUploadTransition(current.status, request.status)) {
      throw new OptionalUploadError(
        'state_conflict',
        `Illegal upload item transition from ${current.status} to ${request.status}.`,
        409,
      )
    }
    return uploadRepo.updateItemStatus(
      sessionId,
      itemId,
      ownerActorId,
      request,
      new Date().toISOString(),
    )
  },

  async heartbeat(
    sessionId: string,
    ownerActorId: string,
    expectedConcurrencyVersion: number,
  ): Promise<UploadSessionSummary> {
    actorRequired(ownerActorId)
    return uploadRepo.heartbeat(
      sessionId,
      ownerActorId,
      expectedConcurrencyVersion,
      new Date().toISOString(),
    )
  },

  async reconcileAndGet(
    sessionId: string,
    ownerActorId: string,
    staleAfterMs = 15_000,
  ): Promise<UploadSessionDetail | undefined> {
    actorRequired(ownerActorId)
    const now = new Date()
    return uploadRepo.reconcileStale(
      sessionId,
      ownerActorId,
      new Date(now.getTime() - staleAfterMs).toISOString(),
      now.toISOString(),
    )
  },
}
