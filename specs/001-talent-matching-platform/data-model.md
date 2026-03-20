# Data Model — Talent Matching Platform

**Feature**: 001-talent-matching-platform | **Date**: 2026-03-09

This document defines all entities, their fields, relationships, validation rules, and state transitions.

---

## Entity Overview

```text
User ──────────────────────────────────────── ProcessingEvent
  │                                                 │
  ├── creates → Job ─────────────────────────────── │ (audit)
  │               │                                 │
  │               ├── has many → JobConfigVersion    │
  │               │                                 │
  │               ├── has many → ScoringPrompt ◄────┤
  │               │                  │               │
  │               │                  ├── PromptTestRun
  │               │                  │       │
  │               ├── has many → Application ◄──────┤
  │               │                  │               │
  │               │                  ├── ApplicationDocument
  │               │                  ├── ExtractionArtifact
  │               │                  ├── ScoringRun
  │               │                  ├── AggregatedResult
  │               │                  ├── ManualReviewData
  │               │                  └── FailureQueueItem
  │               │
  └── PasswordResetRequest
```

---

## Entities

### 1. User

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| userId | string (UUID) | PK, required | |
| username | string | required, unique, 3–50 chars | |
| passwordHash | string | required | SHA-256 hex; never exposed via API |
| role | UserRole enum | required | `admin` \| `recruiter` \| `business_panel` |
| department | string | optional (required for recruiter) | Filters job visibility |
| fullName | string | required | |
| email | string | optional | |
| createdAt | ISO 8601 datetime | required, auto-set | |
| lastLogin | ISO 8601 datetime | optional | Updated on each login |
| passwordResetRequired | boolean | default: false | |

**Validation Rules**:
- Username must be unique (case-insensitive)
- Password hashed with SHA-256 before storage; plaintext never stored
- Admin cannot delete themselves (FR, US2 scenario 6)
- Recruiter must have a non-empty `department`

---

### 2. Job

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| jobId | string (UUID) | PK, required | |
| jobCode | string | required | Human-readable identifier |
| title | string | required, 1–200 chars | |
| department | string | required | |
| organization | string | required | |
| postingDate | ISO 8601 date | required | |
| createdBy | string | required | userId of creator |
| createdAt | ISO 8601 datetime | required, auto-set | |
| status | JobStatus enum | required, default: Draft | `Draft` \| `Active` \| `Processing` \| `Completed` \| `Archived` |
| currentVersion | JobConfigVersion | required (embedded/ref) | Reference to active config |
| jobDescription | string | optional | Extracted or manually entered |
| specDocumentId | string | optional | Reference to uploaded spec document |
| rubricDocumentId | string | optional | Reference to uploaded rubric document |

**State Transitions**:
```
Draft → Active (when config is complete)
Active → Processing (when scoring pipeline starts)
Processing → Completed (when all applications scored)
Active/Completed → Archived (manual action)
```

**Validation Rules**:
- Recruiter can only create jobs in their own department
- Job must have at least one rubric category to become Active

---

### 3. JobConfigVersion

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| versionId | string (UUID) | PK, required | |
| jobId | string | FK → Job, required | |
| rubric | RubricCategory[] | required, ≥1 item | Weights must sum to 1.0 |
| mustHaves | MustHave[] | required (can be empty) | |
| desiredCriteria | DesiredCriteria[] | optional | |
| runsPerApplication | integer | required, default: 3, range: 1–10 | |
| aggregationStrategy | AggregationStrategy | required, default: median | `median` \| `mean` \| `weighted` |
| longlistThreshold | number | required, 0.0–1.0 | |
| shortlistThreshold | number | required, 0.0–1.0 | Must be > longlistThreshold |
| varianceThreshold | number | required, default: 15 | Points; triggers manual review flag |
| createdAt | ISO 8601 datetime | required, auto-set | |

**Sub-entities**:

| RubricCategory | Type | Constraints |
|----------------|------|-------------|
| id | string (UUID) | PK |
| name | string | required |
| description | string | required |
| weight | number | required, 0.0–1.0 |

| MustHave | Type | Constraints |
|----------|------|-------------|
| id | string (UUID) | PK |
| criterion | string | required |
| description | string | required |

| DesiredCriteria | Type | Constraints |
|-----------------|------|-------------|
| id | string (UUID) | PK |
| qualification | string | required |
| description | string | required |

**Validation Rules**:
- Rubric category weights MUST sum to 1.0 (±0.001 tolerance)
- shortlistThreshold > longlistThreshold
- Config changes create a NEW version (FR-006); in-progress scoring uses the original

---

### 4. ScoringPrompt *(NEW — from US3a / FR-032–FR-041)*

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| promptId | string (UUID) | PK, required | |
| jobId | string | FK → Job, required | |
| versionNumber | integer | required, auto-increment per job | Starts at 1 |
| promptText | string | required | The scoring prompt content |
| status | PromptStatus enum | required, default: draft | `draft` \| `active` \| `inactive` \| `production-approved` |
| createdAt | ISO 8601 datetime | required, auto-set | |
| lastModifiedAt | ISO 8601 datetime | required, auto-set | |
| author | string | required | userId of creator |
| rating | integer | optional, 0–5 | Recruiter-assigned quality rating |
| comments | string | optional | Free-text notes on this revision |
| source | PromptSource enum | required | `manual` \| `imported` \| `generated` |
| generationMetadata | object | optional | Raw API response if generated |

**State Transitions**:
```
draft → active (recruiter activates; deactivates previous active)
active → inactive (another prompt activated for same job)
active → production-approved (test-and-review workflow passed)
production-approved → inactive (another prompt approved)
inactive → active (re-activated by recruiter)
```

**Validation Rules**:
- Only ONE prompt per job may be `active` or `production-approved` at a time (FR-036)
- Rating must be integer 0–5 (FR-037)
- Editing an existing prompt creates a NEW revision with incremented version (FR-035)
- Production approval requires test-and-review workflow completion (FR-039/FR-040)
- All status transitions recorded in ProcessingEvent audit trail (FR-041)

---

### 5. PromptTestRun *(NEW — from US3a / FR-038–FR-040, FR-048)*

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| testRunId | string (UUID) | PK, required | |
| jobId | string | FK → Job, required | |
| promptId | string | FK → ScoringPrompt, required | The prompt revision being tested |
| status | TestRunStatus enum | required, default: pending_scoring | `pending_scoring` \| `scoring` \| `pending_review` \| `approved` \| `rejected` |
| applicationIds | string[] | required | IDs of test-case applications |
| createdAt | ISO 8601 datetime | required, auto-set | |
| completedAt | ISO 8601 datetime | optional | When review was completed |
| reviewedBy | string | optional | userId of reviewer |
| reviewNotes | string | optional | |

**State Transitions** (FR-048):
```
pending_scoring → scoring        (first test application begins processing)
scoring → pending_review         (all test applications scored and aggregated)
pending_review → approved        (manual review passed — no score changes)
pending_review → rejected        (manual review required score changes)
```

**Validation Rules**:
- Test-case applications excluded from production ranked lists (FR-038)
- Approval requires ALL test applications to pass manual review without score changes (FR-039)
- Prompt can be approved for production ONLY when testRun status is `approved` (FR-040)
- Auto-trigger: scoring pipeline fires automatically after test application upload — no manual trigger (FR-048)

---

### 6. Application

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| applicationId | string (UUID) | PK, required | |
| jobId | string | FK → Job, required | |
| candidateRef | string | required | Anonymised reference |
| candidateName | string | optional | |
| candidateEmail | string | optional | |
| status | ApplicationStatus enum | required, default: Queued | See state transitions |
| createdAt | ISO 8601 datetime | required, auto-set | |
| documents | ApplicationDocument[] | required, ≥1 | |
| extractionArtifactId | string | optional | FK → ExtractionArtifact |
| finalScore | number | optional | Set after aggregation |
| finalDecision | Decision enum | optional | `Eligible` \| `Excluded` \| `NeedsManualReview` |
| variance | number | optional | Across scoring runs |
| flagged | boolean | default: false | True when variance > threshold |
| testRunId | string | optional | FK → PromptTestRun; if set, this is a test case |

**State Transitions**:
```
Queued → Scoring → Aggregating → Completed
                                             → NeedsManualReview (variance > threshold)
Scoring → ScoringFailed (retry → failure queue after max retries)
```

> **Note**: The `Extracting` and `ExtractionFailed` statuses are retained for backward compatibility
> but are no longer used in the pipeline. The AWR scoring API handles document preprocessing
> (including OCR) internally per FR-059.

---

### 7. ApplicationDocument

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| documentId | string (UUID) | PK, required | |
| applicationId | string | FK → Application, required | |
| fileName | string | required | |
| mimeType | string | required | Validated allowlist: pdf, docx, md, txt, jpg |
| sizeBytes | number | required, max: 50MB | |
| sha256 | string | required | Cryptographic fingerprint for dedup |
| uploadedAt | ISO 8601 datetime | required, auto-set | |
| contentUrl | string | optional | Blob URL (production) or inline (dev) |
| rawContent | string | required | Base64-encoded raw file content for native rendering and scoring API submission (FR-056) |

**Validation Rules**:
- Allowed MIME types: `application/pdf`, `application/vnd.openxmlformats-officedocument.wordprocessingml.document`, `text/markdown`, `text/plain`, `image/jpeg`
- Duplicate detection via `sha256` fingerprint per job (FR-008)

---

### 8. ExtractionArtifact *(Deprecated)*

> **Note**: ExtractionArtifact is retained for backward compatibility but is no longer produced
> by the pipeline. The AWR scoring API handles document preprocessing (including OCR) internally
> per FR-059. The scoring worker sends the original document directly to the API.

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| artifactId | string (UUID) | PK, required | |
| applicationId | string | FK → Application, required | |
| markdown | string | required | Normalised text content |
| extractionMetadata | object | required | `{toolVersion, confidence, extractedAt}` |
| status | string | required | `Success` \| `Failed` |
| createdAt | ISO 8601 datetime | required, auto-set | |

---

### 9. ScoringRun

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| runId | string (UUID) | PK, required | |
| applicationId | string | FK → Application, required | |
| versionId | string | FK → JobConfigVersion, required | |
| runIndex | integer | required, 0-based | Index within the N runs |
| modelDeploymentId | string | required | AI model identifier |
| promptVersionId | string | required | FK → ScoringPrompt.promptId |
| overallScore | number | required, 0–100 | |
| subScores | Record<string, number> | required | Per-category scores |
| mustHaveResult | MustHaveResult | required | Pass/fail + details |
| evidenceCitations | EvidenceCitation[] | required | |
| rationale | string | required | |
| improvementRecommendations | string[] | required | |
| createdAt | ISO 8601 datetime | required, auto-set | |
| durationMs | number | required | Processing time |
| tokenUsage | object | optional | `{promptTokens, completionTokens, totalTokens}` |
| status | string | required | `Success` \| `Failed` |

---

### 10. AggregatedResult

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| resultId | string (UUID) | PK, required | |
| applicationId | string | FK → Application, required | |
| versionId | string | FK → JobConfigVersion, required | |
| finalScore | number | required, 0–100 | |
| finalSubScores | Record<string, number> | required | |
| confidence | number | required, 0–1 | |
| variance | number | required | Across N runs |
| finalDecision | Decision enum | required | |
| rationaleText | string | required | Consolidated |
| recommendationsText | string | required | Merged tips |
| allRuns | ScoringRun[] | embedded | All N scoring runs |
| createdAt | ISO 8601 datetime | required, auto-set | |

---

### 11. ManualReviewData

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| applicationId | string | PK, FK → Application | One review per application |
| jobId | string | FK → Job, required | |
| rubricScores | Record<string, {points, maxPoints, comment}> | required | Per-category |
| overallComment | string | required | |
| adjustedFinalScore | number | optional | |
| auditTrail | ManualReviewAuditEntry[] | required | Immutable append-only |
| lastModifiedAt | ISO 8601 datetime | required | |
| lastModifiedBy | string | required | userId |

**ManualReviewAuditEntry**:

| Field | Type | Notes |
|-------|------|-------|
| entryId | string (UUID) | |
| applicationId | string | |
| reviewerId | string | userId |
| reviewerName | string | |
| timestamp | ISO 8601 datetime | |
| changeType | enum | `score_adjustment` \| `comment_added` \| `points_allocated` |
| categoryId | string | optional |
| categoryName | string | optional |
| previousValue | number \| string | optional |
| newValue | number \| string | optional |
| comment | string | optional |

---

### 12. FailureQueueItem (DLQ)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| itemId | string (UUID) | PK, required | |
| applicationId | string | FK → Application, required | |
| jobId | string | FK → Job, required | |
| failureType | enum | required | `Extraction` (deprecated) \| `Scoring` \| `Aggregation` |
| failureReason | string | required | |
| attemptCount | integer | required | |
| firstFailedAt | ISO 8601 datetime | required | |
| lastAttemptedAt | ISO 8601 datetime | required | |
| canRetry | boolean | required | |
| notes | string | optional | |

---

### 13. ProcessingEvent (Audit Ledger)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| eventId | string (UUID) | PK, required | |
| timestamp | ISO 8601 datetime | required, auto-set | |
| actor | string | required | userId or system identifier |
| action | string | required | Event type (e.g., `prompt.created`, `prompt.activated`) |
| entityType | string | required | e.g., `job`, `application`, `scoring_prompt` |
| entityId | string | required | |
| correlationId | string | required | End-to-end trace ID |
| details | Record<string, any> | required | Event-specific payload |

---

### 14. PasswordResetRequest

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| requestId | string (UUID) | PK, required | |
| userId | string | FK → User, required | |
| username | string | required | Denormalised for display |
| fullName | string | required | Denormalised for display |
| requestedAt | ISO 8601 datetime | required, auto-set | |
| status | enum | required | `pending` \| `completed` \| `rejected` |
| resolvedAt | ISO 8601 datetime | optional | |
| resolvedBy | string | optional | Admin userId |

---

### 15. SystemStats (computed, not persisted)

| Field | Type | Notes |
|-------|------|-------|
| totalJobs | integer | |
| activeJobs | integer | |
| totalApplications | integer | |
| queuedApplications | integer | |
| processingApplications | integer | |
| completedApplications | integer | |
| failedApplications | integer | |
| averageThroughputPerHour | number | |

---

## Relationships Summary

| From | To | Cardinality | FK Field |
|------|----|-------------|----------|
| Job | JobConfigVersion | 1:N | `jobId` |
| Job | ScoringPrompt | 1:N | `jobId` |
| Job | Application | 1:N | `jobId` |
| Application | ApplicationDocument | 1:N | `applicationId` |
| Application | ExtractionArtifact | 1:1 | `applicationId` |
| Application | ScoringRun | 1:N | `applicationId` |
| Application | AggregatedResult | 1:1 | `applicationId` |
| Application | ManualReviewData | 1:1 | `applicationId` |
| Application | FailureQueueItem | 1:0..1 | `applicationId` |
| ScoringPrompt | PromptTestRun | 1:N | `promptId` |
| PromptTestRun | Application | 1:N | `testRunId` (on Application) |
| User | PasswordResetRequest | 1:N | `userId` |
| User | Job | 1:N | `createdBy` |

---

## New Entities for Implementation

The following entities are defined in the spec but **do not yet exist** in the codebase and must be added:

1. **ScoringPrompt** — Stack A: `src/types/index.ts` + `server/routes/prompts.ts`; Stack B: `Domain/Entities/ScoringPrompt.cs`
2. **PromptTestRun** — Stack A: `src/types/index.ts` + prompt test routes; Stack B: `Domain/Entities/PromptTestRun.cs`
3. **PromptStatus enum** — `draft` | `active` | `inactive` | `production-approved`
4. **PromptSource enum** — `manual` | `imported` | `generated`
5. **TestRunStatus enum** — `pending_review` | `approved` | `rejected`

All existing entities (User, Job, JobConfigVersion, Application, ApplicationDocument, ExtractionArtifact, ScoringRun, AggregatedResult, ManualReviewData, FailureQueueItem, ProcessingEvent, PasswordResetRequest) are already implemented in both stacks and require only the addition of `testRunId` on `Application`.
