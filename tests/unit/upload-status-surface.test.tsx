// @vitest-environment jsdom

import { render, screen } from '@testing-library/react'
import '@testing-library/jest-dom/vitest'
import { describe, expect, it, vi } from 'vitest'

const session = {
  id: 'session-1',
  jobId: 'job-1',
  status: 'active',
  allowDuplicates: false,
  limits: {
    fileConcurrency: 4,
    maxIndividualFileBytes: 4194304,
    maxInFlightBytes: 104857600,
  },
  counts: {
    total: 4,
    waitingOrThrottled: 1,
    activeOrRetrying: 1,
    succeeded: 1,
    skipped: 0,
    failed: 1,
    interrupted: 0,
    terminal: 2,
  },
  progressPercent: 50,
  correlationId: 'correlation-1',
  createdAt: new Date().toISOString(),
  startedAt: new Date().toISOString(),
  lastHeartbeatAt: new Date().toISOString(),
  completedAt: null,
  concurrencyVersion: 3,
  items: [
    {
      id: 'item-1',
      sessionId: 'session-1',
      occurrenceKey: '11111111-1111-4111-8111-111111111111',
      ordinal: 0,
      fileName: 'waiting.pdf',
      mimeType: 'application/pdf',
      rawSizeBytes: 1024,
      status: 'throttled',
      attemptCount: 0,
      contentFingerprint: null,
      applicationId: null,
      outcomeCode: null,
      outcomeMessage: null,
      nextRetryAt: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      completedAt: null,
      concurrencyVersion: 2,
    },
    {
      id: 'item-2',
      sessionId: 'session-1',
      occurrenceKey: '22222222-2222-4222-8222-222222222222',
      ordinal: 1,
      fileName: 'failed.txt',
      mimeType: 'text/plain',
      rawSizeBytes: 2048,
      status: 'failed',
      attemptCount: 4,
      contentFingerprint: null,
      applicationId: null,
      outcomeCode: 'retry_exhausted',
      outcomeMessage: 'Upload failed after four attempts. Try selecting the file again.',
      nextRetryAt: null,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      concurrencyVersion: 6,
    },
  ],
}

vi.mock('@/providers/UploadCoordinatorProvider', () => ({
  useUploadCoordinator: () => ({ sessions: [session] }),
}))

import { UploadStatusSurface } from '@/components/UploadStatusSurface'

describe('UploadStatusSurface', () => {
  it('renders durable aggregate counts and accessible progress', () => {
    render(<UploadStatusSurface />)

    expect(screen.getByRole('progressbar', { name: /upload progress/i })).toHaveAttribute('aria-valuenow', '50')
    expect(screen.getByText(/4 files/i)).toBeInTheDocument()
    expect(screen.getByText(/1 waiting/i)).toBeInTheDocument()
    expect(screen.getByText(/1 active/i)).toBeInTheDocument()
    expect(screen.getByText(/1 succeeded/i)).toBeInTheDocument()
    expect(screen.getByText(/1 failed/i)).toBeInTheDocument()
  })

  it('renders per-item name, raw size, status, attempts, and actionable reason', () => {
    render(<UploadStatusSurface />)

    expect(screen.getByText('waiting.pdf')).toBeInTheDocument()
    expect(screen.getByText('1.0 KB')).toBeInTheDocument()
    expect(screen.getByText('throttled')).toBeInTheDocument()
    expect(screen.getByText('failed.txt')).toBeInTheDocument()
    expect(screen.getByText('2.0 KB')).toBeInTheDocument()
    expect(screen.getByText(/4 attempts/i)).toBeInTheDocument()
    expect(screen.getByText(/try selecting the file again/i)).toBeInTheDocument()
  })
})
