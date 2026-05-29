# Feature Specification: Talent Matching Platform

**Feature Branch**: `001-talent-matching-platform`
**Created**: 2026-03-03
**Updated**: 2026-04-24
**Status**: Active (brownfield — existing codebase)
**Source**: PRD.md, INTEGRATION.md, README.md, AUTHENTICATION.md, MANUAL_REVIEW_SUMMARY.md

## Iteration Scope Amendment (2026-04-23)

This planning/implementation iteration is intentionally limited to the manual review AI
prepopulation regression for User Story 7, specifically FR-014 and FR-026.

In scope for this iteration:
- AI prepopulation of per-category scores and evidence snippets in manual review
- Mismatch warning behavior when AI categories do not align to rubric categories
- Banner behavior for AI prepopulation state

Out of scope for this iteration:
- New feature implementation for user stories outside US7
- Broad platform refactors unrelated to manual review AI prepopulation

## Clarification Amendment (2026-04-24)

This clarification updates US7 behavior for AI prepopulation gating.

Problem clarified:
- The current "meaningful manual review content exists" gate can block AI prepopulation even when
   saved content is only auto-generated AI text and no human edits were made.

Required behavior (Option 1):
- AI prepopulation MUST be skipped only when a manual review has been human-edited.
- Presence of saved prepopulated AI content alone MUST NOT suppress future prepopulation.

New persistent signal:
- Introduce a persisted manual-review field `humanEdited` (boolean) used as the source of truth
   for prepopulation skip behavior.
- Default value MUST be `false` for newly created manual review records.
- Value MUST transition to `true` when a user actually edits prepopulated manual-review content.

Cross-stack/data-store parity requirements:
- This field and behavior MUST be implemented in both stacks (Stack A Node/React and Stack B
   .NET/Blazor).
- This field MUST be supported in both database providers used by the platform:
   SQLite and Azure SQL.
- Read/write DTOs, API contracts, repository models, and persistence mappings MUST remain parity
   aligned across stacks.

Acceptance scenarios for this clarification:

1. **Given** a manual review exists containing only AI-prepopulated content and `humanEdited=false`,
    **When** manual review is opened, **Then** AI prepopulation runs and refreshes the prepopulated
    fields.
2. **Given** a reviewer edits any prepopulated manual review score/comment and saves, **When** the
    review is persisted, **Then** `humanEdited` is stored as `true`.
3. **Given** a saved manual review with `humanEdited=true`, **When** manual review is opened,
    **Then** AI prepopulation is skipped and saved human-edited values are used.
4. **Given** Stack A and Stack B target the same application data in SQLite or Azure SQL,
    **When** manual review records are created/updated/read, **Then** `humanEdited` behavior is
    consistent in both stacks and both providers.

---

## User Scenarios & Testing

### User Story 1 — User authenticates and reaches role-scoped experience (Priority: P1)

A user opens the app and authenticates using the configured client authentication mode. In
`simple` mode, users authenticate with username/password. In `entra` mode, users authenticate with
Microsoft Entra ID and receive application roles via Entra security-group membership. After
authentication, the user lands on a role-scoped experience:
- admin: access to all organizations and departments
- recruiter: access limited to assigned organization and department
- analytics_viewer: read-only analytics with filters at global, organization, and
   organization+department levels

**Why this priority**: Gate to every other feature; nothing is accessible without auth.

**Independent Test**: Run the same authentication and authorization tests in both stacks with
`CLIENT_AUTH_MODE=simple` and `CLIENT_AUTH_MODE=entra`, verifying identical role outcomes and
scope boundaries.

**Acceptance Scenarios**:

1. **Given** the app loads and no authenticated session exists, **When** `CLIENT_AUTH_MODE=simple`,
   **Then** the login screen is shown with username and password fields.
2. **Given** the app loads and no authenticated session exists, **When** `CLIENT_AUTH_MODE=entra`,
   **Then** the user is challenged with Entra sign-in.
3. **Given** valid credentials or valid Entra sign-in, **When** authentication completes,
   **Then** the user is redirected to dashboard with role-filtered data.
4. **Given** invalid username/password in `simple` mode, **When** submitted, **Then** an error
   message is shown and no navigation occurs.
5. **Given** a logged-in user, **When** they click "Logout", **Then** local session state is
   cleared and re-authentication is required according to the configured auth mode.
6. **Given** an admin, **When** they view the dashboard and data screens, **Then** all
   organizations and departments are visible.
7. **Given** a recruiter, **When** they view the dashboard and data screens, **Then** only
   resources in their assigned organization and department are visible.
8. **Given** an analytics_viewer, **When** they open analytics views, **Then** they can read
   analytics at global, organization, and organization+department filter levels and cannot perform
   mutating actions.
9. **Given** `CLIENT_AUTH_MODE=entra`, **When** an authenticated principal is not a member of any
   mapped application role group, **Then** access is denied with a clear unauthorized message.
10. **Given** the Web.Server is running, **When** a browser navigates to the root URL (`/`), **Then**
   the Blazor WebAssembly UI is served (not a 404 or blank page), and API endpoints remain
   accessible under their `/api/` prefix.
11. **Given** Azure SQL is configured and the database is in a cold state, **When** the first
   connection attempt takes 30-90 seconds or returns transient startup errors, **Then** the
   system retries with bounded exponential backoff and eventually succeeds without requiring user
   intervention unless the retry budget is exhausted.

---

### User Story 2 — Admin manages users and passwords (Priority: P1)

An admin can create users, reset any user's password, delete users, and approve/reject recruiter
password-reset requests. All users can change their own password. This user/password lifecycle
applies only when `CLIENT_AUTH_MODE=simple`.

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
manually specifies a job specification, uploads and reviews/approves or manually defines a scoring rubric document with weighted categories, sets must-have criteria, desired criteria, and configures the number of scoring runs and aggregation strategy. The user interface provides the capability to optionally upload a job specification, a separate job scoring rubric document or both.

**Why this priority**: Core data creation step; all downstream features depend on a configured job.

**Independent Test**: Create a job end-to-end; job card appears on dashboard with correct metadata.

**Acceptance Scenarios**:

1. **Given** a recruiter clicks "Create Job", **When** they complete the form, **Then** a new job
   card appears on the dashboard with the correct title, department, organisation, and posting date.
2.  **Given** a job form, **When** a job specification document is uploaded, **Then** the system extracts
   Job Title, Job Description, if present, the department , organization. The system further extracts all "must have" requirements, recommended or desired qualifications and experience items. if a rubric is present in the Job Specification document, extract the rubric categories and its weights that sum to 1.0. If no rubric is present within the document and no separate rubric document was uploaded, generate a draft rubric with weights that sum to 1.0 based on the requirements, assigning 60% of the weights to the "must have" requirements. The supported document types are pdf, jpg , md, txt, docx and the system calls the external API at `AWR_SEQ_API_ENDPOINT/assess/passthrough` to perform this extraction (API documentation is available at `AWR_SEQ_API_ENDPOINT/docs`). The generated or extracted rubric MUST be stored and associated with the job as part of the job configuration version.
3. **Given** a job form, **When** a rubric document is uploaded, **Then** the system extracts the job title and rubric categories with weights that sum to 1.0 and presents them for confirmation. Should the Job title of the rubric document not match the Job Title of the job form, present a warning to the user explaining the mismatch and advise the user to correct and upload the corrected the rubric document and inform then that the existing rubric document will be discarded.
4. **Given** rubric categories are confirmed, **When** the job is saved, **Then** the job
   configuration is persisted with the rubric (including the raw API response from generation, if
   applicable), must-have criteria, scoring run count, aggregation strategy, and longlist/shortlist
   thresholds. The stored rubric MUST be explicitly approved by the user toggling a button from "draft" to "approved" state - once approved by the user, it  serves as the approved rubric for downstream prompt generation
   (US3a).
5. **Given** a job is already created, **When** configuration is changed, **Then** a new version of
   the configuration is created without affecting in-progress scoring on the previous version.
6. **Given** a valid job, **When** the jobs list is viewed, **Then** the job card shows the
   organisation name and days since posting.

---

### User Story 3a — Recruiter creates, tests, and approves a scoring prompt (Priority: P1)

After a job has been created (US3) and before any applications can be scored (US5), the recruiter
must create, test, and approve a scoring prompt for use by the external scoring API. This step is a
hard prerequisite for production scoring — the pipeline MUST NOT score applications for a job that
does not have an approved, active prompt.

Once a job exists, the job detail view enables prompt management controls. The recruiter can
choose to: (a) manually author a prompt, (b) import/upload a prompt from a file, or (c) generate
a draft prompt. When generating, the system uses the approved scoring rubric from US3 and calls the
external API endpoint defined by the environment variable `AWR_SEQ_API_ENDPOINT` to produce a draft.
The recruiter MUST review the generated prompt, may optionally edit it, and MUST explicitly approve
it before it can be used.

Prompts are stored per-job and versioned. A dropdown lists all existing prompt revisions for the
job. Selecting an existing prompt allows the recruiter to either activate it as-is or edit it and
save the result as a new revision. Only one prompt may be active for a job at any time.

The recruiter can assign a rating (0–5) and add comments to any prompt revision to guide future
improvements and track prompt quality over time.

Once a prompt has been created and set as active, the recruiter can test it by uploading a small
set of real example applications through the same upload and scoring flow described in US4/US5.
The resulting scored applications are marked as **test cases** (not production data). The recruiter
reviews these test-case results using the manual review process (US7). If the manual review
confirms the scores are acceptable without requiring changes, the prompt is approved for production
scoring. If changes are needed, the recruiter revises the prompt and repeats testing until
satisfied.

**Why this priority**: The scoring pipeline depends on an approved prompt; without this gate,
scoring results are unreliable and unauditable.

**Independent Test**: Create a job with a rubric. Generate a draft prompt via the API. Edit the
prompt, save, and activate it. Upload 3 test applications, verify they are scored and flagged as
test cases. Perform manual review on each. Approve the prompt for production. Verify production
scoring can now proceed and test-case results do not appear in production ranked lists.

**Acceptance Scenarios**:

1. **Given** a job has been created (US3 complete), **When** the recruiter views the job detail,
   **Then** prompt management controls (create, import/upload, generate) are enabled.
2. **Given** no job exists, **When** the recruiter views the prompt management area, **Then** all
   prompt controls are disabled with a message indicating a job must be created first.
3. **Given** the recruiter selects "Generate Prompt", **When** the job has an approved scoring
   rubric, **Then** the system calls the `AWR_SEQ_API_ENDPOINT` API with the rubric and returns a
   draft prompt for review.
4. **Given** a draft prompt is displayed, **When** the recruiter edits and clicks "Save", **Then**
   the prompt is stored as a new versioned revision for the job.
5. **Given** the recruiter clicks "Approve" on a prompt revision, **Then** that revision becomes
   the active prompt for the job; any previously active prompt is deactivated.
6. **Given** multiple prompt revisions exist, **When** the recruiter opens the prompt dropdown,
   **Then** all revisions are listed with version number, creation date, active status, and rating.
7. **Given** the recruiter selects an existing prompt revision, **When** they choose "Activate",
   **Then** it becomes the active prompt and the previously active prompt is deactivated.
8. **Given** the recruiter selects an existing prompt revision, **When** they choose "Edit",
   **Then** the prompt text is loaded into the editor; saving creates a new revision (the original
   is unchanged).
9. **Given** only one prompt can be active at a time, **When** a new prompt is activated, **Then**
   the system confirms the switch and records the change in the audit trail.
10. **Given** a prompt revision, **When** the recruiter assigns a rating (0–5) and/or adds a
    comment, **Then** the rating and comment are persisted against that revision and visible in the
    prompt revision list.
11. **Given** an active prompt exists, **When** the recruiter initiates a "Test Prompt" workflow,
    **Then** the system allows uploading example applications using the same process as US4.
12. **Given** test applications are uploaded, **When** the upload completes, **Then** the system
    automatically triggers the scoring pipeline for each test application using the prompt under
    test (not requiring production-approved status), marks all resulting scores as **test cases**,
    and excludes them from production ranked lists. The test-run status transitions through
    `pending_scoring` → `scoring` → `pending_review` as processing completes.
13. **Given** test-case scoring completes and the test-run status transitions to `pending_review`,
    **When** the recruiter views the test run card in prompt management, **Then** a "Review Results"
    button is displayed on the card. Clicking it opens an expandable results summary dialog showing
    each test application with its overall score, eligibility gate pass/fail, and per-category
    sub-scores. From this summary, the recruiter can click "Open Manual Review" to enter the full
    three-pane manual review interface (US7) pre-filtered to show only the test applications from
    that specific test run, or close the dialog and return later.
14. **Given** the recruiter completes manual review of all test applications in the run, **When**
    the system compares AI-assigned scores with manual review scores and finds no score changes in
    any category for any test application, **Then** the test run is marked as eligible and an
    "Approve for Production" button appears on the test run card. The button MUST NOT appear
    alongside any intermediate approval controls — the approval flow is: Review Results → Manual
    Review → (if no changes) → Approve for Production appears on the card.
15. **Given** the recruiter completes manual review of test applications, **When** any manual review
    adjusted a score in any category for any test application, **Then** the system prompts the user to mark the test run as 'acceptable' or 
    as `rejected`. if it is marked as rejectedt, the prompt is NOT eligible for production approval, and 
    recruiter must revise the prompt and re-test.
16. **Given** no prompt has been approved for production for a job, **When** production scoring is
    attempted, **Then** the system blocks scoring and displays a message requiring prompt approval.
17. **Given** the recruiter imports/uploads a prompt file, **When** the file is valid, **Then** its
    content is loaded into the editor for review, editing, and saving as a new revision.

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

Once applications are queued, the system submits each application to the scoring engine with the
configured number of runs (N). The scoring engine executes all N scoring passes internally and
performs automatic aggregation — computing the final score, per-category aggregates, variance,
confidence, and final decision — returning both the individual run results and the aggregated
result in a single response. The platform stores these results directly without performing its
own aggregation step.

The scoring pipeline supports two processing modes determined at startup by comparing the
`AWR_PLATFORM_API_ENDPOINT` and `AWR_SEQ_API_ENDPOINT` environment variables:

- **Sequential mode** — When `AWR_PLATFORM_API_ENDPOINT` equals `AWR_SEQ_API_ENDPOINT` (or
  `AWR_PLATFORM_API_ENDPOINT` is unset), the pipeline orchestrator sends a single scoring
  request to `AWR_SEQ_API_ENDPOINT/assess/passthrough` specifying the number of runs (N). The
  engine executes all N passes, aggregates the results, and returns the complete response
  synchronously. This is the simpler path, reusing the same endpoint already used for prompt
  management and test scoring runs.

- **Platform mode** — When `AWR_PLATFORM_API_ENDPOINT` differs from `AWR_SEQ_API_ENDPOINT`, the
  pipeline orchestrator submits scoring work to the scalable platform API at
  `AWR_PLATFORM_API_ENDPOINT` using its asynchronous, queue-based processing architecture.
  The platform API likewise executes N runs and returns aggregated results.

Regardless of mode, the scoring output schema, audit trail, and all downstream behaviour (ranked
lists, manual review, failure handling) remain identical. The mode selection is transparent to
the user — the UI, API contracts, and data model are unchanged.

**Why this priority**: Core value proposition of the platform.

**Independent Test**: Trigger scoring for a single application; verify N scoring run records exist,
each with score, evidence citations, must-have results, and AI model metadata; verify aggregated
result is present with final decision. Repeat with both endpoint configurations to validate
dual-mode operation.

**Acceptance Scenarios**:

1. **Given** a queued application, **When** scoring is submitted to the engine with N runs
   requested, **Then** the engine executes all N scoring passes internally and returns each
   individual run record (containing run index, total score, per-category scores, must-have
   evaluation, evidence citations, improvement tips, and AI model and prompt version identifiers)
   along with the aggregated result. The original document is sent directly to the scoring API
   which handles preprocessing/OCR internally.
2. **Given** the engine returns aggregated results, **Then** the aggregated result contains a final
   score, decision (Eligible, Excluded, or Needs Manual Review), variance, confidence, and a
   consolidated rationale. The platform stores these results directly without performing its own
   aggregation computation.
3. **Given** variance exceeds the configured threshold, **Then** the application is flagged as
   "Needs Manual Review".
4. **Given** a scoring run failure after maximum retries, **Then** the item moves to the failure
   queue with error details.
5. **Given** `AWR_PLATFORM_API_ENDPOINT` equals `AWR_SEQ_API_ENDPOINT`, **When** scoring is
   triggered, **Then** the pipeline uses sequential mode — a single request to
   `AWR_SEQ_API_ENDPOINT/assess/passthrough` with N runs specified.
6. **Given** `AWR_PLATFORM_API_ENDPOINT` differs from `AWR_SEQ_API_ENDPOINT`, **When** scoring is
   triggered, **Then** the pipeline uses platform mode — scoring work is submitted to the
   platform API at `AWR_PLATFORM_API_ENDPOINT`.
7. **Given** either scoring mode, **When** results are produced, **Then** the scoring output schema,
   ranked lists, manual review, and audit trail behave identically.

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
4. **Given** a candidate row is clicked, **Then** a detail view shows: original documents rendered
   in their native format, all N individual scoring runs (scores and evidence), and the aggregated
   final decision.
5. **Given** an excluded candidate, **When** their detail is viewed, **Then** justification and
   improvement tips are prominently displayed.
6. **Given** an eligible application, **When** viewed, **Then** a "Manual Review" option navigates
   to the three-pane manual review interface.

---

### User Story 7 — Recruiter performs a manual review (Priority: P3)

The manual review interface has three resizable panes: original application document rendered in
its native format (left), rubric scoring form (centre), and job specification with rubric
categories (right). The recruiter assigns points per category, adds comments, saves the review,
and every change is appended to an immutable audit trail. Documents are rendered natively: PDF
via browser-embedded viewer, DOCX via client-side HTML conversion, Markdown via a Markdown
renderer, TXT as preformatted text, and images (JPG) inline.

When AI scoring results are available for an application and no manual review has yet been saved,
the scoring form MUST be pre-populated with the AI's per-category scores and evidence:
- Each rubric category input MUST be pre-filled with the AI-assigned score (averaged across all
  scoring runs if N > 1).
- The notes/comment field for each category MUST be pre-filled with the AI evidence — direct
  quotes or citations extracted from the CV by the scoring engine — so the recruiter can see the
  justification for each AI-assigned score without leaving the review pane.
- A clearly visible banner MUST indicate that scores and notes are AI-pre-populated and should
  be verified. The banner MUST include the AI's overall aggregated score and variance.
- If AI scoring exists but category names do not match the configured rubric (e.g. the prompt
  used different category labels), a distinct warning MUST be shown explaining why pre-population
  is unavailable for affected categories.

**Why this priority**: Required for high-variance and edge-case applications; provides defensibility.

**Independent Test**: Open manual review for a flagged application that has completed AI scoring;
 verify that every rubric category shows the AI score pre-filled and that each category's notes
 field contains the AI evidence snippets. Adjust one score; save; reload and verify the saved
 scores and comments persist with audit trail entries.

**Acceptance Scenarios**:

1. **Given** a recruiter opens manual review, **Then** all three panes are visible and independently
   scrollable. The left pane displays the original uploaded document in its native format (PDF,
   DOCX rendered as HTML, Markdown rendered, TXT as preformatted text, images inline).
2. **Given** points are allocated to a rubric category, **Then** the live score updates immediately.
3. **Given** save is clicked, **Then** the review data (including rubric scores, overall comment,
   and audit trail) is persisted against the application.
4. **Given** a page reload, **When** reviewing the same application, **Then** previously saved
   rubric scores and comments are pre-populated (from the persisted review, not from AI).
5. **Given** any scoring change, **Then** an audit entry is appended recording the reviewer,
   timestamp, category, previous value, and new value.
6. **Given** AI scoring results exist for the application and no manual review has been saved yet,
   **When** the recruiter opens manual review, **Then** every rubric category input is pre-filled
   with the AI-assigned score (averaged across runs) AND the notes field for each category
   contains the AI evidence snippets (direct quotes/citations from the CV) that support that
   score. A banner MUST be visible indicating AI pre-population is active and showing the
   aggregated score and variance.
7. **Given** AI scoring exists but category names in the scoring output do not match the job's
   configured rubric, **When** the recruiter opens manual review, **Then** a warning is displayed
   explaining the mismatch and that pre-population is unavailable; the recruiter may re-score
   the application to resolve the mismatch.
8. **Given** AI scoring does not exist for the application (e.g. scoring failed or has not run),
   **When** the recruiter opens manual review, **Then** all category inputs are empty (zero) and
   no pre-population banner is shown.

---

### User Story 8 — Admin/recruiter monitors the processing pipeline (Priority: P3)

The dashboard shows system-wide stats (queued, processing, completed, failed), a filterable job
list, a pipeline visualiser per job (scoring → complete), and a failure queue
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
4. **Given** the pipeline visualiser for a job, **Then** the stages (scoring, complete) display
   correct counts and percentage completion. Aggregation is performed by the scoring engine as
   part of the scoring step.

---

### Edge Cases

- Document format failures: scoring API returns error; application marked for manual review;
  original document still viewable in its native format for human processing.
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
- Azure SQL cold-start wake-up delays (30-90 seconds): initial connection may time out or fail
   with transient startup errors; system retries with bounded backoff and logs retry attempts.
- Auth-mode mismatch across stacks: if Stack A and Stack B are configured with different
   `CLIENT_AUTH_MODE` values for the same environment, startup/deploy validation fails with a
   descriptive parity error.
- Entra role mapping gaps: authenticated users with no mapped app role groups are denied access and
   shown a clear authorization message.

---

## Requirements

### Functional Requirements

- **FR-001**: System MUST support a client authentication mode switch (`CLIENT_AUTH_MODE`) with two values: `simple` and `entra`, and MUST apply the same selected mode to both Stack A and Stack B in each environment.
- **FR-002**: In `simple` mode, system MUST authenticate users with username and password, store passwords with one-way hashing, and maintain session-based authentication.
- **FR-003**: In `entra` mode, system MUST authenticate users through Microsoft Entra ID and map access via application-specific Entra security groups to roles: `admin`, `recruiter`, and `analytics_viewer`.
- **FR-004**: System MUST enforce role-based access in both auth modes: `admin` has full access, `recruiter` is limited to assigned organization and department, and `analytics_viewer` has read-only analytics access with filters at global, organization, and organization+department levels.
- **FR-072**: In `simple` mode, admin MUST be able to create users, reset passwords, and delete other users.
- **FR-073**: In `simple` mode, system MUST support recruiter-initiated password-reset requests visible to admin.
- **FR-005**: Jobs MUST be created with title, department, organisation, posting date, and a versioned configuration.
- **FR-006**: Job configuration changes MUST create a new version without invalidating in-progress scoring.
- **FR-007**: System MUST accept bulk document upload with per-file cryptographic fingerprinting, file type, and size validation.
- **FR-008**: System MUST flag duplicate applications via fingerprint comparison.
- **FR-009**: System MUST submit each application to the scoring engine with N configurable scoring passes requested (default N=3). The engine executes all N passes internally and returns both the individual run results and the aggregated result in a single response. The platform MUST NOT loop over N individual API calls — the run count is passed to the engine as a parameter.
- **FR-010**: Each scoring run MUST record the score, category breakdown, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage metrics.
- **FR-011**: Aggregation of N runs MUST be performed by the scoring engine automatically when multiple runs are requested. The engine computes the final score, per-category aggregates, variance, confidence, and final decision (using its built-in aggregation strategy) and returns them alongside the individual runs. The platform MUST store the engine-provided aggregated result directly. The platform's local aggregation worker (`server/workers/aggregation.ts` in Stack A; equivalent in Stack B) is deprecated for new scoring flows but retained for backward compatibility.
- **FR-012**: Applications with variance above the configured threshold MUST be auto-flagged as "Needs Manual Review".
- **FR-013**: Failed scoring runs MUST be retried with backoff; items exceeding maximum retries go to the failure queue.
- **FR-014**: Manual review MUST record per-category scores, comments, and produce an immutable audit trail. When AI scoring results are available and no manual review has been previously saved, the review form MUST be pre-populated with: (a) the AI-assigned score per rubric category (averaged across all scoring runs); (b) the AI evidence snippets (direct quotes/citations from the CV) for each category, surfaced in the category's notes/comment field; (c) the overall aggregated AI score and variance in a clearly visible banner. Pre-population MUST NOT overwrite a previously saved manual review. If AI category names do not match the rubric, a warning MUST be shown per unmatched category.
- **FR-015**: Dashboard stats MUST auto-refresh at intervals of 30 seconds or less.
- **FR-016**: Longlist, shortlist, and exclusion lists MUST be sortable and filterable.
- **FR-017**: All AI model calls MUST be proxied through the backend; no client-side API keys.
- **FR-018**: System MUST support swappable storage backends for local development and production deployment.
- **FR-019**: The Stack B Web.Server project MUST host the Blazor WebAssembly client — it MUST include the WebAssembly.Server hosting package, serve Blazor framework files and static assets via middleware, and map a fallback route to `index.html` so that the root URL and all client-side routes serve the Blazor UI alongside the API endpoints.
- **FR-020**: In `simple` mode, Stack B Web.Server MUST seed a default admin user on first launch (username `admin`, SHA-256 hashed password, role `admin`, department `all`) if no users exist in the database, matching Stack A behaviour defined in `server/services/init-users.ts` and `AUTHENTICATION.md`.
- **FR-021**: In `simple` mode, both stacks MUST use cookie/session authentication semantics consistently. For Stack B this includes sending cookie credentials with every API request via `BrowserRequestCredentials.Include` and setting cookie options `HttpOnly`, `SameSite=Strict`, and `SecurePolicy=SameAsRequest`.
- **FR-022**: The Stack B Blazor WASM client MUST provide a `UserMenu` component in the layout header displaying the authenticated user's name, role badge, and department, with controls for logout, change password, and (admin-only) user management access — matching the React `UserMenu.tsx` behaviour.
- **FR-023**: The Stack B Blazor WASM client MUST provide accessible navigation entry points for all features: "Create Job" button on Dashboard, "Upload Applications" button on Job Detail, User Management from UserMenu (admin-only), Change Password from UserMenu, and Failure Queue from navigation — ensuring no component is orphaned from the UI.
- **FR-024**: The Stack B Blazor WASM client MUST implement a Change Password dialog with current password, new password, and confirmation fields, calling the change-password API endpoint with success/error feedback — matching US2 acceptance scenario 5.
- **FR-025**: The Stack B Blazor WASM client MUST implement an Application Detail page (`ApplicationDetail.razor`) displaying original documents rendered in their native format (PDF via embedded viewer, DOCX converted to HTML, Markdown rendered, TXT as preformatted text, images inline), all N individual scoring runs with per-category breakdown and evidence citations, and the aggregated final decision with rationale and improvement tips — matching US6 acceptance scenario 4 and React `ApplicationDetail.tsx`.
- **FR-026**: The Stack B Manual Review page MUST implement per-category rubric scoring with point allocation inputs for each category, live weighted score recalculation, display of the job's rubric categories and must-have criteria in the left pane, and a chronological audit trail — matching US7 acceptance scenarios 1–8 and React `ManualReviewView.tsx`. This MUST include AI pre-population of both scores and evidence per category (US7 scenario 6), the mismatch warning (scenario 7), and the pre-population banner showing overall AI score and variance.
- **FR-027**: The Stack B Blazor WASM client MUST implement a Failure Queue view displaying DLQ items with error details, failure reason, retry count, timestamps, a retry button, and 30-second auto-refresh — matching US8 acceptance scenario 3 and React `FailureQueueView.tsx`.
- **FR-028**: The Stack B Create Job dialog MUST implement rubric category management (add/remove rows, weight inputs with sum-to-1.0 validation), must-have criteria list management (add/remove), and job specification file upload with LLM-based rubric extraction — matching US3 acceptance scenarios 2–3 and React `CreateJobDialog.tsx` / `UploadRubricDialog.tsx`.
- **FR-029**: The Stack B User Management component MUST include a tab or section for pending password reset requests with approve (with new password) and reject controls — matching US2 acceptance scenarios 3–4 and React `UserManagementDialog.tsx`.
- **FR-030**: All Stack B Blazor WASM mutating operations (create user, delete user, reset password, create job, upload applications) MUST check the API response for success before updating local state; MUST show a visible success message on success and an error message with the server-returned reason on failure; and MUST NOT close dialogs or clear form fields when the operation fails — matching the feedback patterns in the React frontend's toast notifications.
- **FR-031**: The Stack B Blazor WASM client MUST verify that newly created users can authenticate immediately — the password hash produced during user creation (via `CreateUserCommand`) MUST be identical to the hash computed during login (via `AuthEndpoints`), and the `ApiClient.CreateUserAsync` method MUST propagate server-side errors (e.g., duplicate username, validation failures) to the calling component.
- **FR-032**: System MUST require an approved, active scoring prompt for a job before any production scoring runs can execute for that job.
- **FR-033**: Prompt management controls MUST be enabled only after a job has been created with an approved scoring rubric.
- **FR-034**: System MUST support three prompt creation methods: manual authoring, file import/upload, and AI-assisted generation via the `AWR_SEQ_API_ENDPOINT` external API using the job's approved rubric.
- **FR-035**: All prompts MUST be stored per-job with immutable versioning; editing an existing prompt MUST create a new revision without altering the original.
- **FR-036**: Only one prompt revision MAY be active for a given job at any time; activating a new revision MUST deactivate the previously active one.
- **FR-037**: Recruiters MUST be able to assign a rating (integer 0–5) and add free-text comments to any prompt revision.
- **FR-038**: The system MUST support a prompt testing workflow that scores a set of example applications using the active prompt, marks all resulting scores as test cases, and excludes them from production ranked lists. Completing the test-application upload MUST automatically trigger the scoring pipeline for those applications using the prompt under test — no separate manual trigger is required. The prompt under test does NOT need to be production-approved for test scoring to proceed. Test scoring applications (identified by a non-null `testRunId`) MUST be excluded from all production-facing statistics, including system-wide dashboard counts (total, queued, processing, completed, failed), per-recruiter analytics, per-department analytics, and per-job application counts.
- **FR-039**: Test-case results MUST be reviewable through the manual review process (US7); a prompt is eligible for production approval only when manual review of all test cases completes without requiring score changes.
- **FR-040**: The system MUST block production scoring for a job until at least one prompt has been approved for production use via the test-and-review workflow.
- **FR-041**: All prompt lifecycle events (creation, editing, activation, rating, commenting, testing, approval) MUST be recorded in the immutable audit trail with actor, timestamp, and event payload.
- **FR-045**: The scoring worker in both Stack A and Stack B MUST send the original uploaded document (in its native format) directly to the `AWR_SEQ_API_ENDPOINT/assess/passthrough` API for scoring. The passthrough API handles document preprocessing (including OCR) internally. The production-approved `ScoringPrompt.promptText` (with `{{JOB_SPEC_TEXT}}` placeholder resolved) MUST be sent as the `promptFile`. The original document blob (PDF, DOCX, etc.) MUST be sent as `specFile` with its original MIME type and filename. The scoring worker MUST NOT generate synthetic/random scores — all scores MUST originate from the external LLM via the passthrough API. The `{{CANDIDATE_CV_TEXT}}` placeholder is no longer used since the original document is sent directly. The scoring response MUST be valid JSON; however, the system MUST NOT assume any fixed schema or hardcoded field names for the scoring output. Each job's prompt defines the scoring categories, and the LLM response structure varies accordingly. The parser MUST walk the JSON generically — extracting numeric values as scores, string values as evidence/recommendations, arrays of objects with numeric fields as category breakdowns, and top-level objects with numeric sub-fields as category scores. If the LLM response is not valid JSON, the system MUST flag it as an error, store the raw response for display, and guide the user to edit the prompt to ensure JSON output.
- **FR-046**: The prompt test-run workflow (FR-038) MUST execute real LLM scoring via `AWR_SEQ_API_ENDPOINT/assess/passthrough` — not mocked or synthetic scores — so that test-case results are representative of production quality and can be meaningfully reviewed during the manual review gate (FR-039). For test scoring, the prompt under test is used directly (it does not need production-approved status).
- **FR-047**: Both Stack A and Stack B MUST implement the scoring worker with the same passthrough calling convention used by prompt generation (FR-034): `POST {AWR_SEQ_API_ENDPOINT}/assess/passthrough` with multipart `FormData` containing `promptFile` (the scoring system prompt) and `specFile` (the original application document in its native format). The response text is the LLM's structured scoring output.
- **FR-048**: When test-application upload completes (FR-038), the system MUST automatically trigger the scoring pipeline in the background for each uploaded test application. The test-run status MUST transition from `pending_scoring` to `scoring` during processing and to `pending_review` once all test applications have been scored and aggregated. The UI MUST reflect these status transitions. No separate user action (e.g. clicking a "Process" button) is required to initiate test scoring.
- **FR-049**: All outbound HTTP requests to `AWR_SEQ_API_ENDPOINT` MUST include authentication headers determined by the `AWR_AUTH_MODE` environment variable. When `AWR_AUTH_MODE=none`, no authentication headers are sent. When `AWR_AUTH_MODE=apikey`, the request MUST include `X-Api-Key` (from `AWR_API_KEY`), `X-User-Id` (the authenticated user's username), and `X-User-Role` (the authenticated user's role). When `AWR_AUTH_MODE=entra`, the request MUST include an `Authorization: Bearer <JWT>` header obtained via MSAL / `DefaultAzureCredential` client-credentials flow against the audience specified by `AWR_AAD_AUDIENCE`.
- **FR-050**: The system MUST read AWReason API authentication configuration from environment variables: `AWR_AUTH_MODE` (`none` | `apikey` | `entra`), `AWR_API_KEY` (shared secret for apikey mode), `AWR_AAD_ISSUER` (Entra ID issuer URL for entra mode), and `AWR_AAD_AUDIENCE` (Entra ID audience for entra mode). Missing required variables for the configured mode MUST cause a descriptive startup error.
- **FR-051**: Health endpoints (`/healthz`, `/ready`) on the AWReason API are unauthenticated regardless of mode. The system's health-check or readiness probes (if any) targeting the AWReason API MUST NOT send authentication headers to these endpoints.
- **FR-052**: Each test run card in the prompt management panel MUST display a "Review Results" button when the test run status is `pending_review`. Clicking it MUST open a results summary dialog showing each test application with its overall score, eligibility gate pass/fail, and per-category sub-scores. The dialog MUST provide an "Open Manual Review" button that launches the three-pane manual review interface (US7) pre-filtered to the test applications from that specific test run.
- **FR-053**: The system MUST automatically compare AI-assigned scores with manual review scores for each test application upon completion of manual review. If any manual review adjusted a score in any category, the system must prompt the user to choose between 'reject' or 'accept' the test run scores as the variance may be acceptable. is user select "reject" , the test run MUST be marked as `rejected` and the prompt MUST NOT be eligible for production approval. the approval or rejection step is mandatory "human in the loop" step.
- **FR-054**: The "Approve for Production" button MUST appear on the test run card ONLY after the recruiter has completed manual review of ALL test applications in the run  (FR-053). The button MUST NOT appear alongside the "Review Results" button or any intermediate approval controls. The approval flow is: Review Results → Manual Review  → Approve for Production.
If one or more test runs result in rejected status, the user must be returned to the prompt management ui to make improvements to the prompt for another test cycle.
- **FR-055**: Both Stack A (React/TypeScript) and Stack B (.NET/Blazor) MUST implement identical test review workflow UI elements with feature parity: test run cards with "Review Results" button, results summary dialog with per-application scores and eligibility status, and entry into the manual review interface (US7). The API endpoints are shared between stacks.
- **FR-056**: The upload route MUST persist the raw file content (base64-encoded) alongside the document metadata so that the original document can be retrieved for native rendering and for submission to the scoring API. Document blobs MUST be stored in the KV store under a dedicated key per document (`app:{applicationId}:doc:{documentId}:blob` in Stack A; equivalent storage in Stack B).
- **FR-057**: Both stacks MUST expose a document content retrieval endpoint (`GET /api/applications/:applicationId/documents/:documentId/content`) that returns the original file bytes with the correct `Content-Type` header and `Content-Disposition: inline` for browser rendering.
- **FR-058**: The manual review left pane and application detail views MUST render uploaded documents in their native format: PDF via browser-embedded `<iframe>` viewer, DOCX via client-side HTML conversion (e.g. mammoth.js in React, a Blazor equivalent), Markdown via a Markdown renderer, TXT as preformatted `<pre>` text, and images (JPG/PNG) inline via `<img>` tag. If multiple documents exist, a tabbed or selectable list MUST allow switching between them.
- **FR-059**: The scoring pipeline MUST skip the extraction step entirely. The pipeline flow is: Queued → Scoring (single engine call with N runs + aggregation) → Completed/NeedsManualReview. The `Extracting` and `Aggregating` statuses are no longer used for new applications — the engine performs both multi-run scoring and aggregation in a single request. The `ExtractionArtifact` type and local aggregation worker are retained for backward compatibility but are no longer produced/invoked.
- **FR-060**: The scoring worker MUST load the original document blob from storage and send it as the `specFile` parameter in the multipart FormData to the passthrough scoring API, using the document's original MIME type and filename. The `{{CANDIDATE_CV_TEXT}}` prompt placeholder is deprecated; prompts should use only `{{JOB_SPEC_TEXT}}`.
- **FR-061**: The system MUST support two scoring modes — **sequential** and **platform** — determined at startup by comparing the `AWR_PLATFORM_API_ENDPOINT` and `AWR_SEQ_API_ENDPOINT` environment variables. When `AWR_PLATFORM_API_ENDPOINT` is unset, empty, or equal to `AWR_SEQ_API_ENDPOINT`, the system MUST operate in sequential mode. When `AWR_PLATFORM_API_ENDPOINT` is set to a value that differs from `AWR_SEQ_API_ENDPOINT`, the system MUST operate in platform mode. The resolved mode MUST be logged at startup.
- **FR-062**: In **sequential mode**, the pipeline orchestrator MUST send a single scoring request to `AWR_SEQ_API_ENDPOINT/assess/passthrough` specifying the number of runs (N) as a parameter. The engine executes all N passes internally, performs aggregation, and returns the complete set of individual runs plus the aggregated result in a single synchronous response. The request uses the multipart FormData calling convention (FR-045, FR-047) extended with a `runs` parameter.
- **FR-063**: In **platform mode**, the pipeline orchestrator MUST submit production scoring work to `AWR_PLATFORM_API_ENDPOINT` using the platform API's asynchronous submission and result-retrieval pattern. The platform API handles document preprocessing, parallel LLM execution, multi-run scoring, and aggregation at scale. Results returned include both individual runs and the aggregated result, following the same schema as sequential mode. The specific platform API contract (submission endpoint, polling/callback mechanism, response format) MUST be documented in a separate contract addendum (`contracts/scoring-platform.md`) before platform mode implementation begins. All test scoring must make use of the "sequential mode" api
- **FR-064**: Regardless of scoring mode, the scoring output MUST be valid JSON with no assumed fixed schema — the structure is defined entirely by the job's scoring prompt. The parser extracts scores, evidence, and recommendations generically by JSON value type (see FR-045). The ranked lists (FR-016), manual review (FR-014), audit trail (FR-041), and failure handling (FR-013) MUST behave identically in both modes. The scoring mode MUST be transparent to all downstream consumers.
- **FR-065**: Non-scoring operations — prompt management, prompt generation (FR-034), test scoring runs (FR-038, FR-046), job spec extraction, and rubric extraction — MUST always use `AWR_SEQ_API_ENDPOINT/assess/passthrough` regardless of the scoring mode. The platform API endpoint is used exclusively for production and batch scoring operations.
- **FR-066**: Both Stack A and Stack B MUST implement scoring mode detection with identical logic: read `AWR_PLATFORM_API_ENDPOINT` and `AWR_SEQ_API_ENDPOINT` from environment variables, compare them, and route production scoring accordingly. The mode detection logic MUST reside in the pipeline orchestrator layer (`server/services/pipeline.ts` in Stack A; equivalent orchestrator in Stack B) — not in individual workers or route handlers.
- **FR-067**: Authentication for outbound requests to `AWR_PLATFORM_API_ENDPOINT` (platform mode) MUST use the same `AWR_AUTH_MODE` mechanism defined in FR-049 and FR-050. The existing `getAwrAuthHeaders()` (Stack A) and `AwrAuthHandler` (Stack B) MUST be reused with no changes to the auth layer.
- **FR-068**: When Azure SQL is enabled, both Stack A and Stack B MUST treat initial connection failures caused by pay-as-you-go cold-start wake-up as transient and retry with bounded exponential backoff before surfacing a startup failure. The retry policy MUST tolerate a 30-90 second wake-up window, log each retry attempt with elapsed time and error reason, and fail fast only after the configured retry budget is exhausted.
- **FR-069**: In `entra` mode, both stacks MUST use identical role-mapping configuration keys for application-specific Entra security groups (`ENTRA_GROUP_ADMIN`, `ENTRA_GROUP_RECRUITER`, `ENTRA_GROUP_ANALYTICS_VIEWER`) and MUST fail startup with a descriptive error if any required group mapping is missing.
- **FR-070**: In `entra` mode, user/password management features (create local user, reset local password, request password reset, change local password) MUST be hidden or disabled in both stacks and replaced by guidance that identity lifecycle is managed in Entra.
- **FR-071**: Authorization outcomes for identical user claims/groups MUST be equivalent across Stack A and Stack B, including role resolution, organization/department scoping, analytics_viewer read-only constraints, and unauthorized responses.

### Key Entities

- **User**: A person who accesses the system. Key attributes: username, role (`admin`, `recruiter`, or `analytics_viewer`), organization, department, full name, email, account creation date, and last login. In `simple` mode, passwords are stored as secure one-way hashes; in `entra` mode, identity is externalized to Entra ID.
- **Client Auth Configuration**: Environment-level configuration selecting `CLIENT_AUTH_MODE` (`simple` or `entra`) and, for `entra` mode, role-group mappings (`ENTRA_GROUP_ADMIN`, `ENTRA_GROUP_RECRUITER`, `ENTRA_GROUP_ANALYTICS_VIEWER`).
- **Job**: A job opening that applications are scored against. Key attributes: job code, title, department, organisation, posting date, status, and a reference to its current configuration version.
- **Job Configuration Version**: A versioned snapshot of a job's scoring setup. Key attributes: rubric (categories with weights), must-have criteria, number of scoring runs, aggregation strategy, longlist/shortlist thresholds, and variance threshold.
- **Application**: A candidate's submission for a specific job. Tracks status through the pipeline (Queued → Scoring → Completed / Needs Manual Review / Failed), associated documents, final score, final decision, and variance. The `Aggregating` status is deprecated — aggregation is now performed by the scoring engine as part of the scoring step.
- **Application Document**: An individual file within an application (CV, cover letter, etc.). Tracked with cryptographic fingerprint, file type, file size, file name, upload timestamp, and persisted raw file content (base64-encoded blob) for native rendering and scoring API submission.
- **Extraction Artifact**: *(Deprecated — extraction is handled internally by the AWR scoring API.)* Retained for backward compatibility but no longer produced by the pipeline.
- **Scoring Run**: A single AI scoring pass for an application. Records the run index, total score, per-category scores, must-have evaluation, evidence citations, improvement tips, AI model identifier, prompt version, and resource usage.
- **Aggregated Result**: The combined result of all scoring runs for an application, computed by the scoring engine and returned alongside the individual runs. Contains final score, decision (Eligible / Excluded / Needs Manual Review), variance, confidence, consolidated rationale, and merged improvement tips. The platform stores the engine-provided result directly rather than computing aggregation locally.
- **Scoring Prompt**: A versioned prompt used by the external scoring API for a specific job. Key attributes: job reference, version number, prompt text, status (draft / active / inactive / production-approved), creation timestamp, last-modified timestamp, author, rating (0–5, nullable), and comments. Only one prompt may be active per job. A prompt must pass the test-and-review workflow before it can be approved for production scoring.
- **Prompt Test Run**: A scoring pipeline execution triggered for prompt testing purposes. Links a set of test-case applications to a specific prompt revision. Tracks test status (pending review / approved / rejected) and is excluded from production result sets.
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
- **SC-009**: Switching `CLIENT_AUTH_MODE` between `simple` and `entra` changes the authentication entry flow without changing role-based authorization outcomes for equivalent admin/recruiter/analytics_viewer personas.
- **SC-010**: In `entra` mode, 100% of users without mapped application role groups are denied access with an explicit authorization error, and 0 unauthorized mutating actions are permitted for analytics_viewer.

---

## Assumptions

- The platform will be hosted in a cloud environment with access to AI/LLM services.
- Initial deployment targets a single-tenant scenario with a defined set of departments and user roles.
- Document formats accepted for upload include common file types (PDF, DOCX, MD); exotic formats are out of scope for initial release.
- The admin user account is pre-created during initial deployment; subsequent users are managed through the admin interface.
- `CLIENT_AUTH_MODE` is configured consistently per environment and propagated equally to both stacks.
- In `entra` mode, application-specific Entra security groups exist and are pre-mapped for `admin`, `recruiter`, and `analytics_viewer` roles.
- AI model availability and rate limits are managed externally; the platform handles transient failures through retry mechanisms.
- The AWReason engine API (`AWR_SEQ_API_ENDPOINT`) requires authentication configured via `AWR_AUTH_MODE`. Local development uses `none`; staging uses `apikey` with a shared secret; production uses `entra` (Entra ID JWT). See FR-049–FR-051.
- The system supports two scoring modes: sequential (using `AWR_SEQ_API_ENDPOINT`) and platform (using `AWR_PLATFORM_API_ENDPOINT`). The mode is determined by comparing the two endpoint values at startup. When both point to the same URL (or `AWR_PLATFORM_API_ENDPOINT` is unset), sequential mode is used. See FR-061–FR-067.
- Scoring rubrics are defined per job; there is no global rubric library in the initial release.
- The platform handles English-language documents and job specifications; multi-language support is a future consideration.

---

## Scope & Boundaries

### In Scope

- User authentication mode switch (`simple` or `entra`) with role-based access control (`admin`, `recruiter`, `analytics_viewer`)
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
- Multi-IdP federation beyond Microsoft Entra ID

---

## Clarifications

### Session 2026-03-14

- Q: How does the recruiter navigate from the test run in prompt management to the manual review (US7) interface? → A: Each test run card shows a "Review Results" button when status is `pending_review`. Clicking it opens the manual review interface (US7) pre-filtered to show only the test applications from that specific test run, using the same three-pane interface.
- Q: Should test run results show a summary before the recruiter enters full manual review? → A: Yes. The "Review Results" button first shows an expandable results summary dialog with each test application's overall score, eligibility gate pass/fail, and per-category sub-scores. From there, the recruiter can click "Open Manual Review" or close and return later.
- Q: How does the system detect whether manual review required score changes (for the approval gate in Scenario 14-15)? → A: Automatic comparison of AI-assigned scores vs manual review scores per test application. If any category score was adjusted, the test run is marked `rejected` and the prompt cannot be approved for production.
- Q: Should this test review workflow be identical in both Stack A (React) and Stack B (Blazor)? → A: Yes. Both stacks must have feature parity: test run cards with Review Results button, results summary dialog, and entry into manual review. The API endpoints are shared.
- Q: Where should the "Approve for Production" button appear? → A: On the test run card ONLY after the recruiter has completed manual review of ALL test applications AND no score changes were required. It must NOT appear alongside "Review Results" — the flow is: Review Results → Manual Review → (if no changes) → Approve for Production appears on the card.

### Session 2026-03-20

- Q: Should the platform perform its own aggregation of scoring runs, or delegate to the scoring engine? → A: The scoring engine has built-in automatic aggregation when multiple runs are requested for the same input. The platform MUST delegate aggregation to the engine rather than running its own aggregation step. The pipeline submits a single request per application with the run count (N), and the engine returns both individual run results and the aggregated result (final score, variance, confidence, decision). This eliminates the separate `Aggregating` pipeline step and simplifies the flow to: Queued → Scoring → Completed. See updated FR-009, FR-011, FR-059.
- Q: What happens to the existing local aggregation worker? → A: The local aggregation worker (`server/workers/aggregation.ts` in Stack A; equivalent in Stack B) is deprecated for new scoring flows but retained for backward compatibility. The pipeline orchestrator no longer calls it — the engine-provided aggregated result is stored directly.
- Q: Does this change affect the scoring passthrough contract? → A: Yes. The passthrough API contract (`contracts/scoring-passthrough.md`) must be updated to include the `runs` parameter in the request and the aggregated result alongside individual runs in the response.

### Session 2026-03-19

- Q: Can the scoring pipeline support both a simple sequential API path and the full scalable platform architecture? → A: Yes. The system supports dual-mode scoring determined by environment configuration. When `AWR_PLATFORM_API_ENDPOINT` equals `AWR_SEQ_API_ENDPOINT` (or is unset), sequential mode is used — the same synchronous passthrough endpoint already used for prompt management, test scoring, and extraction. When they differ, platform mode routes production scoring to the scalable platform API. See FR-061–FR-067.
- Q: What is the impact on existing code? → A: Minimal. The mode detection logic is a simple string comparison at the pipeline orchestrator level. Sequential mode is the existing behaviour — zero changes to current workers, routes, or services. Platform mode requires a new submission/retrieval path in the orchestrator but reuses the same auth layer, output schema, and downstream logic. Non-scoring operations (prompt management, extraction, test runs) always use the sequential endpoint.
- Q: Should test scoring runs also use platform mode when configured? → A: No. Test scoring runs (FR-038, FR-046) always use `AWR_SEQ_API_ENDPOINT` regardless of the scoring mode configuration. Test runs are low-volume, interactive workflows where synchronous response is appropriate. Platform mode is reserved for production/batch scoring.
- Q: What needs to happen before platform mode can be implemented? → A: The platform API contract (submission endpoint, polling/callback mechanism, response format mapping) must be documented in `contracts/scoring-platform.md`. The sequential mode is fully functional today and serves as the baseline.

### Session 2026-03-16

- Q: Should the application call the API to extract markdown content before scoring? → A: No. The `AWR_SEQ_API_ENDPOINT` passthrough API handles document preprocessing (including OCR) internally. The extraction step is removed from the pipeline. The scoring worker sends the original uploaded document directly to the API.
- Q: What should the left panel of the manual review 3-panel layout show? → A: The original document rendered in its native format (PDF via embedded viewer, DOCX converted to HTML client-side, Markdown rendered, TXT as preformatted text, images inline) — not extracted markdown text.
- Q: What happens to the existing extraction worker and ExtractionArtifact? → A: The extraction worker (`server/workers/extraction.ts`) and `ExtractApplicationCommand` (.NET) are deprecated. The `ExtractionArtifact` type and storage keys are retained for backward compatibility but no longer produced. The pipeline skips directly from Queued to Scoring.
- Q: How does the scoring worker get the document content if extraction is removed? → A: The upload route now persists raw file content (base64) in the KV store. The scoring worker loads the document blob and sends it as `specFile` in the FormData to the passthrough API with the correct MIME type. The `{{CANDIDATE_CV_TEXT}}` placeholder is deprecated.
- Q: How is the document content served to the frontend for rendering? → A: A new endpoint `GET /api/applications/:applicationId/documents/:documentId/content` returns the raw file bytes with the correct `Content-Type` header for browser rendering.
