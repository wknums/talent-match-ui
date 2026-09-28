import JSZip from 'jszip'

import type { Application } from '@/types'

export type ApplicationExportFormat = 'csv' | 'xlsx'

interface ApplicationExportRow {
  applicationId: string
  candidate: string
  email: string
  status: string
  score?: number
  variance?: number
  decision: string
  submitted: string
}

const headers = [
  'Application ID',
  'Candidate',
  'Email',
  'Status',
  'Score',
  'Variance',
  'Decision',
  'Submitted',
] as const

const spreadsheetNamespace = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'

function getRows(applications: Application[]): ApplicationExportRow[] {
  return applications.map((application) => ({
    applicationId: application.applicationId,
    candidate: application.candidateName?.trim() || application.candidateRef,
    email: application.candidateEmail ?? '',
    status: application.status,
    score: application.finalScore,
    variance: application.variance,
    decision: application.finalDecision ?? '',
    submitted: new Date(application.createdAt).toISOString(),
  }))
}

function protectCsvFormula(value: string): string {
  return /^\s*[=+\-@]/.test(value) ? `'${value}` : value
}

function escapeCsv(value: string | number | undefined): string {
  if (value === undefined) return ''

  const text = typeof value === 'string' ? protectCsvFormula(value) : String(value)
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;')
}

function columnName(index: number): string {
  let value = index + 1
  let name = ''

  while (value > 0) {
    const remainder = (value - 1) % 26
    name = String.fromCharCode(65 + remainder) + name
    value = Math.floor((value - 1) / 26)
  }

  return name
}

function stringCell(reference: string, value: string): string {
  return `<c r="${reference}" t="inlineStr"><is><t xml:space="preserve">${escapeXml(value)}</t></is></c>`
}

function numberCell(reference: string, value: number | undefined): string {
  return value === undefined ? `<c r="${reference}"/>` : `<c r="${reference}"><v>${value}</v></c>`
}

function buildWorksheetXml(applications: Application[]): string {
  const rows = getRows(applications)
  const headerCells = headers
    .map((header, index) => stringCell(`${columnName(index)}1`, header))
    .join('')
  const dataRows = rows.map((row, rowIndex) => {
    const excelRow = rowIndex + 2
    const values = [
      stringCell(`A${excelRow}`, row.applicationId),
      stringCell(`B${excelRow}`, row.candidate),
      stringCell(`C${excelRow}`, row.email),
      stringCell(`D${excelRow}`, row.status),
      numberCell(`E${excelRow}`, row.score),
      numberCell(`F${excelRow}`, row.variance),
      stringCell(`G${excelRow}`, row.decision),
      stringCell(`H${excelRow}`, row.submitted),
    ].join('')
    return `<row r="${excelRow}">${values}</row>`
  }).join('')
  const lastRow = Math.max(rows.length + 1, 1)

  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<worksheet xmlns="${spreadsheetNamespace}">
  <cols>
    <col min="1" max="1" width="38" customWidth="1"/>
    <col min="2" max="4" width="24" customWidth="1"/>
    <col min="5" max="7" width="14" customWidth="1"/>
    <col min="8" max="8" width="26" customWidth="1"/>
  </cols>
  <sheetData><row r="1">${headerCells}</row>${dataRows}</sheetData>
  <autoFilter ref="A1:H${lastRow}"/>
</worksheet>`
}

function sanitizeWorksheetName(value: string): string {
  const sanitized = value.replace(/[[\]:*?/\\]/g, ' ').trim()
  return (sanitized || 'Applications').slice(0, 31)
}

export function buildApplicationCsv(applications: Application[]): string {
  const rows = getRows(applications)
  const lines = [
    headers.join(','),
    ...rows.map((row) => [
      row.applicationId,
      row.candidate,
      row.email,
      row.status,
      row.score,
      row.variance,
      row.decision,
      row.submitted,
    ].map(escapeCsv).join(',')),
  ]

  return `\uFEFF${lines.join('\r\n')}`
}

export async function buildApplicationXlsx(
  applications: Application[],
  worksheetName: string,
): Promise<Uint8Array> {
  const zip = new JSZip()
  const safeWorksheetName = escapeXml(sanitizeWorksheetName(worksheetName))

  zip.file('[Content_Types].xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
</Types>`)
  zip.file('_rels/.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>`)
  zip.file('xl/workbook.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="${spreadsheetNamespace}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="${safeWorksheetName}" sheetId="1" r:id="rId1"/></sheets>
</workbook>`)
  zip.file('xl/_rels/workbook.xml.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
</Relationships>`)
  zip.file('xl/worksheets/sheet1.xml', buildWorksheetXml(applications))

  return zip.generateAsync({
    type: 'uint8array',
    compression: 'DEFLATE',
    compressionOptions: { level: 6 },
  })
}

export function getApplicationExportFileName(
  jobTitle: string,
  viewName: string,
  format: ApplicationExportFormat,
): string {
  const slug = `${jobTitle}-${viewName}-applications`
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')

  return `${slug || 'applications'}.${format}`
}

export async function downloadApplicationExport(
  applications: Application[],
  jobTitle: string,
  viewName: string,
  format: ApplicationExportFormat,
): Promise<void> {
  const content = format === 'csv'
    ? buildApplicationCsv(applications)
    : await buildApplicationXlsx(applications, viewName)
  const blob = new Blob(
    [content],
    {
      type: format === 'csv'
        ? 'text/csv;charset=utf-8'
        : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    },
  )
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')

  try {
    anchor.href = url
    anchor.download = getApplicationExportFileName(jobTitle, viewName, format)
    document.body.append(anchor)
    anchor.click()
  } finally {
    anchor.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 0)
  }
}
