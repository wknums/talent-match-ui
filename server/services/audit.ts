import { randomUUID } from 'node:crypto'
import type { ProcessingEvent } from '../../src/types/index.js'
import { auditRepo } from '../storage/repos/index.js'

export const auditService = {
  async appendEvent(
    actor: string,
    eventType: string,
    entityType: string,
    entityId: string,
    payload: Record<string, any>,
    correlationId?: string,
  ): Promise<ProcessingEvent> {
    const event: ProcessingEvent = {
      eventId: randomUUID(),
      timestamp: new Date().toISOString(),
      actor,
      action: eventType,
      entityType,
      entityId,
      correlationId: correlationId || randomUUID(),
      details: payload,
    }
    await auditRepo.appendEvent(event)
    return event
  },
}

/** @deprecated Use auditService directly instead */
export function createAuditService(_storage?: unknown) {
  return auditService
}