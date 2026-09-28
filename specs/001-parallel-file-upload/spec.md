# Feature Specification: Optional Parallel File Uploads

**Feature Branch**: `001-parallel-file-upload`  
**Created**: 2026-09-22  
**Status**: Draft  
**Input**: User description: "Create an optional, background-capable path that uploads selected application/resume documents individually with bounded parallelism, durable status, safe retries, administrator-configurable limits, preserved duplicate semantics, and no changes to the default upload path or scoring behavior."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Choose an Optional Background Upload (Priority: P1)

As a recruiter uploading application documents for a job, I can deliberately select a parallel individual-file upload option next to the duplicate-document option so that a large selection can continue after I close the upload dialog and navigate elsewhere in the application.

**Why this priority**: This is the primary user value. It adds a faster, non-blocking choice while protecting the established upload experience for users who do not opt in.

**Independent Test**: Select multiple valid documents, enable the new option, start the upload, close the dialog, and navigate to another in-app view. The upload remains active, each eligible file is processed independently within configured limits, and successfully uploaded applications enter the existing queued/scoring flow.

**Acceptance Scenarios**:

1. **Given** a user opens the existing upload dialog, **When** the dialog is first displayed, **Then** the current upload path is selected by default and the duplicate-document choice retains its current default.
2. **Given** valid documents are selected and the parallel upload option is enabled, **When** the user starts the upload, **Then** an upload session is recorded before file transfer begins and the files are scheduled as individual upload items.
3. **Given** a parallel upload session is active, **When** the user closes the dialog or navigates to another view within the application, **Then** eligible uploads continue while the browser tab remains open.
4. **Given** the configured concurrency or in-flight byte limit has been reached, **When** another file is ready, **Then** that file waits without being marked failed and begins only after capacity becomes available.
5. **Given** a file uploads successfully, **When** it enters the established downstream flow, **Then** it remains compatible with the current queued and scoring behavior without changing scoring decisions or scoring parallelism.

---

### User Story 2 - Monitor and Isolate File Outcomes (Priority: P2)

As a recruiter, I can see durable aggregate progress and structured status for every selected file so that I know what completed, what is waiting, and what needs attention without one failure obscuring or stopping the rest.

**Why this priority**: Background work is trustworthy only when users can observe progress and understand partial success or failure.

**Independent Test**: Start a parallel upload containing successful files, a duplicate, and a file that encounters a retriable and then a terminal failure. Verify the Job Details pipeline shows the latest upload action, its same-page drill-down opens the related Upload activity details, the dashboard shows only an active-upload aggregate, and unaffected files continue.

**Acceptance Scenarios**:

1. **Given** an active upload session, **When** its state changes, **Then** the user can see aggregate counts and progress plus each file's name, size, status, and available error or duplicate explanation.
2. **Given** one file fails after safe retries, **When** other files remain eligible, **Then** only that file is marked failed and all other files continue independently.
3. **Given** a transient failure occurs for a file, **When** the system retries it, **Then** the retry cannot create more than one application for that selected file occurrence.
4. **Given** duplicate uploads are disabled, **When** matching content is found within the selection or already belongs to the job, **Then** the file is skipped and identified as a duplicate according to the current duplicate-handling behavior.
5. **Given** duplicate uploads are enabled, **When** matching content is intentionally selected, **Then** each selected occurrence may create one application, while retries of the same occurrence create no additional application.
6. **Given** the browser tab was closed or reloaded during an upload, **When** the user later returns, **Then** the recorded upload session and all server-known completed outcomes remain observable, and incomplete files are clearly identified as no longer uploading.
7. **Given** one or more upload sessions exist for a job, **When** Job Details renders Pipeline Status, **Then** an **Uploads** stage shows the number submitted in the latest upload action for that job.
8. **Given** the latest upload action contains nonterminal or problem items, **When** Pipeline Status renders, **Then** waiting/throttled/uploading work is light blue, failed/retrying work is orange, and completed or otherwise terminal capacity is the grey remainder.
9. **Given** the user activates **Uploads** in Pipeline Status, **When** the drill-down runs, **Then** the current `/jobs/{jobId}` route is preserved, Upload activity expands, the related session expands, and browser focus/scroll moves to that section.
10. **Given** the user views the dashboard or a System Configuration page, **When** the page renders, **Then** Upload activity details are absent; the dashboard instead shows **Active Uploads** between **Applications** and **Queued**, counting all owned nonterminal upload items.

---

### User Story 3 - Configure Safe Upload Capacity (Priority: P3)

As an authorized system administrator, I can open **System Configuration > Settings** and configure the optional upload path's capacity and size limits so that resource use can be controlled independently from scoring.

**Why this priority**: Safe operational controls are necessary before the optional path can be used at scale, but they support rather than define the core user journey.

**Independent Test**: As a system administrator, change each upload setting, start a new parallel upload, and verify that the new session obeys the saved values. Verify that unauthorized users cannot view or change these settings and that scoring capacity is unchanged.

**Acceptance Scenarios**:

1. **Given** a system administrator opens System Configuration, **When** the submenu is displayed, **Then** it includes an option named **Settings**.
2. **Given** no custom upload settings have been saved, **When** Settings is viewed, **Then** it shows defaults of 4 concurrent file uploads, 4 MB maximum per file, and 100 MB total raw bytes in flight.
3. **Given** upload runners or workers are managed separately from concurrent file uploads, **When** Settings is viewed without a saved value, **Then** the runner/worker count defaults to 1 and can be configured independently.
4. **Given** an administrator enters invalid or internally conflicting limits, **When** the administrator tries to save them, **Then** the values are rejected with a clear explanation and the last valid settings remain active.
5. **Given** settings are changed while a session is active, **When** uploads continue, **Then** the active session retains the limits effective when it started and the new values apply to subsequently created sessions.
6. **Given** the System Configuration submenu is expanded, **When** a submenu option is hovered, focused, or active, **Then** its full wrapped label is covered by one consistent full-width visual target.
7. **Given** the administrator opens Optional upload settings, **When** the form renders, **Then** its title uses the same heading level as the other System Configuration pages and all three labels and numeric inputs align consistently, collapsing to one column on narrow screens.

### Edge Cases

- A user starts an upload with no eligible files: no session starts, and the user is told to select at least one valid file.
- A file is exactly the configured individual-size limit: it is eligible; a file one byte larger is rejected before transfer with a file-specific explanation.
- The selected files total more than the in-flight byte limit: the selection remains eligible, but files wait until enough capacity is available.
- A single file cannot fit under the configured in-flight limit: administrators cannot save a total in-flight limit lower than the maximum individual file size.
- Concurrency capacity is available but starting the next file would exceed the byte limit: the file waits rather than failing or causing the limit to be exceeded.
- Multiple files have identical content: duplicate-disabled sessions skip matching occurrences according to current behavior; duplicate-enabled sessions permit one result per selected occurrence.
- A response is lost after a file was accepted: retrying the same file occurrence reports the already-recorded result rather than creating another application.
- A transient problem persists beyond the retry allowance: that file becomes failed with an actionable message, while unrelated files continue.
- Four browser transport failures that never reach the content endpoint are counted
  independently of server attempts and end in the same durable `retry_exhausted`
  terminal outcome.
- The user closes and reopens the upload dialog during an active session: the existing session continues and remains visible in the aggregate status surface; reopening the dialog does not duplicate the session.
- The user navigates repeatedly within the application: active work and the progress surface remain available.
- The user activates the Job Details Uploads drill-down: a fragment or base-URI resolution MUST NOT navigate to the dashboard; the action stays on the same job route and focuses the expanded Upload activity section.
- The browser tab is closed, refreshed, crashes, or loses local access to selected files: continuation is not guaranteed; completed outcomes remain durable, and unfinished items are shown as interrupted rather than incorrectly shown as active or successful.
- A non-administrator attempts to access or change upload settings: access is denied without revealing controls that the user cannot use.
- An administrator changes settings during active work: the active session remains internally consistent, and only later sessions receive the new limits.
- The optional path receives a currently unsupported document type: existing file-type validation behavior is preserved.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The upload dialog MUST present an optional parallel individual-file upload control next to the existing **Allow duplicate documents** control.
- **FR-002**: The optional control MUST default to off each time a new upload selection flow begins.
- **FR-003**: When the optional control is off, the system MUST use the current upload path without changing its submission, validation, progress, duplicate handling, dialog-closing behavior, results, or downstream scoring behavior.
- **FR-004**: The current path and optional path MUST remain operationally isolated so that configuration or failure of the optional path does not alter or disable the current path.
- **FR-005**: When the optional control is on, the system MUST create a durable upload session and a distinct upload item for every selected file occurrence before beginning file uploads.
- **FR-006**: The optional path MUST upload each eligible file independently and MUST enforce the concurrency limit effective for that session.
- **FR-007**: The optional path MUST enforce the total raw-byte in-flight limit effective for that session across all files actively being uploaded.
- **FR-008**: Reaching either capacity limit MUST queue or pause eligible work until capacity becomes available and MUST NOT by itself fail, reject, or skip a file.
- **FR-009**: Users MUST be able to close the upload dialog after starting the optional path without canceling the session.
- **FR-010**: In-app navigation MUST NOT interrupt active uploads while the originating browser tab remains open.
- **FR-011**: Closing, reloading, or losing the originating browser tab MAY stop unfinished uploads; the system is not required to continue transferring local files without that tab.
- **FR-012**: The system MUST preserve the durable upload session and every server-known completed, skipped, or failed outcome even if the originating tab closes or reloads.
- **FR-013**: Each upload item MUST expose one structured status from: waiting, throttled, uploading, retrying, succeeded, skipped as duplicate, failed, or interrupted.
- **FR-014**: A terminal failure for one upload item MUST NOT cause other eligible items in the same session to fail, stop, or roll back.
- **FR-015**: The system MUST safely retry transient per-file failures up to three additional attempts before marking that file failed.
- **FR-016**: Each selected file occurrence MUST have a stable identity within its upload session, and every retry or repeated result submission for that occurrence MUST resolve to no more than one application.
- **FR-017**: With duplicate handling disabled, the optional path MUST preserve the current rules for matching content already associated with the job and repeated content within the same selection.
- **FR-018**: With duplicate handling enabled, the optional path MUST permit one application for each intentionally selected file occurrence while still preventing retries of an occurrence from creating extra applications.
- **FR-019**: Successfully uploaded applications MUST enter the same existing queued/scoring flow as applications uploaded through the current path.
- **FR-020**: Upload concurrency and runner/worker capacity MUST be independent of all scoring concurrency and scoring worker settings.
- **FR-021**: The application MUST preserve upload state independently from the upload dialog and route lifetime. In Stack B, detailed Upload activity MUST be rendered only on Job Details; the dashboard MUST expose only the active-upload aggregate and System Configuration pages MUST expose neither.
- **FR-022**: The Job Details Upload activity surface MUST show, for each visible session, total file count, aggregate progress, and counts for waiting/throttled, active/retrying, succeeded, skipped, failed, and interrupted items.
- **FR-023**: From Job Details, users MUST be able to inspect each item's file name, raw size, current status, attempt count, and a user-actionable explanation when it is skipped, failed, or interrupted.
- **FR-024**: The **System Configuration** menu MUST contain a submenu option named **Settings** for authorized system administrators.
- **FR-025**: Settings MUST allow authorized system administrators to view and change optional-path concurrency, maximum individual document size, and total raw bytes allowed in flight.
- **FR-026**: If upload runner/worker count is an independently managed capacity in the adopted operating model, Settings MUST also allow authorized system administrators to view and change it separately from file concurrency.
- **FR-027**: In the absence of saved configuration, the system MUST use 4 concurrent file uploads, a 4 MB maximum individual file size, a 100 MB total raw-byte in-flight limit, and—when independently modeled—1 upload runner/worker.
- **FR-028**: Settings MUST accept only positive whole numbers for concurrency and any separate runner/worker count, positive size limits, and a total in-flight limit greater than or equal to the maximum individual file size.
- **FR-029**: Invalid settings MUST NOT become active and MUST produce a clear field-specific explanation.
- **FR-030**: Each upload session MUST retain the validated settings effective at its creation; later changes MUST apply only to newly created sessions.
- **FR-031**: Only authorized system administrators MUST be able to view or change optional-path settings; existing authorization terminology and boundaries MUST be preserved.
- **FR-032**: Starting a session with no eligible files MUST be prevented with a clear user-facing explanation.
- **FR-033**: A file at or below the configured individual-size limit MUST remain eligible, while an oversized file MUST be rejected individually without preventing eligible files from starting.
- **FR-034**: The feature MUST preserve existing supported document types and validation rules except for the optional path's separately configurable 4 MB default size limit.
- **FR-035**: Stack B Job Details Pipeline Status MUST include an **Uploads** stage based on the newest upload session for the current job and MUST visibly state how many files were submitted in that action.
- **FR-036**: The **Uploads** stage MUST render waiting/throttled/uploading items as a decreasing light-blue segment, failed/retrying items as an orange segment, and completed or otherwise terminal items as the grey remainder.
- **FR-037**: Activating the **Uploads** stage MUST be a same-page action, MUST NOT change the current job route, and MUST expand and focus/scroll the related session in Upload activity.
- **FR-038**: Stack B MUST render Upload activity details on Job Details only, not from the global layout, dashboard, or any System Configuration page.
- **FR-039**: The Stack B dashboard MUST render **Active Uploads** between **Applications** and **Queued** and calculate it as the sum of `Total - Terminal` across the authenticated user's visible upload sessions.
- **FR-040**: System Configuration submenu links MUST provide a full-width hover/focus/active target that covers wrapped option text without clipping or detached shading.
- **FR-041**: Optional upload settings MUST use the System Configuration heading hierarchy and a consistently aligned three-row label/input layout, with a single-column responsive layout on narrow screens.
- **FR-042**: An active upload session MUST renew its heartbeat before any status read that can reconcile stale work, concurrent item transitions MUST NOT invalidate that renewal, and transient heartbeat gaps shorter than two minutes MUST NOT mark live files interrupted.
- **FR-043**: Job Details MUST load application aggregates and the shortlist initially without materializing every application; longlist, excluded, and review lists MUST load only when selected.
- **FR-044**: Job Details MUST provide a **Review** filter containing only applications whose status or final decision is `NeedsManualReview`. Ordinary AI exclusions MUST remain in Excluded and MAY expose a separately labelled **Review decision** action without being counted as requiring review.

### Key Entities

- **Upload Session**: A durable intention to upload a user's selected files for one job through the optional path. It records ownership and job context, duplicate preference, configuration effective at creation, aggregate status/counts, and creation and completion times.
- **Upload Item**: One selected file occurrence within an upload session. It records stable occurrence identity, file name and raw size, current structured status, attempt count, user-facing outcome, and any resulting application reference. Two intentionally selected matching files remain separate items.
- **Upload Settings**: Administrator-managed limits for optional-path file concurrency, maximum individual document size, total raw bytes in flight, and a separate runner/worker count only when the operating model distinguishes that capacity.
- **Application**: The existing job application created by one successfully completed upload item and handed to the established queued/scoring flow.

### Assumptions and Dependencies

- The existing system-wide administrator role is the authorized role for **System Configuration > Settings**; organization-scoped administrators and other roles are not granted this system-level capability.
- Repository context has no separate upload-runner capacity model today. The baseline therefore uses one configurable file-concurrency setting with a default of 4. If planning introduces a separately operated runner pool, its independent default is 1.
- Three additional retry attempts provide a bounded, testable default for transient file-level failures; permanent validation or authorization failures are not retried.
- Upload settings are snapshotted per session so an administrator's later changes cannot create inconsistent limits within active work.
- Current supported file types, job association rules, content-based duplicate detection, and authorization for uploading applications remain authoritative.
- The established queued/scoring flow can accept each successfully created application independently.
- Implementation sequencing is strict: the .NET/Blazor Clean Architecture Stack B is implemented first and must pass its legacy-off and optional-path regression gate before any React/TypeScript and Express Stack A implementation begins. Later tasks must encode every Stack A implementation task as dependent on that Stack B gate.

### Scope Boundaries

**Included**:

- An opt-in upload execution choice in the existing dialog.
- Durable session intent, individual file outcomes, aggregate progress, throttling, bounded retries, and navigation-safe activity while the browser tab remains open.
- System-administrator settings for upload-only limits.
- Compatibility with current duplicate handling and downstream queued/scoring behavior.

**Excluded**:

- Guaranteed transfer continuation after browser close, reload, crash, or loss of local file access.
- Durable storage of or later automatic access to users' local source files.
- Cloud-folder ingestion or synchronization.
- Desktop upload agents.
- Changes to scoring rules, scoring results, scoring capacity, or scoring parallelism.
- Behavioral changes to the current upload path when the optional control is off.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In 100% of new upload-dialog sessions, the current upload path remains selected unless the user explicitly opts into the parallel path.
- **SC-002**: For a 100-file optional-path test containing valid files within configured size limits, no more than the configured number of files upload concurrently and total active raw bytes never exceed the configured in-flight limit.
- **SC-003**: When capacity is saturated in controlled tests, 100% of otherwise eligible excess files wait and later proceed as capacity becomes available; none fail solely because a capacity limit was reached.
- **SC-004**: In controlled partial-failure tests, a failed file has no effect on the final outcome of the other 99 files in a 100-file session.
- **SC-005**: Across retry, lost-response, and repeated-result test cases, each selected file occurrence creates at most one application, including when intentional duplicates are enabled.
- **SC-006**: Users can close the upload dialog and navigate to another in-app view within 2 seconds of starting a session, while eligible uploads continue and progress remains available.
- **SC-007**: At least 95% of observed per-file state changes appear on Job Details Upload activity within 2 seconds, and terminal outcomes remain visible after leaving and returning to that job.
- **SC-008**: In role-based acceptance tests, 100% of authorized system administrators can view and save valid upload settings, and 100% of unauthorized users are prevented from viewing or changing them.
- **SC-009**: All new sessions use the most recently saved valid settings, while 100% of sessions already active during a settings change continue with their original limits.
- **SC-010**: Existing upload acceptance and regression tests pass without changed expectations when the optional control is off.
- **SC-011**: In duplicate-behavior parity tests, both duplicate-disabled and duplicate-enabled outcomes match the current path's intentional duplicate semantics, apart from the retry protection required for each file occurrence.
- **SC-012**: At least 90% of representative users can opt into a background upload, leave the dialog, use the Job Details Uploads stage to open the related Upload activity session without changing routes, and identify any failed file without assistance.
- **SC-013**: A 67-file optional upload completes without any live item being marked `interrupted` solely because item updates race with heartbeat persistence.
- **SC-014**: Opening a job with more than 100 applications initially transfers only aggregate counts and shortlist rows; selecting longlist, excluded, or review causes exactly that list to be requested.
