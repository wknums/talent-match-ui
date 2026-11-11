# Feature Specification: Edit User with Multi-Department Assignment

**Feature Branch**: `002-edit-user-departments`  
**Created**: 2026-03-10  
**Status**: Draft  
**Input**: User description: "Edit User with Multi-Department Assignment"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Edit User Profile (Priority: P1)

An admin opens the User Management page, clicks an "Edit" action on a user row, and sees a form pre-populated with the user's current profile (full name, email, role, and department assignments). The admin modifies one or more fields and saves. The system validates the input, persists the changes, and the user table reflects the updated values immediately.

**Why this priority**: This is the core capability. Without the ability to edit a user, none of the multi-department or other enhancement stories can function. It delivers the highest standalone value by closing the CRUD gap (create exists, delete exists, edit does not).

**Independent Test**: Can be fully tested by creating a user, clicking Edit, changing their full name and role, saving, and confirming the table shows the updated values.

**Acceptance Scenarios**:

1. **Given** an admin is on the User Management page, **When** they click "Edit" on a user row, **Then** an edit form appears pre-populated with that user's current full name, email, role, and department(s).
2. **Given** the edit form is open with modified values, **When** the admin clicks "Save", **Then** the system persists the changes and the user table updates to show the new values.
3. **Given** the edit form is open, **When** the admin clicks "Cancel" or closes the form, **Then** no changes are saved and the user table remains unchanged.
4. **Given** the admin submits the form with invalid data (e.g., blank full name, invalid email format), **When** the form is submitted, **Then** validation errors are displayed inline and the form is not submitted.

---

### User Story 2 - Multi-Department Assignment (Priority: P2)

While editing a user, the admin can assign the user to multiple departments using a user-friendly multi-select or tag-style input. The selected departments are displayed as removable tags. The admin can add departments from a known list and remove existing assignments before saving.

**Why this priority**: Multi-department is the key differentiator of this feature. It builds on the edit form (P1) and enables the primary business need — recruiters working across departments.

**Independent Test**: Can be tested by editing a user, adding two departments, removing one, saving, and confirming the user's department field reflects the correct assignments. Then re-opening the edit form and verifying the saved departments appear as tags.

**Acceptance Scenarios**:

1. **Given** the edit form is open for a user with department "Engineering", **When** the admin views the department field, **Then** "Engineering" is displayed as a removable tag.
2. **Given** the department field shows "Engineering", **When** the admin adds "HR" and "Marketing", **Then** all three appear as tags in the field.
3. **Given** three departments are shown as tags, **When** the admin clicks the remove button on "HR", **Then** "HR" is removed and only "Engineering" and "Marketing" remain.
4. **Given** the admin saves with departments "Engineering" and "Marketing", **When** the user record is retrieved, **Then** the stored department value is "Engineering,Marketing" (comma-separated, no spaces).
5. **Given** a user has comma-separated departments saved, **When** the edit form is re-opened, **Then** each department appears as a separate tag.

---

### User Story 3 - Authorization Enforcement (Priority: P3)

Only users with the Admin role can access the edit user functionality. Non-admin users do not see the Edit action. Attempts to invoke the edit operation without admin privileges are rejected.

**Why this priority**: Authorization is essential for production safety but is a cross-cutting concern that layers onto the edit functionality. The edit form (P1) and multi-department (P2) must exist first.

**Independent Test**: Can be tested by logging in as a non-admin user and verifying the Edit button is not visible, then attempting to call the edit endpoint directly and confirming a 403 response.

**Acceptance Scenarios**:

1. **Given** a user is logged in with the Admin role, **When** they view the User Management page, **Then** an "Edit" action is visible for each user row.
2. **Given** a user is logged in with a non-Admin role (e.g., Recruiter), **When** they view the User Management page, **Then** no "Edit" action is visible.
3. **Given** a non-Admin user crafts a direct request to the edit endpoint, **When** the request is received, **Then** the system returns a 403 Forbidden response.

---

### Edge Cases

- What happens when an admin tries to edit their own account? They should be allowed to edit their own profile fields but should not be able to change their own role (to prevent self-demotion or privilege escalation).
- What happens when the admin removes all departments from a user? The system should allow saving with no departments (empty department field), as not all roles require department assignment.
- What happens if two admins edit the same user simultaneously? The last save wins; no optimistic concurrency is required for this initial implementation.
- What happens when a department name contains a comma? Department names must not contain commas, as commas are the delimiter. Validation should reject department names with commas.
- What happens when the admin changes a user's email to one already in use? The system should reject the change with a clear error message indicating duplicate email.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide an "Edit" action for each user row on the User Management page, visible only to Admin users.
- **FR-002**: System MUST display an edit form pre-populated with the selected user's current full name, email, role, and department assignments.
- **FR-003**: System MUST allow modification of full name, email, role, and department assignments through the edit form.
- **FR-004**: System MUST validate that full name is not empty and email is a valid email format before accepting changes.
- **FR-005**: System MUST validate that email addresses are unique across all users (excluding the user being edited).
- **FR-006**: System MUST support assigning multiple departments to a user via a tag-style or multi-select input.
- **FR-007**: System MUST store multiple department assignments as a comma-separated string in the existing Department field to maintain backward compatibility.
- **FR-008**: System MUST display existing department assignments as individual removable tags when the edit form is opened.
- **FR-009**: System MUST reject department names that contain commas, with a clear validation message.
- **FR-010**: System MUST restrict the edit operation to users with the Admin role, returning a 403 Forbidden for unauthorized requests.
- **FR-011**: System MUST prevent admins from changing their own role through the edit form.
- **FR-012**: System MUST allow saving a user with zero departments (empty department field).
- **FR-013**: System MUST display inline validation errors on the edit form without closing or resetting the form.
- **FR-014**: System MUST update the user table to reflect saved changes immediately after a successful edit, without requiring a full page reload.

### Key Entities

- **User**: Represents a platform user. Key attributes: full name, email, role (Admin, Recruiter, Viewer), department assignments (stored as comma-separated string). A user may have zero or more department assignments.
- **Department**: A label representing an organizational unit (e.g., "Engineering", "HR", "Marketing"). Departments are string values — not a separate entity — stored within the User's department field.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Admins can edit a user's profile and save changes in under 30 seconds for a typical update (changing 1–2 fields).
- **SC-002**: 100% of edit attempts by non-Admin users are blocked at both the UI level (hidden controls) and the server level (403 response).
- **SC-003**: Users with existing single-department assignments can be edited and saved without data loss or format changes.
- **SC-004**: Multi-department assignments round-trip correctly: departments saved as comma-separated values are displayed as individual tags when the edit form is re-opened.
- **SC-005**: All form validation errors are surfaced to the admin before submission, with zero silent failures.

## Assumptions

- The list of available departments is derived from existing department values already in use in the system (or entered freeform by the admin). There is no separate department management feature.
- No audit trail is required for user edits in this initial implementation. Audit logging may be added as a separate feature.
- Optimistic concurrency control is not required. Last-write-wins is acceptable for concurrent edits.
- The comma-separated storage format is sufficient for the current scale of departments (typically under 10 per user).
