import JSZip from 'jszip'
import { describe, expect, it } from 'vitest'

import {
  buildApplicationCsv,
  buildApplicationXlsx,
  getApplicationExportFileName,
} from '@/lib/application-export'
import type { Application } from '@/types'

const application: Application = {
  applicationId: 'application-hidden-123',
  jobId: 'job-1',
  candidateRef: 'candidate-ref-1',
  candidateName: '=HYPERLINK("https://example.test")',
  candidateEmail: 'candidate@example.test',
  status: 'Completed',
  createdAt: '2026-09-23T18:30:00.000Z',
  documents: [],
  finalScore: 87.125,
  finalDecision: 'Eligible',
  variance: 2.5,
}

describe('application export', () => {
  it('exports the displayed fields and hidden application ID to formula-safe CSV', () => {
    const csv = buildApplicationCsv([application])

    expect(csv).toContain(
      'Application ID,Candidate,Email,Status,Score,Variance,Decision,Submitted',
    )
    expect(csv).toContain('application-hidden-123')
    expect(csv).toContain(`"'=HYPERLINK(""https://example.test"")"`)
    expect(csv).toContain('candidate@example.test')
    expect(csv).toContain('87.125')
    expect(csv).toContain('2026-09-23T18:30:00.000Z')
  })

  it('creates an XLSX workbook containing the displayed fields and hidden application ID', async () => {
    const bytes = await buildApplicationXlsx([application], 'Shortlist')
    const archive = await JSZip.loadAsync(bytes)
    const worksheet = await archive.file('xl/worksheets/sheet1.xml')?.async('string')
    const workbook = await archive.file('xl/workbook.xml')?.async('string')

    expect(Object.keys(archive.files)).toEqual(expect.arrayContaining([
      '[Content_Types].xml',
      '_rels/.rels',
      'xl/workbook.xml',
      'xl/worksheets/sheet1.xml',
    ]))
    expect(workbook).toContain('name="Shortlist"')
    expect(worksheet).toContain('Application ID')
    expect(worksheet).toContain('application-hidden-123')
    expect(worksheet).toContain('candidate@example.test')
    expect(worksheet).toContain('<v>87.125</v>')
  })

  it('creates a filesystem-safe name for the selected job view', () => {
    expect(getApplicationExportFileName('Platform / Engineer', 'Longlist', 'xlsx'))
      .toBe('platform-engineer-longlist-applications.xlsx')
  })
})
