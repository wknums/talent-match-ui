// @vitest-environment jsdom

import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import '@testing-library/jest-dom/vitest'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { User } from '@/types'

const { getUploadSettings, updateUploadSettings } = vi.hoisted(() => ({
  getUploadSettings: vi.fn(),
  updateUploadSettings: vi.fn(),
}))

vi.mock('@/lib/api', () => ({
  api: { getUploadSettings, updateUploadSettings },
  TalentMatchApiError: class TalentMatchApiError extends Error {},
}))
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

import { OptionalUploadSettings } from '@/components/OptionalUploadSettings'
import { UserMenu } from '@/components/UserMenu'

const user = (role: User['role']): User => ({
  userId: 'user-1',
  username: 'user',
  role,
  fullName: 'User',
  createdAt: new Date().toISOString(),
})

describe('optional upload settings UI', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    getUploadSettings.mockResolvedValue({
      fileConcurrency: 4,
      maxIndividualFileBytes: 4194304,
      maxInFlightBytes: 104857600,
      concurrencyVersion: 0,
      persisted: false,
      updatedAt: null,
      updatedBy: null,
    })
  })

  it('shows System Configuration Settings only to global admins', async () => {
    const onSettings = vi.fn()
    const view = render(
      <UserMenu user={user('admin')} onManageUploadSettings={onSettings} onLogout={vi.fn()} />,
    )
    fireEvent.pointerDown(screen.getByRole('button'), { button: 0 })
    expect(await screen.findByText('System Configuration')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('menuitem', { name: 'Settings' }))
    expect(onSettings).toHaveBeenCalledOnce()

    view.rerender(
      <UserMenu user={user('organization_admin')} onManageUploadSettings={onSettings} onLogout={vi.fn()} />,
    )
    fireEvent.pointerDown(screen.getByRole('button'), { button: 0 })
    expect(screen.queryByRole('menuitem', { name: 'Settings' })).not.toBeInTheDocument()
  })

  it('renders binary-unit defaults without scoring or runner controls', async () => {
    render(<OptionalUploadSettings open onClose={vi.fn()} />)

    expect(await screen.findByLabelText(/file concurrency/i)).toHaveValue(4)
    expect(screen.getByLabelText(/maximum individual file size.*mib/i)).toHaveValue(4)
    expect(screen.getByLabelText(/total in-flight.*mib/i)).toHaveValue(100)
    expect(screen.queryByText(/runner|worker|scoring concurrency/i)).not.toBeInTheDocument()
  })

  it('preserves the last valid display and renders field errors', async () => {
    updateUploadSettings.mockRejectedValue(Object.assign(
      new Error('Validation failed'),
      {
        status: 422,
        validationErrors: {
          maxInFlightBytes: ['Total in-flight bytes must be at least the individual limit.'],
        },
      },
    ))
    render(<OptionalUploadSettings open onClose={vi.fn()} />)
    await screen.findByLabelText(/file concurrency/i)
    fireEvent.change(screen.getByLabelText(/total in-flight.*mib/i), { target: { value: '1' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))

    expect(await screen.findByText(/must be at least/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/total in-flight.*mib/i)).toHaveValue(100)
  })

  it('reloads successfully saved values', async () => {
    updateUploadSettings.mockResolvedValue({
      fileConcurrency: 2,
      maxIndividualFileBytes: 2097152,
      maxInFlightBytes: 5242880,
      concurrencyVersion: 1,
      persisted: true,
      updatedAt: new Date().toISOString(),
      updatedBy: 'admin-1',
    })
    render(<OptionalUploadSettings open onClose={vi.fn()} />)
    await screen.findByLabelText(/file concurrency/i)
    fireEvent.change(screen.getByLabelText(/file concurrency/i), { target: { value: '2' } })
    fireEvent.change(screen.getByLabelText(/maximum individual file size.*mib/i), { target: { value: '2' } })
    fireEvent.change(screen.getByLabelText(/total in-flight.*mib/i), { target: { value: '5' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))

    await waitFor(() => expect(updateUploadSettings).toHaveBeenCalledWith({
      fileConcurrency: 2,
      maxIndividualFileBytes: 2097152,
      maxInFlightBytes: 5242880,
      expectedConcurrencyVersion: 0,
    }))
    expect(screen.getByLabelText(/file concurrency/i)).toHaveValue(2)
  })
})
