# Data Model: Configurable Rubric Generation and Editing

## ExtractionInstructionVersion

Represents one immutable version of the administrator-editable job-specification extraction instructions.

### Fields

- `Id`: UUID/string primary key.
- `VersionNumber`: positive integer, globally unique and increasing.
- `InstructionText`: non-empty editable business instruction body.
- `ProtectedContractVersion`: non-empty identifier for the application-owned response contract.
- `Status`: `draft | active | retired`.
- `ChangeNote`: optional administrator explanation.
- `ValidationStatus`: `unvalidated | valid | invalid`.
- `ValidationFindingsJson`: JSON array of structured findings.
- `ValidatedAt`: nullable timestamp.
- `ValidatedBy`: nullable actor identifier.
- `CreatedAt`: timestamp.
- `CreatedBy`: actor identifier.
- `ActivatedAt`: nullable timestamp.
- `ActivatedBy`: nullable actor identifier.
- `ConcurrencyVersion`: positive integer incremented by state-changing writes.

### Invariants

- Exactly one row is `active` after initial seeding.
- Only a `valid` version may become `active`.
- `InstructionText` cannot be empty and must pass mandatory-constraint checks.
- Historical instruction text is immutable; editing creates a new draft.
- Activating one version retires the prior active version in the same transaction.
- Version numbers are never reused.

### State Transitions

- Create: none -> `draft/unvalidated`.
- Successful validation: `draft/unvalidated|invalid` -> `draft/valid`.
- Failed validation: `draft/unvalidated|valid` -> `draft/invalid`.
- Activate: `draft/valid|retired/valid` -> `active/valid`; prior `active` -> `retired`.
- Rollback: activate a prior `retired/valid` version.

## JobSpecExtraction

Captures one real job extraction or administrator validation run.

### Fields

- `Id`: UUID/string primary key returned to the caller.
- `Purpose`: `job_creation | instruction_validation`.
- `InstructionVersionId`: required foreign key.
- `ProtectedContractVersion`: contract identifier used for the run.
- `SourceFileName`: sanitized display name.
- `SourceMimeType`: validated MIME type.
- `SourceSha256`: lowercase SHA-256 of source bytes.
- `RawResponse`: raw AWReason response text.
- `NormalizedResponseJson`: nullable validated extraction document.
- `ValidationStatus`: `valid | invalid`.
- `ValidationFindingsJson`: JSON array of errors/warnings.
- `JobId`: nullable job link.
- `JobConfigVersionId`: nullable config-version link.
- `CreatedAt`: timestamp.
- `CreatedBy`: actor identifier.
- `CompletedAt`: timestamp.
- `CorrelationId`: request/audit correlation identifier.

### Invariants

- The instruction and contract versions are fixed for the complete run.
- `valid` requires non-null normalized response.
- `invalid` retains the raw response and findings but cannot populate or approve a job rubric.
- Source bytes are not duplicated in this row.
- Linking to a job/config is allowed only for a valid `job_creation` extraction.
- A job/config link is immutable after it is established.

## ExtractionValidationFinding

Stored inside `ValidationFindingsJson`.

### Fields

- `Code`: stable machine-readable code.
- `Severity`: `error | warning`.
- `Path`: JSON path or logical extraction section.
- `Message`: actionable human-readable explanation.

### Required Finding Codes

- `invalid_json`
- `schema_mismatch`
- `missing_requirement`
- `compound_requirement`
- `duplicate_requirement`
- `unassigned_requirement`
- `invalid_weight_total`
- `missing_source_trace`
- `mandatory_instruction_missing`
- `stale_version`

## RubricEnvelopeV2

The value stored in `JobConfigVersion.RubricJson` for new or converted rubrics.

### Fields

- `SchemaVersion`: constant `rubric-v2`.
- `Categories`: ordered array of `RubricCategoryV2`.
- `Items`: ordered array of `RubricItemV2`.
- `LegacySourceVersionId`: nullable config version from which this rubric was converted.

### Invariants

- Category IDs and item IDs are unique within the envelope.
- At least one category exists.
- Category weights are numbers in the accepted range and sum to exactly `1.0` after normalization.
- Each item references exactly one existing category.
- Each category's item orders are unique and normalize to contiguous zero-based values.
- Moving an item changes only `CategoryId`, `Order`, and modification metadata.

## RubricCategoryV2

### Fields

- `Id`: stable UUID/string.
- `Name`: non-empty display/scoring name.
- `Weight`: decimal from 0 through 1.
- `Description`: optional category-level guidance, not a requirement container.
- `Order`: zero-based display order.

### Invariants

- Name comparison is trimmed and case-insensitive for duplicate detection.
- Empty categories are allowed.
- Renaming a category requires downstream category-name remapping validation.

## RubricItemV2

### Fields

- `Id`: stable UUID/string.
- `CategoryId`: required category reference.
- `Text`: one independently assessable requirement.
- `RequirementType`: `must_have | desired | experience | responsibility | other`.
- `Order`: zero-based order within the category.
- `SourceText`: source wording supporting the item.
- `SourceLocation`: optional page, heading, paragraph, or bullet reference.
- `SourceRequirementId`: identifier from the normalized extraction response.
- `ReviewStatus`: `confirmed | needs_review`.
- `CreatedFrom`: `extracted | manual | legacy_conversion`.

### Invariants

- Text and source text are non-empty for extracted items.
- One item must not combine independently assessable requirements.
- Related items remain separate unless they are substantially duplicate.
- Item identity and source trace survive category moves and reordering.
- A manual item may omit `SourceRequirementId` and `SourceLocation`.

## NormalizedExtractionDocument

Validated representation of AWReason output.

### Fields

- `JobTitle`: nullable string.
- `JobDescription`: nullable string.
- `Department`: nullable string.
- `Organization`: nullable string.
- `Requirements`: non-empty array of `ExtractedRequirement`.
- `Rubric`: category definitions and requirement mappings.
- `WeightsSumToOne`: true only after validation.

## ExtractedRequirement

### Fields

- `Id`: unique identifier within the extraction.
- `Text`: one independently assessable requirement.
- `RequirementType`: same values as `RubricItemV2.RequirementType`.
- `CategoryId`: category assignment or reserved needs-review category.
- `SourceText`: original source wording.
- `SourceLocation`: optional location.
- `DuplicateOf`: nullable requirement ID when the model reports a true duplicate.
- `NeedsReview`: boolean.

## JobConfigVersion Extensions

- `ExtractionId`: nullable foreign key to `JobSpecExtraction`.
- `ExtractionInstructionVersionId`: nullable foreign key to `ExtractionInstructionVersion`.
- `RubricJson`: accepts legacy array or `RubricEnvelopeV2`; all new writes use `rubric-v2`.

Existing `MustHavesJson` and `DesiredCriteriaJson` remain populated as projections of `RubricItemV2` so existing eligibility and display flows continue to work during migration.

## LegacyRubricAdapter

Legacy shape:

```json
[
  {
    "name": "Technical Skills",
    "weight": 0.6,
    "description": "Requirement A; Requirement B"
  }
]
```

### Read Rules

- Display the category, weight, and description without mutation.
- Mark the config as `legacy` and disable item-level drag/drop until conversion.
- Continue supplying existing category names, weights, and descriptions to scoring consumers.

### Conversion Rules

- Produce proposed individual items from descriptions and existing must-have/desired arrays.
- Show the proposal for user review.
- Require explicit confirmation.
- Save confirmation as a new `JobConfigVersion` with `rubric-v2`.
- Preserve the legacy config version and set `LegacySourceVersionId`.

## Relationships

- One `ExtractionInstructionVersion` has many `JobSpecExtraction` records.
- One `ExtractionInstructionVersion` may be referenced by many job config versions.
- One valid `JobSpecExtraction` may link to at most one job config version.
- One `RubricEnvelopeV2` contains many categories and many items.
- Each rubric item belongs to exactly one category.

## Shared Database Constraints and Indexes

- Unique index on `ExtractionInstructionVersions.VersionNumber`.
- Filtered/partial unique index allowing only one active instruction version.
- Index on `ExtractionInstructionVersions(Status, VersionNumber DESC)`.
- Index on `JobSpecExtractions(InstructionVersionId, CreatedAt DESC)`.
- Index on `JobSpecExtractions(JobId, CreatedAt DESC)`.
- Foreign keys use restrictive deletion; instruction and extraction history cannot be deleted while referenced.

## Audit Events

- `extraction-instruction.created`
- `extraction-instruction.validated`
- `extraction-instruction.validation-failed`
- `extraction-instruction.activated`
- `extraction-instruction.rolled-back`
- `job-spec.extracted`
- `job-spec.extraction-failed`
- `job.rubric-converted`
- Existing `job.config-updated` includes counts of moved/added/removed items, not prompt text or full requirement content.
