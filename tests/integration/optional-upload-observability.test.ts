import { beforeEach, describe, expect, it, vi } from 'vitest'

const auditRepo = vi.hoisted(() => ({ appendEvent: vi.fn() }))
vi.mock('../../server/storage/repos/index.js', () => ({ auditRepo }))

import { auditService } from '../../server/services/audit.js'

describe('optional upload observability safety', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it.each([
    ['contentBase64', 'sensitive'],
    ['bytes', Buffer.from('sensitive')],
    ['accessToken', 'token'],
    ['authorization', 'bearer token'],
    ['secret', 'value'],
  ])('rejects unsafe audit detail %s', async (key, value) => {
    await expect(auditService.appendEvent(
      'actor-1',
      'upload-item.completed',
      'UploadItem',
      'item-1',
      { [key]: value },
      'correlation-1',
    )).rejects.toThrow(/must not contain/i)
    expect(auditRepo.appendEvent).not.toHaveBeenCalled()
  })

  it('persists safe upload dimensions with the supplied correlation ID', async () => {
    auditRepo.appendEvent.mockResolvedValue(undefined)

    const event = await auditService.appendEvent(
      'actor-1',
      'upload-item.completed',
      'UploadItem',
      'item-1',
      {
        sessionId: 'session-1',
        jobId: 'job-1',
        attempt: 2,
        status: 'succeeded',
        rawSizeBytes: 2048,
        durationMs: 50,
        reason: null,
      },
      'correlation-1',
    )

    expect(event.correlationId).toBe('correlation-1')
    expect(event.details).not.toHaveProperty('content')
    expect(auditRepo.appendEvent).toHaveBeenCalledWith(event)
  })
})
