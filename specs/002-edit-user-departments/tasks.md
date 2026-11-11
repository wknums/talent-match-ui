# Tasks: Edit User with Multi-Department Assignment

**Input**: Design documents from `/specs/002-edit-user-departments/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Not explicitly requested in the feature specification — test tasks are omitted.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

**Scope**: Both stacks.
- **Stack A** (Node.js/Express + React/TypeScript) — implemented ✅
- **Stack B** (.NET Blazor WASM / Clean Architecture) — implemented; parity re-review pending

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: No new projects or dependencies needed. Stack A already has the required infrastructure (Express routing, KV store, shadcn/ui components). This phase verifies readiness.

- [x] T001 Verify existing user CRUD patterns in `server/routes/users.ts` (create/delete routes, `getArray`/`setArray` KV helpers, `requireRole('admin')` middleware, `StoredUser` interface), `src/lib/api-real.ts` (`fetchJSON` helper, `createUser`/`deleteUser` methods), `src/lib/api.ts` (Proxy-based mock/real bridge), and `src/components/UserManagementDialog.tsx` (create form, delete flow, table structure) — confirm all patterns are in place to model the edit feature after

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Backend route and frontend API client methods must exist before any UI story can function.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [x] T002 Add `PUT /:userId` route to `server/routes/users.ts` — use `requireRole('admin')` middleware. Handler: read users via `getArray<StoredUser>(storage, AUTH_USERS)`, find by `req.params.userId`. If not found return 404. Validate request body fields: `fullName` (required, non-empty), `email` (required, valid format), `role` (must be `admin`, `recruiter`, or `business_panel`), `department` (optional string, comma-separated). Check email uniqueness across other users — return 409 on conflict with message `"Email '{email}' is already in use by another user."`. Self-role-change prevention: if `req.user.userId === userId`, silently preserve existing role. Apply validated changes to user object (never modify `passwordHash`, `username`, `createdAt`). Write back via `setArray(storage, AUTH_USERS, users)`. Log via `audit.appendEvent`. Return updated user object (exclude `passwordHash` from response). Return 400 for validation failures.
- [x] T003 [P] Add `updateUser()` method to `src/lib/api-real.ts` — signature: `updateUser(userId: string, fullName: string, email: string, role: string, department: string): Promise<User>`. Implementation: call `` fetchJSON<User>(`${API_BASE}/users/${userId}`, { method: 'PUT', body: JSON.stringify({ fullName, email, role, department }) }) ``. Follow the same pattern as `createUser()` for error handling.
- [x] T004 [P] Add `updateUser()` to proxy layer in `src/lib/api.ts` — the Proxy-based `createApiProxy()` auto-delegates to `realAPI` methods by property name, so adding the method to `api-real.ts` (T003) is sufficient. Verify the proxy resolves `api.updateUser(...)` correctly. If `mockAPI` needs a stub for offline/test mode, add a no-op or echo implementation to the mock API object.

**Checkpoint**: `PUT /api/users/:userId` responds correctly via curl. `api.updateUser(...)` is callable from React components.

---

## Phase 3: User Story 1 — Edit User Profile (Priority: P1) 🎯 MVP

**Goal**: Admins can click Edit on a user row, see a pre-populated form, modify fields (full name, email, role, department), save, and see the table update immediately.

**Independent Test**: Create a user, click Edit, change full name and role, save, confirm table shows updated values. Click Cancel on another edit and confirm no changes persist.

### Implementation for User Story 1

- [x] T005 [US1] Add edit state variables and handlers to `src/components/UserManagementDialog.tsx` — add state: `editingUser: User | null` (tracks which user is being edited), `editForm: { fullName: string, email: string, role: string, department: string }` (form field values), `isEditLoading: boolean` (save loading state). Add `handleEditUser(user: User)` function: set `editingUser` to user, populate `editForm` from user's current values (`fullName`, `email ?? ''`, `role`, `department ?? ''`). Add `handleCancelEdit()`: clear `editingUser` to null. Add `handleSaveEdit()`: set `isEditLoading` true, call `api.updateUser(editingUser.userId, editForm.fullName, editForm.email, editForm.role, editForm.department)`, on success toast "User updated" and call `loadData()` to refresh table then clear `editingUser`, on 400 error toast validation message, on 404 toast "User not found", on 409 toast duplicate email message, finally set `isEditLoading` false.
- [x] T006 [US1] Add Edit button to each user row in the `<TableBody>` section of `src/components/UserManagementDialog.tsx` — render a pencil icon button (use `Pencil` from `lucide-react` or `phosphor-react`, matching existing icon library) in the Actions `<TableCell>` next to the existing delete (Trash) and password reset (Key) buttons. On click call `handleEditUser(user)`. Button should be disabled while `isEditLoading` is true.
- [x] T007 [US1] Add inline edit form to `src/components/UserManagementDialog.tsx` — when `editingUser` is not null, render a form section (above or replacing the create form area) with: Full Name `<Input>`, Email `<Input>`, Role `<Select>` with options admin/recruiter/business_panel, Department `<Input>` (plain text for now — upgraded to tags in US2). Pre-populate all fields from `editForm` state. Include Save `<Button>` (shows loading spinner when `isEditLoading`) and Cancel `<Button>`. Form heading: "Edit User: {username}". Hide the create form toggle while editing.

**Checkpoint**: Admins can edit user profile fields (name, email, role, department as plain text) via inline form. Story is fully functional and independently testable.

---

## Phase 4: User Story 2 — Multi-Department Tag Input (Priority: P2)

**Goal**: The department field in the edit form uses a tag-style input — type + Enter to add, click × to remove. Departments are stored as comma-separated string and round-trip correctly.

**Independent Test**: Edit a user, add "Engineering" and "HR" as department tags, remove "HR", save. Reopen edit form and confirm only "Engineering" appears as a tag.

### Implementation for User Story 2

- [x] T008 [US2] Implement tag-style department input in the edit form in `src/components/UserManagementDialog.tsx` — add state `editDepartments: string[]` derived from `editForm.department` by splitting on commas (filter out empty strings). Replace the department `<Input>` from T007 with: a container rendering each department as a `<Badge>` with an `<X>` icon button that removes that department from the array; an `<Input>` below/inside for typing new department names; `onKeyDown` handler on Enter: trim input, reject if empty/duplicate/contains comma (show toast error for comma: "Department names cannot contain commas"), add to `editDepartments` array, clear input. On save: join `editDepartments` with `,` to set `editForm.department`. Show placeholder text "Type a department and press Enter" when no departments are assigned. Allow saving with zero departments (sends empty string).
- [x] T009 [US2] Update the user table department column display in `src/components/UserManagementDialog.tsx` — if a user's `department` contains commas, render each comma-separated value as an individual `<Badge>` instead of plain text. Single or empty departments continue to display as before (text or "—").

**Checkpoint**: Department editing uses tag-style UX. Comma-separated values round-trip correctly. Table displays multi-department badges. Saving with zero departments is supported.

---

## Phase 5: User Story 3 — Authorization Enforcement (Priority: P3)

**Goal**: Non-admin users cannot see or use the Edit button. Direct API calls without admin privileges return 403.

**Independent Test**: Log in as a non-admin user (Recruiter), verify Edit button is not visible on User Management page. Call `PUT /api/users/{id}` directly without admin role and confirm 403 response.

### Implementation for User Story 3

- [x] T010 [US3] Verify Edit button renders only for admin users in `src/components/UserManagementDialog.tsx` — the `UserManagementDialog` is already admin-gated (mounted conditionally in parent component), and T006 placed the Edit button alongside existing admin-only action buttons. Confirm the Edit button follows the same visibility pattern as delete/reset buttons. If needed, add explicit `currentUser?.role === 'admin'` guard around the Edit button render.
- [x] T011 [US3] Verify `requireRole('admin')` middleware on PUT route in `server/routes/users.ts` — confirm the middleware applied in T002 returns 403 Forbidden for non-admin callers. This is a verification task (no code change expected unless the middleware was omitted in T002).

**Checkpoint**: Authorization is enforced at both UI (hidden controls) and server (403 response) levels.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Edge cases, UX refinements, and end-to-end validation.

- [x] T012 Implement self-role-change UX hint in `src/components/UserManagementDialog.tsx` — when the admin is editing their own user record (detect via `currentUser?.userId === editingUser?.userId` using the current user from auth context/props), disable the Role `<Select>` and display helper text below it: "You cannot change your own role". This complements the backend silent-ignore behavior from T002.
- [x] T013 Validate end-to-end feature — manually test the full edit user flow: (1) log in as admin, (2) edit a user's name/email/role, save, verify table updates, (3) edit department using tag input — add multiple, remove one, save, reopen and verify tags persist, (4) try saving with duplicate email — verify 409 error toast, (5) try saving with empty full name — verify validation error, (6) edit own account — verify role selector is disabled, (7) verify non-admin cannot see Edit button

---

## Phase 7: Stack B Parity Completion

**Purpose**: Finish the missing Stack B web/API surface so the feature regains cross-stack parity.

- [x] T014 Add `PUT /{userId}` to `dotnet/src/Web.Server/Endpoints/UsersEndpoints.cs` and introduce the request DTO needed to dispatch `UpdateUserCommand` through the existing `AdminOnly` route group.
- [x] T015 Implement the edit-user flow in `dotnet/src/Web.Client/Components/UserManagement.razor` — add Edit controls, a pre-populated form/modal, save/cancel handling, and the multi-department tag input expected by the feature spec.
- [x] T016 Add or update Stack B tests and manual verification so endpoint behavior, duplicate-email handling, self-role-change prevention, and multi-department editing are re-validated before parity is marked complete.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — verification only
- **Foundational (Phase 2)**: Depends on Setup — T002 is sequential; T003 and T004 can run in parallel with each other (different files) but depend on T002 for the API contract to exist
- **User Story 1 (Phase 3)**: Depends on Foundational (Phase 2) completion — T005 first, then T006 and T007 sequentially (same file)
- **User Story 2 (Phase 4)**: Depends on User Story 1 (Phase 3) — builds on the edit form created in US1
- **User Story 3 (Phase 5)**: Depends on User Story 1 (Phase 3) — needs the Edit button to exist before verifying visibility
- **Polish (Phase 6)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Depends only on Foundational phase — **MVP target**
- **User Story 2 (P2)**: Depends on US1 (edit form must exist to add tag-style input)
- **User Story 3 (P3)**: Depends on US1 (Edit button must exist to verify conditional rendering)
- **US2 and US3 can run in parallel** once US1 is complete (different concerns, overlapping file but different code sections)

### Within Each User Story

- State management before UI components
- UI structure before save/action wiring
- Core functionality before edge case handling

### Parallel Opportunities

- **Phase 2**: T003 and T004 can run in parallel (different files: `api-real.ts` and `api.ts`)
- **Phase 3**: T005 must be first, then T006 and T007 are sequential (same file)
- **Phase 4–5**: US2 and US3 can start in parallel once US1 is complete

---

## Parallel Example: Foundational Phase

```
# Backend route first (T002 — server/routes/users.ts)
# Then these two can run in parallel (different files):
T003: Add updateUser() to src/lib/api-real.ts
T004: Add updateUser() proxy to src/lib/api.ts
```

## Parallel Example: After User Story 1

```
# Once US1 (Phase 3) is complete, these can start in parallel:
Phase 4 (US2): Tag-style department input (T008, T009)
Phase 5 (US3): Authorization verification (T010, T011)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (verification)
2. Complete Phase 2: Foundational (PUT route + API client methods)
3. Complete Phase 3: User Story 1 (edit state + Edit button + inline form)
4. **STOP and VALIDATE**: Edit user profile works end-to-end via inline form
5. Deploy/demo if ready — basic edit is fully functional

### Incremental Delivery

1. Setup + Foundational → Backend API + frontend client ready
2. Add User Story 1 → Edit profile works → Deploy/Demo (**MVP!**)
3. Add User Story 2 → Tag-style departments → Deploy/Demo
4. Add User Story 3 → Authorization enforcement → Deploy/Demo
5. Polish → Self-role UX hint + end-to-end validation

---

## Notes

- Stack A uses a KV-store pattern — users stored as JSON array under `AUTH_USERS` key. Updates require read-modify-write: `getArray` → find/modify → `setArray`.
- The `StoredUser` interface in `server/routes/users.ts` includes `passwordHash` — the PUT handler must never accept or return the password hash field.
- The `User` type in `src/types/index.ts` has `department?: string` — comma-separated format is already compatible. No type changes needed.
- The `UserManagementDialog` component is only mounted when admin clicks "Manage Users" — the dialog itself is admin-gated, but individual action buttons should still check role for defense-in-depth.
- Stack A roles include `business_panel` in addition to `admin` and `recruiter` — validation must accept all three.
- Existing patterns to follow: create user handler for validation, delete user handler for admin RBAC, password reset handler for read-modify-write KV pattern.
- The `api.ts` proxy layer auto-delegates via `Proxy.get` — adding `updateUser` to `realAPI` is typically sufficient; verify mock path handles gracefully.
- All tasks modify existing files — Stack A and Stack B implementation work are complete; the remaining work is parity checklist re-review against the shipped behavior.
