# Feature Specification: Talent Matching Platform

**Feature Branch**: `001-talent-matching-platform`
**Created**: 2026-03-03
**Status**: Active (brownfield — existing codebase)
**Source**: PRD.md, INTEGRATION.md, README.md, AUTHENTICATION.md, MANUAL_REVIEW_SUMMARY.md

---

## User Scenarios & Testing

### User Story 1 — Recruiter authenticates and reaches their dashboard (Priority: P1)

A recruiter opens the app, enters username and password, and lands on a dashboard filtered to show
only jobs from their department. An admin sees all jobs regardless of department.

**Why this priority**: Gate to every other feature; nothing is accessible without auth.

**Independent Test**: Login with `admin`/`adm1n99`, verify all-department dashboard. Create a
recruiter user scoped to `Engineering`, log in, verify only Engineering jobs appear.

**Acceptance Scenarios**:

1. **Given** the app loads, **When** no session exists, **Then** the login screen is shown with
   username + password fields.
2. **Given** valid credentials, **When** submitted, **Then** user is redirected to dashboard with
   role-filtered jobs.
3. **Given** invalid credentials, **When** submitted, **Then** an error message is shown; no
   navigation occurs.
4. **Given** a logged-in user, **When** they click "Logout", **Then** session is cleared and the
   login screen is shown.
5. **Given** an admin, **When** they view the dashboard, **Then** all departments' jobs are visible.
6. **Given** a recruiter, **When** they view the dashboard, **Then** only their department's jobs
   appear.

---

### User Story 2 — Admin manages users and passwords (Priority: P1)

An admin can create users, reset any user's password, delete users, and approve/reject recruiter
password-reset requests. All users can change their own password.

**Why this priority**: Required for initial demo setup and recruiter onboarding.

**Independent Test**: As admin, create a recruiter, reset their password, then approve a pending
reset request from a different user.

**Acceptance Scenarios**:

1. **Given** admin is logged in, **When** they open User Management, **Then** a list of all users
   is shown with Create/Delete/Reset controls.
2. **Given** admin creates a user, **When** they supply username, role, department, and initial
   password, **Then** the user can log in with those credentials.
3. **Given** a recruiter, **When** they request a password reset, **Then** a pending request appears
   in the admin's User Management dialog.
4. **Given** a pending reset request, **When** admin approves with a new password, **Then** the
   user can log in with the new password.
5. **Given** any logged-in user, **When** they change their own password (providing the correct
   current password), **Then** the change persists and old password no longer works.
6. **Given** admin tries to delete themselves, **Then** the action is blocked.

---

### User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

A recruiter fills in job details (title, department, organisation, posting date), uploads or
manually specifies a job spec, defines a scoring rubric with weighted categories, sets must-have
criteria, and configures the number of scoring runs and aggregation strategy.

**Why this priority**: Core data creation step; all downstream features depend on a configured job.

**Independent Test**: Create a job end-to-end; job card appears on dashboard with correct metadata.

**Acceptance Scenarios**:

1. **Given** a recruiter clicks "Create Job", **When** they complete the form, **Then** a new job
   card appears on the dashboard with the correct title, department, organisation, and posting date.
2. **Given** a job form, **When** a rubric document (PDF/MD/DOCX) is uploaded, **Then** the LLM
   extracts categories with weights that sum to 1.0 and they are presented for confirmation.
3. **Given** rubric categories are confirmed, **When** the job is saved, **Then** the
   `JobConfigVersion` is persisted with `versionId`, `rubric`, `mustHaves`, `runsPerApplication`,
   `aggregationStrategy`, `longlistThreshold`, and `shortlistThreshold`.
4. **Given** a job is already created, **When** config is changed, **Then** a new `JobConfigVersion`
   is created without affecting in-progress scoring runs on the old version.
5. **Given** a valid job, **When** the jobs list is viewed, **Then** the job card shows the
   organisation name and days since posting.

---

### User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

A recruiter selects an active job and uses the upload dialog to drag-and-drop or bulk-select
document files (one or more per candidate). Each uploaded application is validated, hashed, stored,
and queued for processing.

**Why this priority**: The pipeline cannot run without ingested applications.

**Independent Test**: Upload 5 application files; verify all appear in the job's application list
with status "Queued" and correct SHA-256 fingerprints.

**Acceptance Scenarios**:

1. **Given** the upload dialog is open, **When** files are dropped/selected, **Then** each file is
   validated for allowed type and size; invalid files show an error inline.
2. **Given** valid files, **When** upload completes, **Then** each application has status `Queued`,
   a `sha256` hash, `mimeType`, `sizeBytes`, and `uploadedAt` recorded.
3. **Given** a duplicate file (matching SHA-256), **When** uploaded, **Then** the UI warns about
   the duplicate and allows the recruiter to link or treat as separate.
4. **Given** a large batch (stress scenario), **When** upload is initiated, **Then** a progress
   indicator per file is shown and the UI does not time out.

---

### User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

Once applications are queued, the system extracts documents to Markdown, runs N configurable scoring
passes per application (each producing a score, category breakdown, must-have evaluation, evidence
citations, and improvement tips), then aggregates runs into a final decision.

**Why this priority**: Core value proposition of the platform.

**Independent Test**: Trigger scoring for a single application; verify N scoring run records exist,
each with score, evidence citations, must-have results, and model metadata; verify aggregated result
is present with final decision.

**Acceptance Scenarios**:

1. **Given** a queued application, **When** extraction succeeds, **Then** an `ExtractionArtifact`
   exists with Markdown content and `confidence` ≥ 0.
2. **Given** a successful extraction, **When** N scoring runs execute, **Then** each `ScoringRun`
   record contains `runIndex`, `totalScore`, `categoryScores`, `mustHaveResult`,
   `evidenceCitations`, `improvementTips`, `modelDeploymentId`, `promptVersionId`, and
   `inputTokens`/`outputTokens`.
3. **Given** N runs complete, **When** aggregation runs (median/mean/weighted), **Then** an
   `AggregatedResult` record exists with `finalScore`, `decision` (`Eligible`|`Excluded`|
   `NeedsManualReview`), `variance`, `confidence`, and a consolidated `consolidatedRationale`.
4. **Given** variance exceeds `varianceThreshold`, **Then** application status becomes
   `NeedsManualReview` and is flagged.
5. **Given** an extraction failure, **Then** application status becomes `ExtractionFailed`; the
   error reason is stored and a manual retry is possible.
6. **Given** a scoring run failure after max retries, **Then** the item moves to the DLQ with error
   details.

---

### User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

The job detail view shows a longlist, shortlist, and exclusions with sortable columns and filters.
Clicking a candidate opens a full drill-down showing documents, extracted Markdown, all N scoring
runs, evidence, final decision, and improvement tips.

**Why this priority**: Decision-making view; core recruiter workflow.

**Independent Test**: Navigate to a completed job; verify longlist/shortlist/exclusions tabs contain
correctly categorised candidates; drill into one application and verify all run details are shown.

**Acceptance Scenarios**:

1. **Given** a job with completed applications, **When** the Lists tab is opened, **Then** longlist,
   shortlist, and exclusions are shown in separate tabs with correct counts.
2. **Given** a list of applications, **When** sorted by score descending, **Then** highest-scoring
   applications appear first.
3. **Given** variance filter applied, **When** threshold set, **Then** only applications above that
   variance are shown.
4. **Given** a candidate row is clicked, **Then** a detail sheet slides in showing: original
   documents, extracted Markdown, all N individual scoring runs (scores + evidence), and the
   aggregated final decision.
5. **Given** an excluded candidate, **When** their detail is viewed, **Then** justification and
   improvement tips are prominently displayed.
6. **Given** an eligible application, **When** viewed, **Then** a "Manual Review" button navigates
   to the three-pane manual review interface.

---

### User Story 7 — Recruiter performs a manual review (Priority: P3)

The manual review interface has three resizable panes: job spec (left), rubric scoring form
(centre), and application content (right). The recruiter assigns points per category, adds comments,
saves the review, and every change is appended to an immutable audit trail.

**Why this priority**: Required for high-variance and edge-case applications; provides defensibility.

**Independent Test**: Open manual review for a flagged application; allocate points in all
categories; save; reload and verify the saved scores and comments persist with audit trail entries.

**Acceptance Scenarios**:

1. **Given** a recruiter opens manual review, **Then** all three panes are visible and independently
   scrollable.
2. **Given** points are allocated to a rubric category, **Then** the live score updates immediately.
3. **Given** save is clicked, **Then** `ManualReviewData` (including rubricScores, overallComment,
   auditTrail) is persisted against the `applicationId`.
4. **Given** a page reload, **When** reviewing the same application, **Then** previous rubric scores
   and comments are pre-populated.
5. **Given** any scoring change, **Then** an `ManualReviewAuditEntry` is appended with the actor's
   userId, timestamp, categoryId, previousValue, and newValue.

---

### User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

The dashboard shows system-wide stats (queued, processing, completed, failed), a filterable job
list, a pipeline visualiser per job (extraction → scoring → aggregation), and a DLQ browser with
retry controls. Stats refresh every 10 seconds.

**Why this priority**: Operational oversight for large batch runs.

**Independent Test**: While jobs are processing, verify stats update without manual page refresh;
simulate a failure and verify it appears in the DLQ with a retry button.

**Acceptance Scenarios**:

1. **Given** the dashboard is open, **When** 10 seconds elapse, **Then** stats refresh
   automatically.
2. **Given** filtering by department and organisation, **Then** only matching jobs are shown.
3. **Given** a failed item in the DLQ, **When** "Retry" is clicked, **Then** the item moves back to
   the processing queue and the DLQ count decreases.
4. **Given** the pipeline visualiser for a job, **Then** the four stages (extraction, scoring,
   aggregation, complete) display correct counts and percentage completion.

---

### Edge Cases

- Document format failures: OCR/extraction records failure reason; application marked for manual
  review; document preview available.
- Model timeout/errors: exponential backoff with jitter; max attempts → DLQ with full error detail.
- High score variance (>15 points across runs): auto-flagged for manual review.
- Concurrent job config changes: versioned configs ensure in-flight scoring uses original version.
- Duplicate applications: SHA-256 detected; UI warns and offers link-or-separate.
- Large document volumes: pagination, virtual scrolling, progressive Markdown rendering.
- Network interruptions: optimistic updates with rollback; visible sync status.

---

## Requirements

### Functional Requirements

- **FR-001**: System MUST authenticate users with username/password; passwords stored as SHA-256 hashes only.
- **FR-002**: System MUST enforce role-based access — recruiter sees only their department's jobs; admin sees all.
- **FR-003**: Admin MUST be able to create, reset passwords for, and delete other users.
- **FR-004**: System MUST support recruiter-initiated password-reset requests visible to admin.
- **FR-005**: Jobs MUST be created with title, department, organisation, posting date, and a versioned `JobConfigVersion`.
- **FR-006**: Job config changes MUST create a new version without invalidating in-progress scoring.
- **FR-007**: System MUST accept bulk document upload with per-file SHA-256 hashing, MIME type, and size validation.
- **FR-008**: System MUST flag duplicate applications via SHA-256 comparison.
- **FR-009**: System MUST run N configurable scoring passes per application (default N=3).
- **FR-010**: Each scoring run MUST store score, category breakdown, must-have evaluation, evidence citations, improvement tips, model ID, prompt version, and token counts.
- **FR-011**: System MUST aggregate N runs using a configurable strategy (median/mean/weighted) and compute variance.
- **FR-012**: Applications with variance above `varianceThreshold` MUST be auto-flagged as `NeedsManualReview`.
- **FR-013**: Failed extractions/scoring MUST be retried with exponential backoff; max-retried items go to DLQ.
- **FR-014**: Manual review MUST record per-category scores, comments, and produce an immutable audit trail.
- **FR-015**: Dashboard stats MUST auto-refresh at ≤30-second intervals.
- **FR-016**: Longlist, shortlist, and exclusion lists MUST be sortable and filterable.
- **FR-017**: All LLM calls MUST be proxied through the backend; no client-side LLM API keys.
- **FR-018**: Storage MUST be swappable between local JSON and Azure SQL via `STORAGE_PROVIDER` env var.

### Key Entities

- **User**: `userId`, `username`, `role` (admin|recruiter), `fullName`, `email`, `department`, `createdAt`, `lastLogin`; stored with separate `passwordHash` field.
- **Job**: `jobId`, `jobCode`, `title`, `department`, `organization`, `postingDate`, `status`, `currentVersion` (ref to `JobConfigVersion`)
- **JobConfigVersion**: `versionId`, `jobId`, `rubric[]`, `mustHaves[]`, `runsPerApplication`, `aggregationStrategy`, `longlistThreshold`, `shortlistThreshold`, `varianceThreshold`
- **Application**: `applicationId`, `jobId`, `candidateRef`, `status` (Queued→Extracting→Scoring→Aggregating→Completed|NeedsManualReview|Failed), `documents[]`, `finalScore`, `finalDecision`, `variance`
- **ApplicationDocument**: `documentId`, `sha256`, `mimeType`, `sizeBytes`, `fileName`, `uploadedAt`
- **ExtractionArtifact**: `artifactId`, `applicationId`, `markdown`, `confidence`, `status`, `createdAt`
- **ScoringRun**: `runId`, `applicationId`, `versionId`, `runIndex`, `totalScore`, `categoryScores`, `mustHaveResult`, `evidenceCitations[]`, `improvementTips[]`, `modelDeploymentId`, `promptVersionId`, `inputTokens`, `outputTokens`
- **AggregatedResult**: `resultId`, `applicationId`, `finalScore`, `decision`, `variance`, `confidence`, `consolidatedRationale`, `mergedImprovementTips[]`
- **DLQItem**: `itemId`, `entityType`, `entityId`, `failureReason`, `retryCount`, `lastAttemptAt`, `createdAt`
- **ManualReviewData**: `applicationId`, `jobId`, `rubricScores{}`, `overallComment`, `adjustedFinalScore`, `auditTrail[]`
- **ProcessingEvent** (ledger): `eventId`, `correlationId`, `actor`, `eventType`, `entityId`, `entityType`, `payload`, `timestamp`

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 20,000 applications are uploaded for a single job within 90 minutes with zero data loss
- **SC-002**: Each application receives all N scoring runs and a final aggregated result within 15 minutes of upload (at N=3 and default LLM concurrency)
- **SC-003**: Dashboard statistics reflect pipeline state within 30 seconds of any status change
- **SC-004**: Applications with score variance >15 points are automatically flagged as `NeedsManualReview`; flagged rate matches manual audit results with 100% accuracy
- **SC-005**: All final scoring decisions are fully traceable in the audit ledger — model deployment ID, prompt version ID, run artifacts, actor, and timestamp are recorded for every decision
- **SC-006**: A recruiter can log in, create a job, upload applications, and reach the ranked list view within 10 minutes of first use
- **SC-007**: Role-based access control is enforced end-to-end: recruiter accounts cannot view, query, or receive data for jobs outside their assigned department
- **SC-008**: Manual review scores persist across page reloads, and every point allocation change is recorded in an immutable per-application audit trail
