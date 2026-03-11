# Feature Specification: Edit Job UI for Stack B

**Feature Branch**: `004-edit-job-stack-b`  
**Created**: 2026-03-11  
**Status**: Draft  
**Input**: User description: "Add Edit Job UI to Stack B (.NET Blazor). Stack A already has this feature: a pencil/edit button on the Job Detail view opens the Create Job dialog pre-populated with the existing job's values (title, jobCode, department, organization, postingDate, rubric, mustHaves, desiredCriteria, jobDescription, runsPerApplication, aggregationStrategy, thresholds). On save it calls PUT /api/jobs/{jobId}/config which already exists in Stack B (UpdateJobConfigCommand + endpoint at JobsEndpoints.cs line 175 + ApiClient.UpdateJobConfigAsync). Only the Blazor UI is missing — no backend work needed."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Edit an Existing Job's Configuration (Priority: P1)

A hiring manager or recruiter navigates to the Job Detail page for an existing job posting. They notice the job's rubric categories need adjusting or the scoring thresholds need fine-tuning. They click the edit (pencil) button, which opens a dialog pre-populated with all of the job's current configuration values. They modify the fields they need to change, click save, and the updated configuration is persisted. The dialog closes and the Job Detail view reflects the updated values.

**Why this priority**: This is the core feature — without the ability to open, view, and save edits, nothing else matters. It delivers immediate value by giving Stack B users parity with the Stack A editing experience.

**Independent Test**: Can be fully tested by navigating to any existing job, clicking the edit button, modifying a field, saving, and confirming the change persists on page reload.

**Acceptance Scenarios**:

1. **Given** a user is on the Job Detail page for an existing job, **When** they click the edit (pencil) button, **Then** a dialog opens with all job configuration fields pre-populated with the current values.
2. **Given** the edit dialog is open with pre-populated values, **When** the user modifies one or more fields and clicks Save, **Then** the updated configuration is saved and the dialog closes.
3. **Given** the edit dialog is open, **When** the user clicks Save successfully, **Then** the Job Detail view refreshes to display the updated values without requiring a manual page reload.
4. **Given** the edit dialog is open, **When** the user clicks Cancel or closes the dialog without saving, **Then** no changes are persisted and the job retains its original configuration.

---

### User Story 2 - Pre-populated Fields Match Current Job State (Priority: P1)

A user opens the edit dialog for a job and sees every field accurately reflecting the job's current stored configuration. This includes basic metadata (title, job code, department, organization, posting date, job description) and scoring configuration (rubric categories, must-have criteria, desired criteria, runs per application, aggregation strategy, longlist threshold, shortlist threshold).

**Why this priority**: If pre-population is inaccurate or incomplete, users may unknowingly overwrite correct values. This is essential for trust and usability.

**Independent Test**: Can be tested by creating a job with known values, opening the edit dialog, and verifying each field matches the expected value.

**Acceptance Scenarios**:

1. **Given** a job exists with specific configuration values, **When** the user opens the edit dialog, **Then** each field (title, jobCode, department, organization, postingDate, jobDescription, rubric, mustHaves, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, shortlistThreshold) displays the current stored value.
2. **Given** a job's configuration was previously edited, **When** the user opens the edit dialog again, **Then** the fields reflect the most recently saved values (not the original creation values).

---

### User Story 3 - Validation Feedback Before Saving (Priority: P2)

A user editing a job enters invalid data — for example, rubric category weights that do not sum to 1.0, or a blank required field. The system prevents saving and displays clear validation messages so the user can correct the errors before resubmitting.

**Why this priority**: Validation prevents data corruption and gives users confidence that their edits are valid. It is important but secondary to the core edit flow.

**Independent Test**: Can be tested by opening the edit dialog, entering known-invalid data (e.g., clearing the title field or setting rubric weights that sum to 0.5), clicking Save, and verifying that appropriate error messages appear and the save is blocked.

**Acceptance Scenarios**:

1. **Given** the edit dialog is open, **When** the user clears a required field (e.g., title) and clicks Save, **Then** a validation message is displayed and the save is blocked.
2. **Given** the edit dialog is open, **When** the user enters rubric category weights that do not sum to 1.0 and clicks Save, **Then** a validation message indicates the weights must total 1.0.
3. **Given** validation errors are displayed, **When** the user corrects the errors, **Then** the validation messages clear and saving becomes possible.

---

### User Story 4 - Error Handling on Save Failure (Priority: P3)

A user makes valid edits and clicks Save, but the save fails due to a network error or server-side issue. The system displays a user-friendly error message, preserves the user's in-progress edits in the dialog (does not close it), and allows the user to retry.

**Why this priority**: While failures should be rare, gracefully handling them prevents data loss and user frustration. Lower priority because it's an exceptional path.

**Independent Test**: Can be tested by simulating a network failure during save and verifying the error message appears, the dialog remains open, and the user's edits are preserved for retry.

**Acceptance Scenarios**:

1. **Given** the user clicks Save and the server returns an error, **When** the error occurs, **Then** a user-friendly error message is displayed in the dialog.
2. **Given** a save error has occurred, **When** the user views the dialog, **Then** all of their in-progress edits are still present (not reset).
3. **Given** a save error has occurred, **When** the user clicks Save again after the issue resolves, **Then** the save succeeds and the dialog closes normally.

---

### Edge Cases

- What happens when the user opens the edit dialog while another user has concurrently edited the same job? The most recent save wins (last-write-wins), consistent with the existing versioned configuration approach.
- What happens if the job is deleted while the edit dialog is open? The save attempt should fail gracefully with a message indicating the job no longer exists.
- What happens when the user navigates away from the Job Detail page while the edit dialog is open? Any unsaved changes are discarded (consistent with standard dialog behavior).
- What happens with very long job descriptions or large numbers of rubric categories? The dialog should handle scrolling gracefully without truncating content.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST display an edit button (pencil icon) on the Job Detail view, consistent with the Stack A design pattern.
- **FR-002**: System MUST open an edit dialog when the edit button is clicked, pre-populated with all current job configuration values.
- **FR-003**: The edit dialog MUST include editable fields for: title, jobCode, department, organization, postingDate, jobDescription, rubric categories (with weights), mustHaves criteria, desiredCriteria, runsPerApplication, aggregationStrategy, longlistThreshold, and shortlistThreshold.
- **FR-004**: System MUST save updated configuration by calling the existing PUT /api/jobs/{jobId}/config endpoint on form submission.
- **FR-005**: System MUST validate required fields and rubric weight totals before allowing save.
- **FR-006**: System MUST display validation errors inline, adjacent to the relevant fields.
- **FR-007**: System MUST close the edit dialog and refresh the Job Detail view upon successful save.
- **FR-008**: System MUST keep the dialog open with user edits preserved if save fails, and display a user-friendly error message.
- **FR-009**: System MUST discard all unsaved changes when the user cancels or closes the dialog.
- **FR-010**: The edit dialog MUST match the look and feel of the existing Create Job dialog in Stack A to maintain feature parity across stacks.
- **FR-011**: Each successful save MUST create a new configuration version (handled by existing backend), preserving edit history.
- **FR-012**: System MUST NOT require any backend changes — all functionality MUST use existing endpoints and commands.

### Key Entities

- **Job**: The primary entity representing a job posting. Key attributes: title, jobCode, department, organization, postingDate, jobDescription. Has a reference to its current configuration version.
- **Job Configuration Version**: A versioned snapshot of a job's scoring and evaluation settings. Key attributes: rubric categories (with weights), must-have criteria, desired criteria, runs per application, aggregation strategy, longlist threshold, shortlist threshold. Each edit creates a new version, preserving history.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can open the edit dialog, modify a job's configuration, and save changes in under 60 seconds for typical edits (changing 1-3 fields).
- **SC-002**: 100% of the job configuration fields listed in FR-003 are correctly pre-populated when the edit dialog opens.
- **SC-003**: Users see validation feedback within 1 second of attempting to save with invalid data.
- **SC-004**: After a successful save, the Job Detail view displays updated values without requiring a manual page refresh.
- **SC-005**: The edit job workflow in Stack B is functionally equivalent to Stack A — users can perform the same editing tasks in both stacks.
- **SC-006**: Save failures display a clear error message and preserve the user's in-progress edits 100% of the time.

## Assumptions

- The existing PUT /api/jobs/{jobId}/config endpoint accepts all fields listed in FR-003 and handles versioned configuration storage. No backend modifications are needed.
- The existing Create Job dialog in Stack A serves as the reference design for the edit dialog's layout and field arrangement.
- The Blazor Job Detail page already loads sufficient job data to pre-populate the edit dialog, or can retrieve it via existing API calls.
- Concurrent editing follows a last-write-wins strategy, consistent with the existing versioned configuration model.
- The edit button is visible to all users who can access the Job Detail page (no additional permission model required beyond existing access control).
