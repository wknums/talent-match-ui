import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import type { RequirementType, RubricCategoryV2, RubricEnvelope, RubricItem } from '@/types'
import { RubricItemCard } from './RubricItemCard'

const NEEDS_REVIEW_CATEGORY_ID = 'cat-needs-review'

export function moveRubricItem(
  rubric: RubricEnvelope,
  itemId: string,
  targetCategoryId: string,
  beforeItemId?: string,
): RubricEnvelope {
  const moving = rubric.items.find(item => item.id === itemId)
  if (!moving) return rubric

  const remaining = rubric.items.filter(item => item.id !== itemId)
  const targetItems = remaining.filter(item => item.categoryId === targetCategoryId).sort((a, b) => a.order - b.order)
  const insertAt = beforeItemId ? Math.max(0, targetItems.findIndex(item => item.id === beforeItemId)) : targetItems.length
  return insertRubricItemAt(rubric, moving, targetCategoryId, insertAt)
}

export function reorderRubricItem(
  rubric: RubricEnvelope,
  itemId: string,
  targetCategoryId: string,
  targetIndex: number,
): RubricEnvelope {
  const moving = rubric.items.find(item => item.id === itemId)
  if (!moving) return rubric
  return insertRubricItemAt(rubric, moving, targetCategoryId, targetIndex)
}

export function addRubricItem(rubric: RubricEnvelope, categoryId: string, text: string, requirementType: RequirementType): RubricEnvelope {
  const siblingCount = rubric.items.filter(item => item.categoryId === categoryId).length
  return reindexRubric({
    ...rubric,
    items: [
      ...rubric.items,
      {
        id: `item-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        categoryId,
        text,
        requirementType,
        order: siblingCount,
        sourceText: text,
        sourceLocation: null,
        sourceRequirementId: null,
        reviewStatus: 'confirmed',
        createdFrom: 'manual',
      },
    ],
  })
}

export function editRubricItem(rubric: RubricEnvelope, itemId: string, updates: Partial<Pick<RubricItem, 'text' | 'requirementType'>>): RubricEnvelope {
  return {
    ...rubric,
    items: rubric.items.map(item => item.id === itemId ? { ...item, ...updates, sourceText: item.sourceText || updates.text || item.text } : item),
  }
}

export function removeRubricItem(rubric: RubricEnvelope, itemId: string): RubricEnvelope {
  return reindexRubric({
    ...rubric,
    items: rubric.items.filter(item => item.id !== itemId),
  })
}

function insertRubricItemAt(
  rubric: RubricEnvelope,
  moving: RubricItem,
  targetCategoryId: string,
  targetIndex: number,
): RubricEnvelope {
  const remaining = rubric.items.filter(item => item.id !== moving.id)
  const targetItems = remaining
    .filter(item => item.categoryId === targetCategoryId)
    .sort((a, b) => a.order - b.order)
  const clampedIndex = Math.max(0, Math.min(targetIndex, targetItems.length))
  const reorderedTargetItems = [...targetItems]
  reorderedTargetItems.splice(clampedIndex, 0, {
    ...moving,
    categoryId: targetCategoryId,
    order: clampedIndex,
  })

  return reindexRubric({
    ...rubric,
    items: [
      ...remaining.filter(item => item.categoryId !== targetCategoryId),
      ...reorderedTargetItems,
    ],
  })
}

export function reindexRubric(rubric: RubricEnvelope): RubricEnvelope {
  const categories = ensureRenderableCategories(rubric)
  return {
    ...rubric,
    categories,
    items: [...categories]
      .sort((a, b) => a.order - b.order)
      .flatMap(category => rubric.items
        .filter(item => item.categoryId === category.id)
        .sort((a, b) => a.order - b.order || a.text.localeCompare(b.text))
        .map((item, index) => ({ ...item, order: index }))),
  }
}

function ensureRenderableCategories(rubric: RubricEnvelope): RubricCategoryV2[] {
  const orderedCategories = [...rubric.categories]
    .sort((a, b) => a.order - b.order)
    .map((category, index) => ({ ...category, order: index }))

  const knownIds = new Set(orderedCategories.map(category => category.id))
  const hasNeedsReviewItems = rubric.items.some(item => item.categoryId === NEEDS_REVIEW_CATEGORY_ID)
  if (!hasNeedsReviewItems || knownIds.has(NEEDS_REVIEW_CATEGORY_ID)) {
    return orderedCategories
  }

  return [
    ...orderedCategories,
    {
      id: NEEDS_REVIEW_CATEGORY_ID,
      name: 'Needs review',
      weight: 0,
      description: 'Review and reassign ambiguous requirements before approval.',
      order: orderedCategories.length,
    },
  ]
}

interface RubricEditorProps {
  rubric: RubricEnvelope
  editable?: boolean
  onChange?: (rubric: RubricEnvelope) => void
}

export function RubricEditor({ rubric, editable = false, onChange }: RubricEditorProps) {
  const [draggingItemId, setDraggingItemId] = useState<string | null>(null)
  const [draftTextByCategory, setDraftTextByCategory] = useState<Record<string, string>>({})
  const [draftTypeByCategory, setDraftTypeByCategory] = useState<Record<string, RequirementType>>({})
  const [editingItemId, setEditingItemId] = useState<string | null>(null)
  const [editText, setEditText] = useState('')
  const [editType, setEditType] = useState<RequirementType>('other')
  const [selectedCategoryByItem, setSelectedCategoryByItem] = useState<Record<string, string>>({})
  const [announcement, setAnnouncement] = useState('')

  const ordered = useMemo(() => reindexRubric(rubric), [rubric])

  const commit = (next: RubricEnvelope) => {
    onChange?.(reindexRubric(next))
  }

  return (
    <div className="space-y-4">
      <div className="sr-only" aria-live="polite">{announcement}</div>
      {ordered.categories.map((category) => {
        const items = ordered.items.filter(item => item.categoryId === category.id)
        return (
          <div
            key={category.id}
            className="rounded-lg border bg-card p-4"
            data-category-id={category.id}
            onDragOver={(event) => event.preventDefault()}
            onDrop={() => {
              if (editable && draggingItemId) {
                const draggedItem = ordered.items.find(item => item.id === draggingItemId)
                commit(moveRubricItem(ordered, draggingItemId, category.id))
                if (draggedItem) {
                  setAnnouncement(`Moved ${draggedItem.text} to ${category.name}.`)
                }
              }
              setDraggingItemId(null)
            }}
          >
            <div className="mb-3 flex items-center justify-between">
              <div>
                <div className="font-semibold">{category.name}</div>
                {category.description && <div className="text-sm text-muted-foreground">{category.description}</div>}
              </div>
              <div className="text-sm text-muted-foreground">{(category.weight * 100).toFixed(0)}%</div>
            </div>

            <div className="space-y-2">
              {items.length === 0 && (
                <div className="rounded-md border border-dashed p-3 text-sm text-muted-foreground">
                  No rubric items are assigned to this category.
                </div>
              )}
              {items.map((item, index) => (
                <div key={item.id} className="space-y-2">
                  <div
                    data-item-id={item.id}
                    draggable={editable}
                    onDragStart={() => setDraggingItemId(item.id)}
                    onDragOver={(event) => event.preventDefault()}
                    onDrop={() => {
                      if (editable && draggingItemId && draggingItemId !== item.id) {
                        commit(moveRubricItem(ordered, draggingItemId, category.id, item.id))
                        const draggedItem = ordered.items.find(candidate => candidate.id === draggingItemId)
                        if (draggedItem) {
                          setAnnouncement(`Moved ${draggedItem.text} before ${item.text} in ${category.name}.`)
                        }
                      }
                      setDraggingItemId(null)
                    }}
                  >
                    {editable && editingItemId === item.id ? (
                      <div className="space-y-2 rounded-md border bg-background p-3">
                        <Input value={editText} onChange={(event) => setEditText(event.target.value)} />
                        <Select value={editType} onValueChange={(value) => setEditType(value as RequirementType)}>
                          <SelectTrigger><SelectValue /></SelectTrigger>
                          <SelectContent>
                            {['must_have', 'desired', 'experience', 'responsibility', 'other'].map(value => (
                              <SelectItem key={value} value={value}>{value}</SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                        <div className="flex gap-2">
                          <Button size="sm" onClick={() => { commit(editRubricItem(ordered, item.id, { text: editText, requirementType: editType })); setEditingItemId(null) }}>Save</Button>
                          <Button size="sm" variant="outline" onClick={() => setEditingItemId(null)}>Cancel</Button>
                        </div>
                      </div>
                    ) : (
                      <RubricItemCard item={item} />
                    )}
                  </div>

                  {editable && (
                    <div className="flex flex-wrap items-center gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        data-action="move-up"
                        onClick={() => {
                          if (index === 0) return
                          commit(reorderRubricItem(ordered, item.id, category.id, index - 1))
                          setAnnouncement(`Moved ${item.text} up in ${category.name}.`)
                        }}
                        disabled={index === 0}
                      >
                        Move up
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        data-action="move-down"
                        onClick={() => {
                          if (index === items.length - 1) return
                          commit(reorderRubricItem(ordered, item.id, category.id, index + 1))
                          setAnnouncement(`Moved ${item.text} down in ${category.name}.`)
                        }}
                        disabled={index === items.length - 1}
                      >
                        Move down
                      </Button>
                      <Select
                        value={selectedCategoryByItem[item.id] ?? item.categoryId}
                        onValueChange={(value) => setSelectedCategoryByItem(current => ({ ...current, [item.id]: value }))}
                      >
                        <SelectTrigger className="w-[220px]"><SelectValue /></SelectTrigger>
                        <SelectContent>
                          {ordered.categories.map(option => (
                            <SelectItem key={option.id} value={option.id}>{option.name}</SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                      <Button
                        size="sm"
                        variant="outline"
                        data-action="apply-move"
                        onClick={() => {
                          const destinationCategoryId = selectedCategoryByItem[item.id] ?? item.categoryId
                          const destinationCategory = ordered.categories.find(option => option.id === destinationCategoryId)
                          commit(moveRubricItem(ordered, item.id, destinationCategoryId))
                          if (destinationCategory) {
                            setAnnouncement(`Moved ${item.text} to ${destinationCategory.name}.`)
                          }
                        }}
                      >
                        Apply move
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        data-edit-item={item.id}
                        onClick={() => { setEditingItemId(item.id); setEditText(item.text); setEditType(item.requirementType) }}
                      >
                        Edit
                      </Button>
                      <Button
                        size="sm"
                        variant="destructive"
                        data-action="remove-item"
                        onClick={() => {
                          commit(removeRubricItem(ordered, item.id))
                          setAnnouncement(`Removed ${item.text}.`)
                        }}
                      >
                        Remove
                      </Button>
                    </div>
                  )}
                </div>
              ))}
            </div>

            {editable && (
              <div className="mt-3 flex flex-wrap items-center gap-2 border-t pt-3">
                <Input
                  placeholder="Add item text"
                  value={draftTextByCategory[category.id] ?? ''}
                  onChange={(event) => setDraftTextByCategory(current => ({ ...current, [category.id]: event.target.value }))}
                  className="min-w-[280px] flex-1"
                />
                <Select
                  value={draftTypeByCategory[category.id] ?? 'other'}
                  onValueChange={(value) => setDraftTypeByCategory(current => ({ ...current, [category.id]: value as RequirementType }))}
                >
                  <SelectTrigger className="w-[180px]"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {['must_have', 'desired', 'experience', 'responsibility', 'other'].map(value => (
                      <SelectItem key={value} value={value}>{value}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button
                  size="sm"
                  onClick={() => {
                    const text = (draftTextByCategory[category.id] ?? '').trim()
                    if (!text) return
                    commit(addRubricItem(ordered, category.id, text, draftTypeByCategory[category.id] ?? 'other'))
                    setAnnouncement(`Added ${text} to ${category.name}.`)
                    setDraftTextByCategory(current => ({ ...current, [category.id]: '' }))
                  }}
                >
                  Add item
                </Button>
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
