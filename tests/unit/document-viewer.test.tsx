// @vitest-environment jsdom

import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DocumentViewer } from '@/components/DocumentViewer'
import { api } from '@/lib/api'
import type { ApplicationDocument } from '@/types'

vi.mock('@/lib/api', () => ({
  api: {
    getDocumentContent: vi.fn(),
  },
}))

const document: ApplicationDocument = {
  documentId: 'document-1',
  applicationId: 'application-1',
  fileName: 'resume.pdf',
  mimeType: 'application/pdf',
  sizeBytes: 4,
  sha256: 'sha256',
  uploadedAt: '2026-09-11T00:00:00.000Z',
}

describe('DocumentViewer', () => {
  const createObjectURL = vi.fn(() => 'blob:authenticated-document')
  const revokeObjectURL = vi.fn()

  beforeEach(() => {
    vi.mocked(api.getDocumentContent).mockResolvedValue(new Uint8Array([37, 80, 68, 70]).buffer)
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL })
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectURL })
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('loads a PDF through the API client and previews the authenticated blob', async () => {
    const view = render(
      <DocumentViewer applicationId="application-1" document={document} />,
    )

    const frame = await screen.findByTitle('resume.pdf')

    expect(api.getDocumentContent).toHaveBeenCalledWith('application-1', 'document-1')
    expect(frame.getAttribute('src')).toBe('blob:authenticated-document')
    expect(createObjectURL).toHaveBeenCalledOnce()

    view.unmount()
    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:authenticated-document'))
  })
})
