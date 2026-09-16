# Feature Specification: Configurable Rubric Generation and Editing

**Feature Branch**: `001-dynamic-rubric-editor`  
**Created**: 2026-09-09  
**Status**: Draft  
**Input**: User description: "Make the job-specification rubric generation contract explicit and configurable for administrators, ensure every articulated requirement becomes an individual rubric item in an appropriate category, and improve rubric creation and editing so users can drag and drop requirements between categories."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Generate an Itemized Rubric (Priority: P1)

As a recruiter creating a job, I want every distinct requirement in the uploaded job specification represented as its own rubric item so that no qualification is hidden inside a broad summary and each requirement can be reviewed independently.

**Why this priority**: Accurate, complete extraction is the foundation for fair scoring and for all later rubric editing.

**Independent Test**: Upload a specification containing several separately stated requirements, including multiple requirements in one paragraph, and verify that each requirement appears once as an independently editable item under a suitable category.

**Acceptance Scenarios**:

1. **Given** a job specification with ten distinct requirements, **When** the system generates a rubric, **Then** all ten requirements appear as ten distinct rubric items.
2. **Given** a sentence or bullet containing multiple independently assessable requirements, **When** the rubric is generated, **Then** the requirements are split into separate items without changing their meaning.
3. **Given** a requirement that clearly belongs to a suggested category, **When** the rubric is generated, **Then** the item is assigned to that category and remains individually editable.
4. **Given** substantially duplicated wording in the specification, **When** the rubric is generated, **Then** only genuine duplicates are consolidated and the original source references remain traceable.

---

### User Story 2 - Reorganize Rubric Requirements (Priority: P2)

As a recruiter reviewing a generated or manually created rubric, I want to drag individual requirements between categories and reorder them so that I can correct categorization quickly without retyping content.

**Why this priority**: Generated categorization requires human review, and efficient correction is essential before approving a rubric for scoring.

**Independent Test**: Open a rubric with multiple categories, move an item to another category, reorder items within a category, save, and verify that the arrangement persists when the rubric is reopened.

**Acceptance Scenarios**:

1. **Given** a rubric item in one category, **When** the user drags it to another category, **Then** the destination is clearly indicated and the item moves to the chosen position.
2. **Given** multiple items in a category, **When** the user reorders an item, **Then** the new order is displayed and retained after saving.
3. **Given** a user who cannot or does not use pointer-based dragging, **When** the user invokes the equivalent move controls, **Then** the same category transfer and reordering operations are available.
4. **Given** unsaved rubric changes, **When** saving fails, **Then** the edited arrangement remains visible and the user receives a clear error without losing work.
5. **Given** a rubric with invalid category weights, **When** items are moved, **Then** item movement remains available while approval and final save continue to show the existing weight validation rules.

---

### User Story 3 - Administer the Default Generation Prompt (Priority: P3)

As an administrator, I want to review, revise, test, activate, and roll back the default job-specification extraction instructions so that rubric-generation behavior can improve without requiring an application release.

**Why this priority**: Administrators need controlled adaptability, but prompt changes must not compromise the response contract expected by the application.

**Independent Test**: Create a revised instruction version, validate it against a sample job specification, activate it, verify that subsequent extractions use it, and roll back to the previous active version.

**Acceptance Scenarios**:

1. **Given** an administrator viewing extraction settings, **When** the settings open, **Then** the active instruction version, status, author, activation date, and protected response contract are visible.
2. **Given** an administrator editing the instructions, **When** a new version is saved, **Then** the active version remains unchanged until the new version is explicitly activated.
3. **Given** a draft whose test output does not satisfy the required response contract, **When** the administrator attempts activation, **Then** activation is blocked and actionable validation errors are shown.
4. **Given** a validated draft, **When** the administrator activates it, **Then** new extraction requests use that version and existing jobs and rubrics remain unchanged.
5. **Given** an active version that produces undesirable results, **When** the administrator rolls back, **Then** the selected prior version becomes active for subsequent extraction requests.
6. **Given** a non-administrator, **When** the user accesses prompt administration, **Then** prompt content and activation controls cannot be changed.

---

### User Story 4 - Diagnose Extraction Results (Priority: P4)

As an administrator or authorized reviewer, I want each generated rubric to identify the instruction version used and expose completeness warnings so that unexpected grouping or omitted requirements can be investigated.

**Why this priority**: Traceability makes prompt changes governable and allows quality problems to be reproduced.

**Independent Test**: Generate a rubric with a known active instruction version and verify that the result records that version, reports any contract or completeness warning, and permits comparison with the source requirements.

**Acceptance Scenarios**:

1. **Given** a completed extraction, **When** an authorized user reviews its details, **Then** the instruction version and extraction time are shown.
2. **Given** extracted requirements that cannot all be mapped confidently, **When** the result is presented, **Then** unmapped items are retained individually and clearly flagged for review rather than discarded.

### Edge Cases

- A single bullet contains multiple skills, certifications, experience thresholds, or responsibilities joined by punctuation or conjunctions.
- The same requirement is repeated in mandatory and preferred sections with materially different strength.
- A requirement could reasonably belong to multiple categories; it is assigned once and flagged when confidence is insufficient.
- The source contains an explicit rubric whose categories conflict with the configured defaults.
- A category becomes empty after its final item is moved.
- A requirement is moved while the destination category is collapsed or outside the visible area.
- The administrator attempts to save an empty instruction body, remove the itemization rule, or activate output that does not meet the protected contract.
- Two administrators edit the same instruction version concurrently.
- An extraction starts while a new instruction version is activated; the extraction retains one identifiable version for its entire run.
- A legacy rubric stores several requirements in one description rather than as individual items.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST define and enforce one explicit structured response contract for job-specification extraction.
- **FR-002**: The response contract MUST include job metadata, individually extracted mandatory requirements, individually extracted desired requirements, experience requirements, rubric categories, category weights, individual rubric items, item source classification, and an indication that weights are valid.
- **FR-003**: The system MUST reject or clearly flag extraction responses that omit required fields, use invalid field types, contain no rubric categories, or have invalid category weights.
- **FR-004**: The system MUST preserve the raw extraction response and the instruction version used for diagnostic and audit purposes.
- **FR-005**: The default extraction instructions MUST direct the generator to identify every distinct, independently assessable requirement in the source specification.
- **FR-006**: The system MUST represent each distinct source requirement as a separate rubric item rather than combining multiple requirements into one item.
- **FR-007**: When one source statement contains multiple independently assessable requirements, the system MUST split it into separate rubric items while preserving meaning.
- **FR-008**: The system MUST consolidate only substantially duplicate requirements and MUST NOT collapse related but independently assessable requirements.
- **FR-009**: Every extracted requirement MUST be mapped to exactly one suggested rubric category or retained in an explicit needs-review grouping when a confident assignment cannot be made.
- **FR-010**: Every generated rubric item MUST retain enough source traceability for a reviewer to understand which source wording produced it.
- **FR-011**: Administrators MUST be able to view the active default extraction instructions and the protected response contract.
- **FR-012**: Administrators MUST be able to create a new draft version of the editable extraction instructions without altering the currently active version.
- **FR-013**: The response contract and mandatory safety and itemization constraints MUST be protected from ordinary prompt editing so an administrator cannot accidentally make generated output incompatible with the application.
- **FR-014**: Administrators MUST be able to test a draft instruction version against a sample job specification and inspect the resulting rubric and validation findings before activation.
- **FR-015**: The system MUST block activation of an instruction version that fails response-contract validation or omits mandatory extraction constraints.
- **FR-016**: Administrators MUST be able to activate one validated instruction version at a time and roll back to any retained prior version.
- **FR-017**: Prompt version history MUST record version identifier, status, author, creation time, activation time, and an optional change note.
- **FR-018**: Activating or rolling back instructions MUST affect only extraction requests started afterward and MUST NOT mutate existing jobs or rubrics.
- **FR-019**: Only administrators MUST be permitted to create, test, activate, or roll back default extraction instructions.
- **FR-020**: Authorized rubric editors MUST be able to drag an individual rubric item to a category and position of their choice.
- **FR-021**: Authorized rubric editors MUST be able to reorder items within a category.
- **FR-022**: The interface MUST provide clearly visible draggable items, valid drop targets, active movement feedback, and a clear indication of the destination position.
- **FR-023**: All drag-and-drop operations MUST have a keyboard-accessible and touch-accessible alternative that provides the same outcome.
- **FR-024**: Moving an item MUST preserve its wording, source traceability, and other item details.
- **FR-025**: Item and category ordering MUST persist after saving and reopening the rubric.
- **FR-026**: Users MUST be able to add, edit, and remove individual rubric items without editing a category-wide text block.
- **FR-027**: Moving requirements MUST NOT silently alter category weights; existing weight validation and approval rules MUST remain in force.
- **FR-028**: Empty categories MUST remain visible and usable as drop targets until the user explicitly removes them.
- **FR-029**: If saving fails, the system MUST preserve the user's unsaved rubric organization and present an actionable error.
- **FR-030**: The improved extraction and rubric-editing behavior MUST be functionally consistent across both supported application experiences.
- **FR-031**: Legacy category descriptions containing multiple requirements MUST remain readable, and users MUST be offered a controlled way to convert them into individual items before editing or approval.
- **FR-032**: The system MUST detect conflicting concurrent prompt edits or rubric edits and prevent one user's changes from silently overwriting another's.

### Key Entities

- **Extraction Instruction Version**: A versioned set of administrator-editable generation instructions with status, ownership, timestamps, change note, validation result, and activation history.
- **Protected Response Contract**: The non-editable definition of required extraction fields, value types, mandatory generation constraints, and validation rules.
- **Extracted Requirement**: One independently assessable statement derived from the source, including requirement type, source wording or location, suggested category, and review status.
- **Rubric Category**: A named weighted grouping with an ordered collection of rubric items.
- **Rubric Item**: An individually editable and movable requirement, including wording, order, source traceability, and category assignment.
- **Extraction Record**: The result of one extraction, including instruction version, raw response, validation findings, and generated rubric.

### Assumptions

- Existing administrator and job-editing permissions continue to determine who can administer prompts and edit rubrics.
- The response contract is application-owned; administrators customize business instructions but do not directly redefine required response fields.
- The default behavior preserves an explicit source rubric when present while still itemizing its requirements.
- Category weights continue to be managed independently from the number and position of items.
- Prompt versions and extraction diagnostics follow the product's existing audit and retention practices.
- Both supported application experiences must remain behaviorally equivalent.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a representative validation set, 100% of explicitly stated, independently assessable job requirements are represented as individual rubric items or explicitly flagged for review.
- **SC-002**: At least 95% of generated rubric items are assigned to an acceptable category on first generation, as judged by authorized reviewers.
- **SC-003**: Zero activated instruction versions produce responses that violate the protected response contract in pre-activation validation.
- **SC-004**: An administrator can create, test, activate, and, if needed, roll back an instruction version in under 10 minutes without an application release.
- **SC-005**: A recruiter can move and save a rubric item into a different category in under 15 seconds.
- **SC-006**: At least 90% of first-time users successfully reorganize rubric items without assistance in usability testing.
- **SC-007**: 100% of saved item movements and ordering changes remain intact after reopening the rubric.
- **SC-008**: All item movement operations are successfully completable using pointer, touch, and keyboard interaction.
- **SC-009**: Existing jobs and approved rubrics show no content changes after a default instruction version is activated or rolled back.
- **SC-010**: Both supported application experiences pass the same extraction completeness, prompt administration, and rubric reorganization acceptance tests.
