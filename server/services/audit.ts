import { randomUUID } from 'node:crypto'
import type { StorageProvider } from '../storage/types.js'
import type { ProcessingEvent } from '../../src/types/index.js'
import { LEDGER } from '../storage/kv-keys.js'
import { pushToArray } from '../storage/kv-helpers.js'

export function createAuditService(storage: StorageProvider) {
  return {
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
      await pushToArray<ProcessingEvent>(storage, LEDGER, event)
      return event
    },
  }
}