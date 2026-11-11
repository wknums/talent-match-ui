import { renderToString } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'

vi.mock('@/lib/api', () => ({
  api: {
    listExtractionInstructions: vi.fn(),
    getExtractionInstruction: vi.fn(),
    createExtractionInstructionDraft: vi.fn(),
    validateExtractionInstruction: vi.fn(),
    activateExtractionInstruction: vi.fn(),
    getSystemPromptGenerationInstructions: vi.fn(),
    createSystemPromptGenerationInstruction: vi.fn(),
    activateSystemPromptGenerationInstruction: vi.fn(),
  },
}))

import { ExtractionInstructionAdmin } from '@/components/ExtractionInstructionAdmin'

describe('ExtractionInstructionAdmin', () => {
  it('renders the draft-management shell when open', () => {
    const html = renderToString(<ExtractionInstructionAdmin open onClose={() => undefined} />)
    expect(html).toContain('Extraction Instruction Versions')
    expect(html).toContain('Create draft')
    expect(html).toContain('Protected contract')
    expect(html).toContain('System Scoring Prompt Generation')
    expect(html).toContain('Create scoring draft')
  })
})
