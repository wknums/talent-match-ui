import type { DesiredCriteria, MustHave, RubricCategory, RubricCategoryV2, RubricEnvelope, RubricItem } from '../../src/types/index.js'
import { normalizeRubricEnvelope } from './extraction-contract.js'

export function isRubricV2(value: unknown): value is RubricEnvelope {
  return Boolean(value && typeof value === 'object' && (value as RubricEnvelope).schemaVersion === 'rubric-v2')
}

export function parseRubricEnvelope(raw: string | null | undefined): RubricEnvelope | undefined {
  if (!raw) return undefined
  try {
    const parsed = JSON.parse(raw)
    return isRubricV2(parsed) ? normalizeRubricEnvelope(parsed) : undefined
  } catch {
    return undefined
  }
}

export function createLegacyConversionProposal(
  legacyRubric: RubricCategory[],
  mustHaves: MustHave[],
  desiredCriteria: DesiredCriteria[],
  legacySourceVersionId: string,
): RubricEnvelope {
  const mustHaveLookup = new Set(mustHaves.map(item => item.criterion.trim().toLowerCase()))
  const desiredLookup = new Set(desiredCriteria.map(item => item.qualification.trim().toLowerCase()))

  const categories: RubricCategoryV2[] = legacyRubric.map((category, index) => ({
    id: slugify(`cat-${category.name}`),
    name: category.name,
    weight: category.weight,
    description: category.description || 'Legacy category preserved during conversion.',
    order: index,
  }))

  const items: RubricItem[] = []
  for (const category of categories) {
    const legacy = legacyRubric.find(candidate => candidate.name === category.name)
    const parts = (legacy?.description || '')
      .split(/[;\n\r]+/)
      .map(part => part.trim())
      .filter(Boolean)

    for (const text of (parts.length ? parts : [category.name])) {
      const normalized = text.toLowerCase()
      items.push({
        id: slugify(`legacy-item-${category.id}-${items.length + 1}`),
        categoryId: category.id,
        text,
        requirementType: mustHaveLookup.has(normalized)
          ? 'must_have'
          : desiredLookup.has(normalized)
            ? 'desired'
            : 'other',
        order: items.filter(item => item.categoryId === category.id).length,
        sourceText: text,
        sourceLocation: null,
        sourceRequirementId: null,
        reviewStatus: 'confirmed',
        createdFrom: 'legacy_conversion',
      })
    }
  }

  return normalizeRubricEnvelope({
    schemaVersion: 'rubric-v2',
    legacySourceVersionId,
    categories,
    items,
  })
}

export function projectLegacyRubric(envelope: RubricEnvelope): RubricCategory[] {
  return envelope.categories
    .sort((left, right) => left.order - right.order)
    .map(category => ({
      id: category.id,
      name: category.name,
      description: envelope.items
        .filter(item => item.categoryId === category.id)
        .sort((left, right) => left.order - right.order)
        .map(item => item.text)
        .join('; '),
      weight: category.weight,
    }))
}

export function projectMustHaves(envelope: RubricEnvelope): MustHave[] {
  return envelope.items
    .filter(item => item.requirementType === 'must_have' || item.requirementType === 'experience')
    .map((item, index) => ({
      id: item.id || `must-have-${index + 1}`,
      criterion: item.text,
      description: item.sourceText || item.text,
    }))
}

export function projectDesiredCriteria(envelope: RubricEnvelope): DesiredCriteria[] {
  return envelope.items
    .filter(item => item.requirementType === 'desired')
    .map((item, index) => ({
      id: item.id || `desired-${index + 1}`,
      qualification: item.text,
      description: item.sourceText || item.text,
    }))
}

function slugify(value: string) {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}
