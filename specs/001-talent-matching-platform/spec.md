# Feature Specification: Talent Matching Platform

**Feature Branch**: `001-talent-matching-platform`
**Created**: 2026-03-03
**Updated**: 2026-03-04
**Status**: Active (brownfield — existing codebase)
**Source**: PRD.md, INTEGRATION.md, README.md, AUTHENTICATION.md, MANUAL_REVIEW_SUMMARY.md

---

## User Scenarios & Testing

### User Story 1 — Recruiter authenticates and reaches their dashboard (Priority: P1)

A recruiter opens the app, enters username and password, and lands on a dashboard filtered to show
only jobs from their department. An admin sees all jobs regardless of department.

**Why this priority**: Gate to every other feature; nothing is accessible without auth.

**Independent Test**: Login with admin credentials, verify all-department dashboard. Create a
recruiter user scoped to "Engineering", log in, verify only Engineering jobs appear.

**Acceptance Scenarios**:

1. **Given** the app loads, **When** no session exists, **Then** the login screen is shown with
   username and password fields.
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
   is shown with Create, Delete, and Reset controls.
2. **Given** admin creates a user, **When** they supply username, role, department, and initial
   password, **Then** the user can log in with those credentials.
3. **Given** a recruiter, **When** they request a password reset, **Then** a pending request appears
   in the admin's User Management view.
4. **Given** a pending reset request, **When** admin approves with a new password, **Then** the
   user can log in with the new password.
5. **Given** any logged-in user, **When** they change their own password (providing the correct
   current password), **Then** the change persists and old password no longer works.
6. **Given** admin tries to delete themselves, **Then** the action is blocked.

---

### User Story 3 — Recruiter creates a job with rubric and scoring config (Priority: P2)

A recruiter fills in job details (title, department, organisation, posting date), uploads or
manually specifies a job specification, defines a scoring rubric with weighted categories, sets
must-have criteria, and configures the number of scoring runs and aggregation strategy.

**Why this priority**: Core data creation step; all downstream features depend on a configured job.

**Independent Test**: Create a job end-to-end; job card appears on dashboard with correct metadata.

**Acceptance Scenarios**:

1. **Given** a recruiter clicks "Create Job", **When** they complete the form, **Then** a new job
   card appears on the dashboard with the correct title, department, organisation, and posting date.
2. **Given** a job form, **When** a rubric document is uploaded, **Then** the system extracts
   categories with weights that sum to 1.0 and presents them for confirmation.
3. **Given** rubric categories are confirmed, **When** the job is saved, **Then** the job
   configuration is persisted with the rubric, must-have criteria, scoring run count, aggregation
   strategy, and longlist/shortlist thresholds.
4. **Given** a job is already created, **When** configuration is changed, **Then** a new version of
   the configuration is created without affecting in-progress scoring on the previous version.
5. **Given** a valid job, **When** the jobs list is viewed, **Then** the job card shows the
   organisation name and days since posting.

---

### User Story 4 — Recruiter uploads applications in bulk (Priority: P2)

A recruiter selects an active job and uses the upload dialog to drag-and-drop or bulk-select
document files (one or more per candidate). Each uploaded application is validated, fingerprinted,
stored, and queued for processing.

**Why this priority**: The pipeline cannot run without ingested applications.

**Independent Test**: Upload 5 application files; verify all appear in the job's application list
with status "Queued" and correct fingerprints.

**Acceptance Scenarios**:

1. **Given** the upload dialog is open, **When** files are dropped or selected, **Then** each file is
   validated for allowed type and size; invalid files show an error inline.
2. **Given** valid files, **When** upload completes, **Then** each application has status "Queued",
   a unique cryptographic fingerprint, file type, file size, and upload timestamp recorded.
3. **Given** a duplicate file (matching fingerprint), **When** uploaded, **Then** the system warns
   about the duplicate and allows the recruiter to link or treat as separate.
4. **Given** a large batch, **When** upload is initiated, **Then** a progress indicator per file is
   shown and the interface does not time out.

---

### User Story 5 — AI scoring pipeline runs and results are visible (Priority: P2)

Once applications are queued, the system extracts documents to a normalised text format, runs N
configurable scoring passes per application (each producing a score, category breakdown, must-have
evaluation, evidence citations, and improvement tips), then aggregates runs into a final decision.

**Why this priority**: Core value proposition of the platform.

**Independent Test**: Trigger scoring for a single application; verify N scoring run records exist,
each with score, evidence citations, must-have results, and AI model metadata; verify aggregated
result is present with final decision.

**Acceptance Scenarios**:

1. **Given** a queued application, **When** extraction succeeds, **Then** a normalised text version
   of the document exists with a confidence score.
2. **Given** a successful extraction, **When** N scoring runs execute, **Then** each run record
   contains a run index, total score, per-category scores, must-have evaluation, evidence citations,
   improvement tips, and AI model and prompt version identifiers.
3. **Given** N runs complete, **When** aggregation runs (using the configured strategy), **Then** an
   aggregated result exists with a final score, decision (Eligible, Excluded, or Needs Manual
   Review), variance, confidence, and a consolidated rationale.
4. **Given** variance exceeds the configured threshold, **Then** the application is flagged as
   "Needs Manual Review".
5. **Given** an extraction failure, **Then** the application status becomes "Extraction Failed"; the
   error reason is stored and a manual retry is available.
6. **Given** a scoring run failure after maximum retries, **Then** the item moves to the failure
   queue with error details.

---

### User Story 6 — Recruiter views ranked lists and drills into application detail (Priority: P2)

The job detail view shows a longlist, shortlist, and exclusions with sortable columns and filters.
Clicking a candidate opens a full drill-down showing documents, extracted text, all N scoring
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
4. **Given** a candidate row is clicked, **Then** a detail view shows: original documents, extracted
   text, all N individual scoring runs (scores and evidence), and the aggregated final decision.
5. **Given** an excluded candidate, **When** their detail is viewed, **Then** justification and
   improvement tips are prominently displayed.
6. **Given** an eligible application, **When** viewed, **Then** a "Manual Review" option navigates
   to the three-pane manual review interface.

---

### User Story 7 — Recruiter performs a manual review (Priority: P3)

The manual review interface has three resizable panes: job specification (left), rubric scoring form
(centre), and application content (right). The recruiter assigns points per category, adds comments,
saves the review, and every change is appended to an immutable audit trail.

**Why this priority**: Required for high-variance and edge-case applications; provides defensibility.

**Independent Test**: Open manual review for a flagged application; allocate points in all
categories; save; reload and verify the saved scores and comments persist with audit trail entries.

**Acceptance Scenarios**:

1. **Given** a recruiter opens manual review, **Then** all three panes are visible and independently
   scrollable.
2. **Given** points are allocated to a rubric category, **Then** the live score updates immediately.
3. **Given** save is clicked, **Then** the review data (including rubric scores, overall comment,
   and audit trail) is persisted against the application.
4. **Given** a page reload, **When** reviewing the same application, **Then** previous rubric scores
   and comments are pre-populated.
5. **Given** any scoring change, **Then** an audit entry is appended recording the reviewer,
   timestamp, category, previous value, and new value.

---

### User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

The dashboard shows system-wide stats (queued, processing, completed, failed), a filterable job
list, a pipeline visualiser per job (extraction → scoring → aggregation), and a failure queue
browser with retry controls. Stats refresh automatically.

**Why this priority**: Operational oversight for large batch runs.

**Independent Test**: While jobs are processing, verify stats update without manual page refresh;
simulate a failure and verify it appears in the failure queue with a retry option.

**Acceptance Scenarios**:

1. **Given** the dashboard is open, **When** time elapses, **Then** stats refresh automatically
   (within 30 seconds).
2. **Given** filtering by department and organisation, **Then** only matching jobs are shown.
3. **Given** a failed item in the failure queue, **When** "Retry" is clicked, **Then** the item
   moves back to the processing queue and the failure count decreases.
4. **Given** the pipeline visualiser for a job, **Then** the stages (extraction, scoring,
   aggregation, complete) display correct counts and percentage completion.

---

### Edge Cases

- Document format failures: extraction records failure reason; application marked for manual review;
  document preview available for human processing.
- AI model timeout/errors: automatic retry with backoff; maximum attempts exceeded moves item to
  failure queue with full error detail and manual retry option.
- High score variance (>15 points across runs): auto-flagged for manual review with variance
  visualisation.
- Concurrent job configuration changes: versioned configurations ensure in-flight scoring uses the
  original version; new version applies only to new applications.
- Duplicate applications: cryptographic fingerprint detected; system warns and offers
  link-or-separate.
- Large document volumes: pagination, virtual scrolling, progressive content rendering.
- Network interruptions: optimistic updates with rollback; visible sync status indicator.

---

## Requirements

### Functional Requirements

- **FR-001**: System MUST authenticate users with username and password; passwords MUST be stored securely using one-way hashing.
- **FR-002**: System MUST enforce role-based access — recruiters see only their department's jobs; admins see all.
- **FR-003**: Admin MUST be able to create, reset passwords for, and delete other users.
- **FR-004**: System MUST support recruiter-initiated password-reset requests visible to admin.
- **FR-005**: Jobs MUST be created with title, department, organisation, posting date, and a versioned configuration.
- **FR-006**: Job configuration changes MUST create a new version without invalidating in-progress scoring.
- **FR-007**: System MUST accept bulk document upload with per-file cryptographic fingerprinting, file type, and size validation.
- **FR-008**: System MUST flag duplicate applications via fingerprint comparison.
- **FR-009**: System MUST run N configurable scoring passes per application (default N=3).
- **FR-010**: Each scoring run MUST record the score, category breakdown, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage metrics.
- **FR-011**: System MUST aggregate N runs using a configurable strategy (median/mean/weighted) and compute variance.
- **FR-012**: Applications with variance above the configured threshold MUST be auto-flagged as "Needs Manual Review".
- **FR-013**: Failed extractions and scoring runs MUST be retried with backoff; items exceeding maximum retries go to the failure queue.
- **FR-014**: Manual review MUST record per-category scores, comments, and produce an immutable audit trail.
- **FR-015**: Dashboard stats MUST auto-refresh at intervals of 30 seconds or less.
- **FR-016**: Longlist, shortlist, and exclusion lists MUST be sortable and filterable.
- **FR-017**: All AI model calls MUST be proxied through the backend; no client-side API keys.
- **FR-018**: System MUST support swappable storage backends for local development and production deployment.

### Key Entities

- **User**: A person who accesses the system. Key attributes: username, role (admin or recruiter), full name, email, department, account creation date, and last login. Passwords stored as secure one-way hashes.
- **Job**: A job opening that applications are scored against. Key attributes: job code, title, department, organisation, posting date, status, and a reference to its current configuration version.
- **Job Configuration Version**: A versioned snapshot of a job's scoring setup. Key attributes: rubric (categories with weights), must-have criteria, number of scoring runs, aggregation strategy, longlist/shortlist thresholds, and variance threshold.
- **Application**: A candidate's submission for a specific job. Tracks status through the pipeline (Queued → Extracting → Scoring → Aggregating → Completed / Needs Manual Review / Failed), associated documents, final score, final decision, and variance.
- **Application Document**: An individual file within an application (CV, cover letter, etc.). Tracked with cryptographic fingerprint, file type, file size, file name, and upload timestamp.
- **Extraction Artifact**: The normalised text version of an application's documents, with a confidence score and processing status.
- **Scoring Run**: A single AI scoring pass for an application. Records the run index, total score, per-category scores, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage.
- **Aggregated Result**: The combined result of all scoring runs for an application. Contains final score, decision (Eligible / Excluded / Needs Manual Review), variance, confidence, consolidated rationale, and merged improvement tips.
- **Failure Queue Item**: A processing item that has exceeded retry limits. Records the entity type, failure reason, retry count, and timestamps.
- **Manual Review Data**: A recruiter's manual assessment of an application. Contains per-category rubric scores, overall comment, adjusted final score, and an immutable audit trail of all changes.
- **Processing Event**: An immutable ledger entry recording any state-changing action. Captures the actor, event type, affected entity, payload, and timestamp with a correlation identifier for end-to-end tracing.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 20,000 applications can be uploaded for a single job within 90 minutes with zero data loss.
- **SC-002**: Each application receives all N scoring runs and a final aggregated result within 15 minutes of upload (at default settings).
- **SC-003**: Dashboard statistics reflect pipeline state within 30 seconds of any status change.
- **SC-004**: Applications with score variance above the configured threshold are automatically flagged for manual review with 100% accuracy.
- **SC-005**: All final scoring decisions are fully traceable — AI model version, prompt version, individual run results, reviewer identity, and timestamp are recorded for every decision.
- **SC-006**: A recruiter can log in, create a job, upload applications, and reach the ranked list view within 10 minutes of first use.
- **SC-007**: Role-based access control is enforced end-to-end: recruiter accounts cannot view, query, or access data for jobs outside their assigned department.
- **SC-008**: Manual review scores persist across page reloads, and every point allocation change is recorded in an immutable per-application audit trail.

---

## Assumptions

- The platform will be hosted in a cloud environment with access to AI/LLM services.
- Initial deployment targets a single-tenant scenario with a defined set of departments and user roles.
- Document formats accepted for upload include common file types (PDF, DOCX, MD); exotic formats are out of scope for initial release.
- The admin user account is pre-created during initial deployment; subsequent users are managed through the admin interface.
- AI model availability and rate limits are managed externally; the platform handles transient failures through retry mechanisms.
- Scoring rubrics are defined per job; there is no global rubric library in the initial release.
- The platform handles English-language documents and job specifications; multi-language support is a future consideration.

---

## Scope & Boundaries

### In Scope

- User authentication and role-based access control (admin and recruiter roles)
- Job creation with versioned scoring configurations
- Bulk document upload and ingestion
- AI-powered multi-run scoring pipeline with aggregation
- Ranked candidate lists with filtering and drill-down
- Three-pane manual review interface with audit trail
- Operational dashboard with pipeline monitoring and failure retry
- Immutable audit ledger for all decisions and actions

### Out of Scope

- Candidate-facing portal or self-service application submission
- Integration with external applicant tracking systems (ATS)
- Email notifications or alerts
- Multi-language document processing
- Advanced analytics or reporting beyond the operational dashboard
- Candidate communication or interview scheduling
- Mobile-native application (responsive web is in scope)
