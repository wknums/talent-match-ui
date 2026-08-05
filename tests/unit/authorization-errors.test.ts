import { describe, expect, it, vi } from 'vitest'
import type { Request, Response } from 'express'
import {
  ensureCorrelationId,
  mapAuthorizationError,
  sendAuthorizationError,
} from '../../server/services/authorization-errors.js'

describe('canonical Stack A authorization errors', () => {
  it.each([
    ['wrong_tenant', 401],
    ['token_stale', 401],
    ['assignment_missing', 403],
    ['identity_disabled', 403],
    ['invalid_scope', 400],
    ['forbidden', 403],
    ['not_found', 404],
    ['version_conflict', 409],
    ['invalid_job_scope', 403],
  ])('maps %s to a stable lowercase code', (code, statusCode) => {
    expect(mapAuthorizationError(
      { code: code.toUpperCase(), statusCode, message: 'Public detail.' },
      { code: 'invalid_scope', statusCode: 500 },
    )).toEqual({ code, statusCode, message: 'Public detail.' })
  })

  it('maps the legacy organization conflict code without changing its 409 status', () => {
    expect(mapAuthorizationError(
      { code: 'conflict', statusCode: 409, message: 'Current state conflicts.' },
      { code: 'version_conflict', statusCode: 409 },
    )).toEqual({
      code: 'version_conflict',
      statusCode: 409,
      message: 'Current state conflicts.',
    })
  })

  it('supports context-specific invalid job scope status semantics', () => {
    expect(mapAuthorizationError({}, {
      code: 'invalid_job_scope',
      statusCode: 400,
      message: 'Job organization and department must be a valid active pair.',
    })).toMatchObject({ code: 'invalid_job_scope', statusCode: 400 })
  })

  it('uses the canonical safe message when internal details must not be exposed', () => {
    expect(mapAuthorizationError(
      { code: 'wrong_tenant', statusCode: 401, message: 'internal tenant detail' },
      { code: 'invalid_token', statusCode: 401 },
      { preserveMessage: false },
    )).toEqual({
      code: 'wrong_tenant',
      statusCode: 401,
      message: 'The access token tenant is not authorized.',
    })
  })

  it('does not expose messages from errors without a recognized public code', () => {
    expect(mapAuthorizationError(
      new Error('database connection detail'),
      {
        code: 'invalid_scope',
        statusCode: 500,
        message: 'The operation could not be completed.',
      },
    )).toEqual({
      code: 'invalid_scope',
      statusCode: 500,
      message: 'The operation could not be completed.',
    })
  })

  it('keeps the response header and envelope correlation IDs equal', () => {
    const headers = new Map<string, string>()
    const req = { get: vi.fn().mockReturnValue('correlation-from-request') } as unknown as Request
    const json = vi.fn().mockReturnValue({})
    const res = {
      getHeader: vi.fn((name: string) => headers.get(name)),
      setHeader: vi.fn((name: string, value: string) => headers.set(name, value)),
      status: vi.fn().mockReturnThis(),
      json,
    } as unknown as Response

    expect(ensureCorrelationId(req, res)).toBe('correlation-from-request')
    sendAuthorizationError(req, res, {
      code: 'forbidden',
      statusCode: 403,
      message: 'Access denied.',
    })

    expect(res.status).toHaveBeenCalledWith(403)
    expect(json).toHaveBeenCalledWith({
      error: 'forbidden',
      message: 'Access denied.',
      correlationId: 'correlation-from-request',
    })
    expect(headers.get('X-Correlation-ID')).toBe('correlation-from-request')
  })
})