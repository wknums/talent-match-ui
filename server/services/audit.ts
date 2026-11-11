import { randomUUID } from 'node:crypto'
import type { ProcessingEvent } from '../../src/types/index.js'
import { auditRepo } from '../storage/repos/index.js'
import type { OrganizationAdminAudit } from './organization-admin.js'

export type AuthorizationAuditAction =
  | 'auth.login.succeeded' | 'auth.login.denied' | 'auth.logout' | 'auth.profile.pending'
  | 'auth.token.stale' | 'auth.role.missing' | 'auth.role.conflict'
  | 'auth.assignment.activated' | 'auth.assignment.revoked'
  | 'auth.membership.activated' | 'auth.membership.revoked'
  | 'auth.group_mapping.updated' | 'auth.scope.denied' | 'auth.scope.unmapped'

export interface AuthorizationAuditDetails {
  subjectObjectId?: string
  tenantId?: string
  role?: string
  organizationId?: string
  departmentId?: string
  source?: string
  result: 'succeeded' | 'denied' | 'revoked' | 'partial' | 'pending'
  reasonCode?: string
}

const forbiddenAuditKeys = new Set([
  'authorization',
  'accesstoken',
  'idtoken',
  'refreshtoken',
  'token',
  'content',
  'contentbase64',
  'bytes',
  'filebytes',
  'secret',
  'credentials',
])

function assertSafeDetails(details: Record<string, unknown>): void {
  const visit = (value: unknown): void => {
    if (!value || typeof value !== 'object') return
    for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
      if (forbiddenAuditKeys.has(key.toLowerCase())) {
        throw new Error(`Audit details must not contain ${key}.`)
      }
      visit(nested)
    }
  }
  visit(details)
}

export const auditService = {
  async appendEvent(
    actor: string,
    eventType: string,
    entityType: string,
    entityId: string,
    payload: Record<string, any>,
    correlationId?: string,
  ): Promise<ProcessingEvent> {
    assertSafeDetails(payload)
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

  async appendAuthorizationEvent(actor: string, action: AuthorizationAuditAction, subjectId: string, details: AuthorizationAuditDetails, correlationId?: string): Promise<ProcessingEvent> {
    return this.appendEvent(actor, action, 'Authorization', subjectId, details as unknown as Record<string, unknown>, correlationId)
  },
}

export const organizationAdminAudit: OrganizationAdminAudit = {
  async record(actor, action, entityType, entityId, details) {
    await auditService.appendEvent(
      actor.objectId,
      action,
      entityType,
      entityId || actor.objectId,
      {
        ...details,
        tenantId: actor.tenantId,
      },
      actor.correlationId,
    )
  },
}

/** @deprecated Use auditService directly instead */
export function createAuditService(_storage?: unknown) {
  return auditService
}