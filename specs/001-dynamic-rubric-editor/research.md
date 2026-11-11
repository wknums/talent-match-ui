# Research: Configurable Rubric Generation and Editing

**Feature**: 001-dynamic-rubric-editor
**Date**: 2026-09-09

## Decision 1: Split editable instructions from the protected output contract

- **Decision**: Administrators edit only the business instruction body. The server appends a versioned, application-owned response contract and mandatory itemization/safety constraints.
- **Rationale**: The current parsers require specific fields. Allowing free-form edits to remove or rename those fields would turn an administrative change into a runtime parser failure.
- **Alternatives considered**:
  - Make the entire prompt editable: rejected because it permits accidental contract drift and prompt-injection defenses to be removed.
  - Keep the whole prompt hardcoded: rejected because it does not meet the administrator customization requirement.

## Decision 2: Use immutable global instruction versions

- **Decision**: Store sequential, immutable versions with `draft`, `active`, and `retired` states; allow exactly one active version. Editing creates a new draft. Activating a prior validated version is the rollback operation.
- **Rationale**: This matches the product's auditability requirements and avoids in-place edits that make prior extractions irreproducible.
- **Alternatives considered**:
  - Reuse `ScoringPrompt`: rejected because scoring prompts are job-scoped and have a different lifecycle.
  - Store one mutable settings row: rejected because rollback and historical attribution would be impossible.

## Decision 3: Persist extraction records at extraction time

- **Decision**: Persist raw output, normalized output, validation findings, source file metadata/hash, purpose, actor, timestamps, and exact instruction/contract versions for both job creation and administrator validation runs.
- **Rationale**: A job may not yet exist when extraction occurs. Persisting only on job creation loses failed or abandoned extraction diagnostics and prevents reliable prompt testing.
- **Alternatives considered**:
  - Keep extraction metadata only in the browser until job save: rejected because it is unauditable and fragile.
  - Store uploaded source bytes again: rejected because existing upload/storage flows already own document content and duplication is unnecessary.

## Decision 4: Adopt a versioned rubric envelope

- **Decision**: New job configurations store `rubric-v2`, containing separate ordered category and item collections. Items hold stable identifiers, one category assignment, ordering, requirement type, source trace, and review status.
- **Rationale**: The current `[{name, weight, description}]` array cannot represent individual movable requirements without parsing prose on every edit.
- **Alternatives considered**:
  - Keep semicolon-delimited requirements in `description`: rejected because delimiter parsing loses identity, order, and source trace.
  - Normalize every item into relational tables immediately: rejected as unnecessary complexity because job configurations are already immutable JSON versions and item queries are config-scoped.

## Decision 5: Preserve legacy data through explicit conversion

- **Decision**: Readers accept legacy category arrays and `rubric-v2`. Legacy data is shown readably; item-level editing triggers an explicit conversion preview that creates a new config version only after user confirmation.
- **Rationale**: Automatic splitting by punctuation can corrupt valid prose and would silently mutate approved historical rubrics.
- **Alternatives considered**:
  - One-time destructive database rewrite: rejected because correct splitting requires human review.
  - Block all editing of legacy rubrics: rejected because it prevents users from adopting the new editor.

## Decision 6: Keep scoring category contracts stable

- **Decision**: Itemization changes rubric authoring and traceability, not category-level scoring output. Prompt generation derives category descriptions from ordered items, while scoring results continue to key scores by category name.
- **Rationale**: Existing scoring parsers, manual review, and application detail surfaces depend on category names and weights. Preserving that boundary minimizes unrelated scoring risk.
- **Alternatives considered**:
  - Score every item independently in this feature: rejected because it materially changes the scoring model and is outside the requested scope.

## Decision 7: Use stable IDs and explicit ordering for movement

- **Decision**: A move updates only `categoryId` and `order`; stable item IDs, text, requirement type, and source trace remain unchanged. Server normalization produces contiguous order values.
- **Rationale**: This makes drag/drop deterministic, auditable, concurrency-safe, and easy to persist in immutable config versions.
- **Alternatives considered**:
  - Use array position as identity: rejected because cross-category movement and concurrent edits become ambiguous.

## Decision 8: Native drag/drop plus explicit accessible move controls

- **Decision**: Use native desktop pointer drag/drop with visible drop targets. Provide item action controls for destination category and relative position, usable by keyboard and touch. Announce moves to assistive technology.
- **Rationale**: Equivalent controls satisfy all input modes without adding different third-party drag/drop dependencies to the two clients.
- **Alternatives considered**:
  - Add separate React and Blazor drag/drop libraries: rejected due to dependency and parity overhead.
  - Pointer drag only: rejected because it excludes keyboard and many touch users.

## Decision 9: Reuse lifecycle and security patterns, not persistence entities

- **Decision**: Reuse existing Admin authorization policies, audit/event infrastructure, multipart document validation, concurrency/error conventions, and prompt-management UX. Add extraction-specific repositories/entities and services.
- **Rationale**: Existing patterns are proven, but scoring prompt records do not model global extraction settings or protected contracts.
- **Alternatives considered**:
  - Expand scoring-prompt entities with nullable global fields: rejected because it creates ambiguous invariants and weakens type safety.

## Decision 10: Seed the current behavior as version 1

- **Decision**: On schema initialization, create instruction version 1 from the current full runtime extraction instructions only if no versions exist. The protected schema is stored/versioned in application code and identified on the row.
- **Rationale**: Existing deployments gain equivalent behavior without a manual bootstrap step, while future instruction changes become controlled.
- **Alternatives considered**:
  - Read `artifacts/prompts/job-gen/V001.txt` at runtime: rejected because the artifact is currently a shortened copy and deployment packaging is not guaranteed.
  - Require an administrator to create the first version: rejected because extraction would be unavailable after upgrade.

## Decision 11: Strict validation with actionable failure states

- **Decision**: Parse the raw AWReason response once, validate it against the protected schema, normalize IDs/order/weights, and persist validation findings. Invalid responses return a typed failure and never populate job form state as if successful.
- **Rationale**: Both current stacks use permissive mapping and discard fields; silent defaults would hide prompt regressions.
- **Alternatives considered**:
  - Best-effort coercion of arbitrary JSON: rejected because missing requirements could go unnoticed.
  - Retry automatically with a different prompt: rejected because it obscures which prompt produced the result and can amplify cost.

## Decision 12: Shared fixtures prove parity

- **Decision**: Keep contract fixtures covering split compound requirements, duplicates, ambiguous categories, explicit source rubrics, invalid weights, malformed JSON, and legacy conversion. Run equivalent fixtures through both stack validators.
- **Rationale**: The current extraction flows have no direct tests, and a shared contract is the most reliable way to prevent Stack A/Stack B drift.
- **Alternatives considered**:
  - UI-only tests: rejected because they cannot isolate parser and persistence differences.

## Resolved Technical Context

- Prompt scope: one global default shared by both stacks.
- Editable scope: business instructions and change note; not protected contract or mandatory constraints.
- Item model: config-scoped JSON items with stable IDs, category assignment, order, type, source trace, and review state.
- Legacy strategy: dual-read plus explicit new-version conversion.
- Drag/drop strategy: native pointer drag/drop plus keyboard/touch controls.
- Downstream scoring: category-level behavior preserved.
- Concurrency: optimistic versions for prompt drafts and job-config updates.
- External integration: existing authenticated `AWR_SEQ_API_ENDPOINT/assess/passthrough` multipart flow remains the transport.
