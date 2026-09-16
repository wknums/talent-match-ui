import { renderToString } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  api: {
    listExtractionInstructions: vi.fn(),
    getExtractionInstruction: vi.fn(),
    createExtractionInstructionDraft: vi.fn(),
    validateExtractionInstruction: vi.fn(),
    activateExtractionInstruction: vi.fn(),
  },
}))

import { ExtractionInstructionAdmin } from '@/components/ExtractionInstructionAdmin'

describe('ExtractionInstructionAdmin', () => {
  it('renders the draft-management shell when open', () => {
    const html = renderToString(<ExtractionInstructionAdmin open onClose={() => undefined} />)
    expect(html).toContain('Extraction Instruction Versions')
    expect(html).toContain('Create draft')
    expect(html).toContain('Protected contract')
  })
})
