// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { ApplicationsTable } from '@/components/ApplicationsTable'
import type { Application } from '@/types'

const application: Application = {
  applicationId: 'application-1',
  jobId: 'job-1',
  candidateRef: 'candidate-1',
  candidateName: 'Candidate One',
  status: 'Completed',
  createdAt: '2026-09-23T18:30:00.000Z',
  documents: [],
  finalScore: 90,
  finalDecision: 'Eligible',
  variance: 1,
}

afterEach(cleanup)

describe('ApplicationsTable export menu', () => {
  it('shows CSV and XLSX options when the current view is exportable', () => {
    render(
      <ApplicationsTable
        applications={[application]}
        onApplicationClick={() => undefined}
        exportContext={{ jobTitle: 'Platform Engineer', viewName: 'Longlist' }}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Export longlist applications' }))

    expect(screen.getByRole('menuitem', { name: 'Export as CSV' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Export as XLSX' })).toBeInTheDocument()
  })

  it('does not show the menu for views that do not opt in', () => {
    render(
      <ApplicationsTable
        applications={[application]}
        onApplicationClick={() => undefined}
      />,
    )

    expect(screen.queryByRole('button', { name: /Export .* applications/i })).not.toBeInTheDocument()
  })
})
