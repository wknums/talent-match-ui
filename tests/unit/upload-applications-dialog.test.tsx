// @vitest-environment jsdom

import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import '@testing-library/jest-dom/vitest'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const { uploadApplications, startUpload, toast } = vi.hoisted(() => ({
  uploadApplications: vi.fn(),
  startUpload: vi.fn(),
  toast: {
    error: vi.fn(),
    success: vi.fn(),
    warning: vi.fn(),
  },
}))

vi.mock('@/lib/api', () => ({
  api: { uploadApplications },
}))
vi.mock('@/providers/UploadCoordinatorProvider', () => ({
  useUploadCoordinator: () => ({ startUpload }),
}))
vi.mock('sonner', () => ({ toast }))
vi.mock('@/components/DraggableResizableDialog', () => ({
  DraggableResizableDialog: ({ open, children }: { open: boolean; children: React.ReactNode }) => open ? <div>{children}</div> : null,
  DraggableDialogHeader: ({ children }: { children: React.ReactNode }) => <header>{children}</header>,
  DraggableDialogBody: ({ children }: { children: React.ReactNode }) => <main>{children}</main>,
  DraggableDialogFooter: ({ children }: { children: React.ReactNode }) => <footer>{children}</footer>,
}))
vi.mock('@/components/ui/dialog', () => ({
  DialogTitle: ({ children }: { children: React.ReactNode }) => <h2>{children}</h2>,
  DialogDescription: ({ children }: { children: React.ReactNode }) => <p>{children}</p>,
}))

import { UploadApplicationsDialog } from '@/components/UploadApplicationsDialog'

function selectFiles(files: File[]) {
  fireEvent.change(screen.getByLabelText(/drop files here/i), {
    target: { files },
  })
}

describe('UploadApplicationsDialog legacy isolation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useRealTimers()
    uploadApplications.mockResolvedValue({ applicationIds: ['application-1'], warnings: [] })
  })

  it('defaults both duplicate and optional choices off for each new flow', () => {
    const view = render(
      <UploadApplicationsDialog open jobId="job-1" onClose={vi.fn()} />,
    )

    expect(screen.getByRole('checkbox', { name: /allow duplicate documents/i })).not.toBeChecked()
    expect(screen.getByRole('checkbox', { name: /allow parallel individual uploads/i })).not.toBeChecked()

    fireEvent.click(screen.getByRole('checkbox', { name: /allow parallel individual uploads/i }))
    view.rerender(<UploadApplicationsDialog open={false} jobId={null} onClose={vi.fn()} />)
    view.rerender(<UploadApplicationsDialog open jobId="job-2" onClose={vi.fn()} />)

    expect(screen.getByRole('checkbox', { name: /allow parallel individual uploads/i })).not.toBeChecked()
  })

  it('keeps validation, duplicate default, one bulk call, results, and close blocking unchanged when off', async () => {
    vi.useFakeTimers()
    const onClose = vi.fn()
    const onSuccess = vi.fn()
    const valid = new File(['cv'], 'candidate.pdf', { type: 'application/pdf' })
    const invalid = new File(['image'], 'candidate.gif', { type: 'image/gif' })
    let resolveUpload: ((value: { applicationIds: string[]; warnings: string[] }) => void) | undefined
    uploadApplications.mockReturnValue(new Promise(resolve => { resolveUpload = resolve }))
    render(
      <UploadApplicationsDialog
        open
        jobId="job-1"
        onClose={onClose}
        onSuccess={onSuccess}
      />,
    )

    selectFiles([invalid, valid])
    expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('Unsupported file type'))
    fireEvent.click(screen.getByRole('button', { name: 'Upload 1 file(s)' }))

    expect(uploadApplications).toHaveBeenCalledOnce()
    expect(uploadApplications).toHaveBeenCalledWith('job-1', [valid], false)
    expect(startUpload).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onClose).not.toHaveBeenCalled()

    await act(async () => {
      resolveUpload?.({ applicationIds: ['application-1'], warnings: [] })
      await Promise.resolve()
    })
    expect(toast.success).toHaveBeenCalledWith('Successfully uploaded 1 application(s)')

    await act(async () => {
      vi.advanceTimersByTime(500)
    })
    expect(onSuccess).toHaveBeenCalledOnce()
    expect(onClose).toHaveBeenCalledOnce()
  })

  it('preserves skipped-result interpretation on the legacy path', async () => {
    uploadApplications.mockResolvedValue({ applicationIds: [], warnings: ['duplicate'] })
    render(<UploadApplicationsDialog open jobId="job-1" onClose={vi.fn()} />)
    selectFiles([new File(['cv'], 'candidate.pdf', { type: 'application/pdf' })])

    fireEvent.click(screen.getByRole('button', { name: 'Upload 1 file(s)' }))

    await waitFor(() => {
      expect(toast.warning).toHaveBeenCalledWith('1 duplicate application(s) skipped')
    })
    expect(startUpload).not.toHaveBeenCalled()
  })
})
