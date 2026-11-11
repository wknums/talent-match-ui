# Feature Specification: Delete Jobs and Enhanced Job Cards

**Feature Branch**: `003-delete-jobs-enhanced-cards`  
**Created**: March 10, 2026  
**Status**: Draft  
**Input**: User description: "Delete Jobs and Enhanced Job Cards"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Admin Deletes a Job from Dashboard (Priority: P1)

An admin user notices a job was created in error (e.g., duplicate posting, wrong department, test data). They navigate to the Dashboard, locate the job card, and click a delete button. A confirmation dialog appears showing the job title and asking them to confirm. After confirming, the job and all its associated data (applications, scoring runs, config versions) are permanently removed. The job card disappears from the Dashboard.

**Why this priority**: Deleting erroneous jobs is the core ask. Without this, admins have no way to clean up bad data, which pollutes dashboards and reporting.

**Independent Test**: Can be fully tested by creating a job, then deleting it from the Dashboard, and verifying it no longer appears in any listing.

**Acceptance Scenarios**:

1. **Given** an admin is on the Dashboard with at least one job card visible, **When** they click the delete button on a job card, **Then** a confirmation dialog appears showing the job title and asking for confirmation.
2. **Given** the confirmation dialog is displayed for a job with no processed applications, **When** the admin confirms deletion, **Then** the job is permanently removed and the job card disappears from the Dashboard without a page reload.
3. **Given** the confirmation dialog is displayed, **When** the admin cancels, **Then** the dialog closes and the job remains unchanged.
4. **Given** a non-admin user views a job card on the Dashboard, **When** they look at the card, **Then** no delete button is visible.

---

### User Story 2 - Admin Deletes a Job with Applications (Priority: P1)

An admin wants to delete a job that already has processed applications (resumes scored, reviews completed, etc.). When they click delete, the confirmation dialog shows an additional warning explaining that all associated applications, scoring data, and configuration versions will be permanently deleted. The admin must explicitly acknowledge this before the deletion proceeds.

**Why this priority**: Cascade deletion of application data is a destructive and irreversible action. The warning prevents accidental data loss and is critical for data integrity and trust.

**Independent Test**: Can be tested by creating a job, uploading applications against it, then deleting the job and verifying all associated applications are also removed.

**Acceptance Scenarios**:

1. **Given** an admin clicks delete on a job card for a job that has one or more applications, **When** the confirmation dialog appears, **Then** it includes a warning message indicating the number of applications that will also be deleted.
2. **Given** the warning dialog is shown, **When** the admin confirms deletion, **Then** the job and all associated applications, scoring data, and config versions are permanently removed.
3. **Given** the warning dialog is shown, **When** the admin cancels, **Then** nothing is deleted and the dialog closes.

---

### User Story 3 - Admin Deletes a Job from the Job Detail Page (Priority: P2)

An admin is reviewing a job on its detail page and decides it needs to be deleted. They click a delete button on the detail page, see the same confirmation (with cascade warning if applicable), and upon confirmation are redirected back to the Dashboard.

**Why this priority**: Provides a secondary access point for deletion. Less critical than Dashboard deletion since admins can always navigate back, but improves workflow when already viewing a job.

**Independent Test**: Can be tested by navigating to a job detail page, clicking delete, confirming, and verifying redirect to Dashboard with the job removed.

**Acceptance Scenarios**:

1. **Given** an admin is on the Job Detail page, **When** they look at the page actions, **Then** a delete button is visible.
2. **Given** an admin clicks delete on the Job Detail page for a job with applications, **When** the confirmation dialog appears, **Then** it includes the cascade warning with application count.
3. **Given** the admin confirms deletion from the Job Detail page, **When** the deletion succeeds, **Then** they are redirected to the Dashboard and the job no longer appears.

---

### User Story 4 - Enhanced Job Cards Show Creator Name (Priority: P2)

A user (any role) views the Dashboard and sees job cards that now display the full name of the person who created each job. This helps teams understand ownership and accountability for job postings at a glance.

**Why this priority**: Creator attribution adds immediate context to every job card. It helps teams coordinate without clicking into details. Slightly lower than delete because it's additive rather than fixing a gap.

**Independent Test**: Can be tested by logging in, viewing the Dashboard, and verifying each job card shows a human-readable creator name (not a userId).

**Acceptance Scenarios**:

1. **Given** a user views the Dashboard, **When** they look at a job card, **Then** the card displays the full name of the user who created the job.
2. **Given** the user who created a job has been deleted or is unavailable, **When** the job card is rendered, **Then** it displays a graceful fallback (e.g., "Unknown User") instead of a raw userId or error.

---

### User Story 5 - Enhanced Job Cards Show Creation Date (Priority: P3)

A user views the Dashboard and sees when each job was created, formatted in a human-friendly way (e.g., "Created Mar 5, 2026"). This complements the existing "days posted" information by providing an absolute reference point.

**Why this priority**: Nice-to-have context. The existing "days posted" already conveys recency; an absolute date adds precision but is less critical.

**Independent Test**: Can be tested by creating a job and verifying the Dashboard card shows the correctly formatted creation date.

**Acceptance Scenarios**:

1. **Given** a user views a job card on the Dashboard, **When** the card renders, **Then** the creation date is displayed in a human-friendly format (e.g., "Created Mar 5, 2026").

---

### User Story 6 - Enhanced Job Cards Show Application Completion Status (Priority: P3)

A user views the Dashboard and sees a progress indicator on each job card showing how many applications have been fully processed versus the total number of applications. This gives a quick sense of pipeline progress without clicking into the job.

**Why this priority**: Useful at-a-glance metric, but users can already find this information by clicking into a job. Dashboard convenience, not a blocker.

**Independent Test**: Can be tested by creating a job, uploading applications (some processed, some pending), and verifying the card shows the correct ratio (e.g., "3/5 completed").

**Acceptance Scenarios**:

1. **Given** a user views a job card for a job with 5 total applications and 3 completed, **When** the card renders, **Then** it shows "3 / 5 completed" (or equivalent).
2. **Given** a user views a job card for a job with 0 applications, **When** the card renders, **Then** it shows "0 applications" or omits the completion indicator.

---

### Edge Cases

- What happens when an admin tries to delete a job that was already deleted by another admin (concurrent deletion)?
- What happens if the deletion fails midway (e.g., network error after confirmation)? The UI should show an error message and the job should remain intact.
- What happens when a job has a very large number of applications (e.g., 500+)? The warning should still show the count and deletion should complete without timeout from the user's perspective.
- What happens when the creator user account is deleted or deactivated? The job card should show a fallback name rather than crash or show a raw ID.
- What happens when a job has 0 applications? No cascade warning should be shown—just the simple confirmation.

## Requirements *(mandatory)*

### Functional Requirements

**Delete Jobs**

- **FR-001**: System MUST allow admin users to delete a job from the Dashboard job card.
- **FR-002**: System MUST allow admin users to delete a job from the Job Detail page.
- **FR-003**: System MUST show a confirmation dialog before deleting a job, displaying the job title.
- **FR-004**: System MUST show an additional warning in the confirmation dialog when the job has associated applications, indicating the number of applications that will be permanently deleted.
- **FR-005**: System MUST cascade-delete all associated data (applications, scoring runs, config versions) when a job is deleted.
- **FR-006**: System MUST restrict job deletion to admin users only (reusing the existing AdminOnly policy).
- **FR-007**: System MUST return an appropriate error if a non-admin user attempts to delete a job.
- **FR-008**: System MUST remove the deleted job from the Dashboard view without requiring a full page reload.
- **FR-009**: System MUST redirect the user to the Dashboard after successful deletion from the Job Detail page.
- **FR-010**: System MUST show an error message to the user if deletion fails.

**Enhanced Job Cards**

- **FR-011**: Job cards on the Dashboard MUST display the creation date in a human-friendly format (e.g., "Created Mar 5, 2026").
- **FR-012**: Job cards on the Dashboard MUST display the full name of the user who created the job.
- **FR-013**: Job cards MUST display a graceful fallback (e.g., "Unknown User") when the creator's user record is unavailable.
- **FR-014**: Job cards on the Dashboard MUST display application completion progress (e.g., "3 / 5 completed").
- **FR-015**: Job cards MUST continue to show all existing information: title, organisation, department, days posted, and status badge.

### Key Entities

- **Job**: Central entity. Has Id, Title, Department, Organisation, PostingDate, Status, CreatedBy (userId), CreatedAt. Owns collections of ConfigVersions and Applications (cascade delete).
- **Application**: Belongs to a Job. Represents a candidate application with scoring data. Deleted when parent job is deleted.
- **User**: Referenced by Job.CreatedBy. Has FullName used for display on job cards. May not always be available (deleted users).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Admin users can delete any job (with or without applications) in under 10 seconds, including confirmation.
- **SC-002**: Non-admin users never see delete controls on job cards or job detail pages.
- **SC-003**: After deletion, the job and all associated applications are no longer retrievable through any part of the application.
- **SC-004**: 100% of job cards on the Dashboard display the creator's full name (or "Unknown User" fallback).
- **SC-005**: 100% of job cards on the Dashboard display the formatted creation date.
- **SC-006**: 100% of job cards on the Dashboard display accurate application completion counts.
- **SC-007**: Users can distinguish at a glance between jobs with many completed applications and those still in progress.

## Assumptions

- The existing AdminOnly authorization policy is sufficient for controlling delete access; no new roles or policies are needed.
- Cascade delete behavior is already configured at the data layer for Job → Applications and Job → ConfigVersions.
- The IJobRepository.DeleteAsync method handles the actual deletion including cascades.
- "Completed" application status is determinable from existing application data (a defined status value exists).
- User records are accessible for resolving CreatedBy to a display name; when unavailable, a fallback is acceptable.
- The Dashboard currently renders job cards inline; enhanced card information can be added to the existing rendering approach.
