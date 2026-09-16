import type {
  ExtractionFailure,
  ExtractionResult,
  ExtractionValidationFinding,
  JobSpecExtractionRecord,
  RequirementType,
  RubricCategoryV2,
  RubricEnvelope,
  RubricItem,
} from '../../src/types/index.js'

export const PROTECTED_EXTRACTION_CONTRACT_VERSION = 'extraction-rubric-v1'
export const RUBRIC_NEEDS_REVIEW_CATEGORY_ID = 'cat-needs-review'

export interface NormalizedExtractionDocument {
  job_title?: string | null
  job_description?: string | null
  department?: string | null
  organization?: string | null
  requirements: Array<{
    id: string
    text: string
    requirement_type: RequirementType
    category_id: string
    source_text: string
    source_location?: string | null
    duplicate_of?: string | null
    needs_review: boolean
  }>
  rubric: {
    has_rubric_in_doc: boolean
    categories: Array<{
      id: string
      name: string
      weight: number
      description: string
      source: 'doc' | 'generated'
    }>
    weights_sum_to_1_0: boolean
  }
}

export interface ExtractionValidationResult {
  isValid: boolean
  validationStatus: 'valid' | 'invalid'
  findings: ExtractionValidationFinding[]
  document?: NormalizedExtractionDocument
  rubric?: RubricEnvelope
}

export function getProtectedContractMetadata() {
  return {
    version: PROTECTED_EXTRACTION_CONTRACT_VERSION,
    schemaFile: 'specs/001-dynamic-rubric-editor/contracts/extraction-rubric.schema.json',
    rubricSchemaFile: 'specs/001-dynamic-rubric-editor/contracts/rubric-config.schema.json',
  }
}

export function composeExtractionPrompt(instructionText: string) {
  return `${instructionText.trim()}

## Protected Output Contract (Mandatory)

Return JSON only. Do not include markdown fences or commentary.

Required top-level fields:
- job_title: string | null
- job_description: string | null
- department: string | null
- organization: string | null
- requirements: array of individually assessable requirements
- rubric: object with has_rubric_in_doc, categories, and weights_sum_to_1_0=true

Requirement shape:
- id: stable non-empty string
- text: one independently assessable requirement
- requirement_type: must_have | desired | experience | responsibility | other
- category_id: exact category id, or ${RUBRIC_NEEDS_REVIEW_CATEGORY_ID} when the assignment needs review
- source_text: original wording from the source
- source_location: optional location string or null
- duplicate_of: nullable id when the requirement is a genuine duplicate of an earlier item
- needs_review: boolean

Rubric category shape:
- id: stable non-empty string
- name: category display name
- weight: numeric weight between 0 and 1
- description: non-empty string
- source: doc | generated

Mandatory constraints:
- Split compound bullets into separate requirements.
- Preserve every independently assessable requirement as its own item.
- Consolidate only true duplicates and represent them with duplicate_of.
- Keep ambiguous assignments as explicit needs_review items instead of discarding them.
- Ensure category weights sum to exactly 1.0 and set weights_sum_to_1_0=true only when they do.`
}

export function validateExtractionResponse(rawResponse: string): ExtractionValidationResult {
  if (!rawResponse?.trim()) {
    return invalidResult([{ code: 'invalid_json', severity: 'error', path: '$', message: 'The extraction response was empty.' }])
  }

  let document: NormalizedExtractionDocument
  try {
    document = JSON.parse(rawResponse) as NormalizedExtractionDocument
  } catch (error) {
    return invalidResult([{
      code: 'invalid_json',
      severity: 'error',
      path: '$',
      message: `The extraction response was not valid JSON: ${(error as Error).message}`,
    }])
  }

  const findings: ExtractionValidationFinding[] = []
  if (!Array.isArray(document.requirements) || document.requirements.length === 0) {
    findings.push({ code: 'missing_requirement', severity: 'error', path: '$.requirements', message: 'At least one extracted requirement is required.' })
  }

  const ids = new Set<string>()
  for (const [index, requirement] of (document.requirements || []).entries()) {
    const path = `$.requirements[${index}]`
    if (!requirement?.id?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.id`, message: 'Requirement id is required.' })
    if (requirement?.id && ids.has(requirement.id)) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.id`, message: `Requirement id '${requirement.id}' must be unique.` })
    if (requirement?.id) ids.add(requirement.id)
    if (!requirement?.text?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.text`, message: 'Requirement text is required.' })
    if (!requirement?.category_id?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.category_id`, message: 'Requirement category_id is required.' })
    if (!requirement?.source_text?.trim()) findings.push({ code: 'missing_source_trace', severity: 'error', path: `${path}.source_text`, message: 'Every requirement must preserve source_text.' })
  }

  const categories = document.rubric?.categories
  if (!Array.isArray(categories) || categories.length === 0) {
    findings.push({ code: 'schema_mismatch', severity: 'error', path: '$.rubric.categories', message: 'At least one rubric category is required.' })
  }

  const categoryIds = new Set<string>()
  let weightSum = 0
  for (const [index, category] of (categories || []).entries()) {
    const path = `$.rubric.categories[${index}]`
    if (!category?.id?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.id`, message: 'Rubric category id is required.' })
    if (category?.id && categoryIds.has(category.id)) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.id`, message: `Rubric category id '${category.id}' must be unique.` })
    if (category?.id) categoryIds.add(category.id)
    if (!category?.name?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.name`, message: 'Rubric category name is required.' })
    if (!category?.description?.trim()) findings.push({ code: 'schema_mismatch', severity: 'error', path: `${path}.description`, message: 'Rubric category description is required.' })
    weightSum += Number(category?.weight ?? 0)
  }

  if (!document.rubric?.weights_sum_to_1_0 || Math.abs(weightSum - 1) > 0.0001) {
    findings.push({
      code: 'invalid_weight_total',
      severity: 'error',
      path: '$.rubric.categories',
      message: `Rubric category weights must sum to 1.0 but were ${weightSum.toFixed(4)}.`,
    })
  }

  if (findings.some(finding => finding.severity === 'error')) return invalidResult(findings, document)

  const rubric = normalizeRubricEnvelope(toRubricEnvelope(document))
  return { isValid: true, validationStatus: 'valid', findings, document, rubric }
}

export function toRubricEnvelope(document: NormalizedExtractionDocument): RubricEnvelope {
  const categories: RubricCategoryV2[] = (document.rubric.categories || []).map((category, index) => ({
    id: category.id.trim(),
    name: category.name.trim(),
    weight: Number(category.weight),
    description: category.description?.trim() || null,
    order: index,
  }))

  const grouped = new Map<string, RubricItem[]>()
  for (const requirement of document.requirements || []) {
    const list = grouped.get(requirement.category_id) || []
    list.push({
      id: requirement.id.trim(),
      categoryId: requirement.category_id.trim(),
      text: requirement.text.trim(),
      requirementType: requirement.requirement_type,
      order: list.length,
      sourceText: requirement.source_text.trim(),
      sourceLocation: requirement.source_location?.trim() || null,
      sourceRequirementId: requirement.id.trim(),
      reviewStatus: requirement.needs_review ? 'needs_review' : 'confirmed',
      createdFrom: 'extracted',
    })
    grouped.set(requirement.category_id, list)
  }

  return {
    schemaVersion: 'rubric-v2',
    legacySourceVersionId: null,
    categories,
    items: categories.flatMap(category => grouped.get(category.id) || []),
  }
}

export function normalizeRubricEnvelope(envelope: RubricEnvelope): RubricEnvelope {
  const categories = [...envelope.categories]
    .sort((left, right) => left.order - right.order || left.name.localeCompare(right.name))
    .map((category, index) => ({ ...category, order: index }))

  const items = categories.flatMap(category => {
    const categoryItems = envelope.items
      .filter(item => item.categoryId === category.id)
      .sort((left, right) => left.order - right.order || left.text.localeCompare(right.text))
      .map((item, index) => ({ ...item, order: index }))
    return categoryItems
  })

  return {
    schemaVersion: 'rubric-v2',
    legacySourceVersionId: envelope.legacySourceVersionId ?? null,
    categories,
    items,
  }
}

export function mapExtractionSuccess(result: {
  extractionId: string
  instructionVersionId: string
  protectedContractVersion: string
  findings: ExtractionValidationFinding[]
  rubric: RubricEnvelope
  document: NormalizedExtractionDocument
}): ExtractionResult {
  return {
    extractionId: result.extractionId,
    instructionVersionId: result.instructionVersionId,
    protectedContractVersion: result.protectedContractVersion,
    validationStatus: 'valid',
    validationFindings: result.findings,
    title: result.document.job_title ?? null,
    jobDescription: result.document.job_description ?? null,
    department: result.document.department ?? null,
    organization: result.document.organization ?? null,
    rubric: result.rubric,
  }
}

export function mapExtractionFailure(result: {
  extractionId: string
  instructionVersionId: string
  protectedContractVersion: string
  findings: ExtractionValidationFinding[]
}): ExtractionFailure {
  return {
    extractionId: result.extractionId,
    instructionVersionId: result.instructionVersionId,
    protectedContractVersion: result.protectedContractVersion,
    validationStatus: 'invalid',
    validationFindings: result.findings,
  }
}

export function createExtractionRecord(args: Omit<JobSpecExtractionRecord, 'createdAt' | 'completedAt'> & Partial<Pick<JobSpecExtractionRecord, 'createdAt' | 'completedAt'>>): JobSpecExtractionRecord {
  return {
    ...args,
    createdAt: args.createdAt || new Date().toISOString(),
    completedAt: args.completedAt || new Date().toISOString(),
  }
}

function invalidResult(findings: ExtractionValidationFinding[], document?: NormalizedExtractionDocument): ExtractionValidationResult {
  return { isValid: false, validationStatus: 'invalid', findings, document }
}
