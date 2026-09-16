import { renderToString } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import {
  RubricEditor,
  addRubricItem,
  editRubricItem,
  moveRubricItem,
  removeRubricItem,
  reorderRubricItem,
} from '@/components/RubricEditor'
import type { RubricEnvelope } from '@/types'

function sampleRubric(): RubricEnvelope {
  return {
    schemaVersion: 'rubric-v2',
    legacySourceVersionId: null,
    categories: [
      { id: 'cat-1', name: 'Technical Skills', weight: 0.7, description: 'Core technical match', order: 0 },
      { id: 'cat-2', name: 'Communication', weight: 0.3, description: 'Communication skills', order: 1 },
    ],
    items: [
      { id: 'item-1', categoryId: 'cat-1', text: 'Expert SQL experience', requirementType: 'must_have', order: 0, sourceText: 'Expert SQL experience', reviewStatus: 'confirmed', createdFrom: 'extracted' },
      { id: 'item-2', categoryId: 'cat-1', text: 'Expert Python experience', requirementType: 'must_have', order: 1, sourceText: 'Expert Python experience', reviewStatus: 'confirmed', createdFrom: 'extracted' },
    ],
  }
}

describe('RubricEditor helpers', () => {
  it('moves items across categories and preserves source trace', () => {
    const next = moveRubricItem(sampleRubric(), 'item-1', 'cat-2')
    expect(next.items.find(item => item.id === 'item-1')).toMatchObject({
      categoryId: 'cat-2',
      sourceText: 'Expert SQL experience',
    })
  })

  it('reorders items downward within the same category', () => {
    const next = reorderRubricItem(sampleRubric(), 'item-1', 'cat-1', 1)
    expect(next.items.filter(item => item.categoryId === 'cat-1').map(item => item.id)).toEqual(['item-2', 'item-1'])
  })

  it('adds, edits, and removes manual items', () => {
    const added = addRubricItem(sampleRubric(), 'cat-2', 'Lead technical presentations', 'responsibility')
    const addedItem = added.items.find(item => item.text === 'Lead technical presentations')
    expect(addedItem).toBeTruthy()

    const edited = editRubricItem(added, addedItem!.id, { text: 'Lead architecture reviews', requirementType: 'desired' })
    expect(edited.items.find(item => item.id === addedItem!.id)).toMatchObject({ text: 'Lead architecture reviews', requirementType: 'desired' })

    const removed = removeRubricItem(edited, addedItem!.id)
    expect(removed.items.find(item => item.id === addedItem!.id)).toBeUndefined()
  })
})

describe('RubricEditor rendering', () => {
  it('renders empty categories, a synthetic needs-review group, and item review badges', () => {
    const html = renderToString(<RubricEditor
      rubric={{
        ...sampleRubric(),
        items: [
          {
            id: 'item-needs-review',
            categoryId: 'cat-needs-review',
            text: 'Translate business requirements into maintainable scoring rubrics',
            requirementType: 'responsibility',
            order: 0,
            sourceText: 'Partner with recruiters and hiring managers to translate business requirements into maintainable scoring rubrics.',
            reviewStatus: 'needs_review',
            createdFrom: 'extracted',
          },
        ],
      }}
      editable
    />)

    expect(html).toContain('No rubric items are assigned to this category.')
    expect(html).toContain('Review and reassign ambiguous requirements before approval.')
    expect(html).toContain('Needs review')
    expect(html).toContain('data-action="move-up"')
    expect(html).toContain('Partner with recruiters and hiring managers to translate business requirements')
  })

  it('exposes move controls for accessible reordering flows', () => {
    const onChange = vi.fn()
    const html = renderToString(<RubricEditor rubric={sampleRubric()} editable onChange={onChange} />)
    expect(onChange).not.toHaveBeenCalled()
    expect(html).toContain('Move up')
    expect(html).toContain('Move down')
    expect(html).toContain('Apply move')
    expect(html).toContain('Add item')
  })
})
