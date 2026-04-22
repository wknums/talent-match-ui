import { describe, expect, it } from 'vitest'
import { resolveAwrHealthUrl, summarizeHealth } from '../../server/services/health.js'

describe('health helpers', () => {
  it('resolves the AWR health URL from an API endpoint', () => {
    expect(resolveAwrHealthUrl('https://example.com/api')).toBe('https://example.com/healthz')
  })

  it('returns null when the AWR endpoint is not configured', () => {
    expect(resolveAwrHealthUrl('')).toBeNull()
    expect(resolveAwrHealthUrl(undefined)).toBeNull()
  })

  it('returns null when the AWR endpoint is invalid', () => {
    expect(resolveAwrHealthUrl('not-a-url')).toBeNull()
  })

  it('marks overall health as unhealthy when any dependency check fails', () => {
    expect(
      summarizeHealth([
        { name: 'awr-api', status: 'ok' },
        { name: 'azure-sql', status: 'failed' },
      ]),
    ).toBe('unhealthy')
  })

  it('keeps overall health ok when checks are ok or skipped', () => {
    expect(
      summarizeHealth([
        { name: 'awr-api', status: 'ok' },
        { name: 'azure-sql', status: 'skipped' },
      ]),
    ).toBe('ok')
  })
})
