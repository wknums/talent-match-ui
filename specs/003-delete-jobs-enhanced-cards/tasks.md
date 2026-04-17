# Tasks: Delete Jobs and Enhanced Job Cards

**Input**: Design documents from `/specs/003-delete-jobs-enhanced-cards/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Not explicitly requested in feature specification. Omitted unless noted.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: No new project scaffolding needed — feature builds on existing Clean Architecture layout. This phase covers only the new shared DTO and query that multiple user stories depend on.

- [X] T001 Create `JobSummaryDto` record in `dotnet/src/Application/Jobs/Queries/GetJobSummariesQuery.cs` with fields: Id, JobCode, Title, Department, Organisation, PostingDate, Status, CurrentConfigVersionId, JobDescription, CreatedBy, CreatedAt, CreatedByName (string, "Unknown User" fallback), TotalApplications (int), CompletedApplications (int)
- [X] T002 Create `GetJobSummariesQuery` request record and `GetJobSummariesQueryHandler` in `dotnet/src/Application/Jobs/Queries/GetJobSummariesQuery.cs` — inject `IJobRepository` and `IUserRepository`, replicate existing RBAC filtering from `GetJobsQueryHandler`, left-join User table for CreatedByName (null → "Unknown User"), count Application statuses per job for TotalApplications/CompletedApplications (per research R4, R5)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Backend endpoint changes and frontend DTO updates that ALL user stories consume. Must complete before any story-specific UI work.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T003 Update `GET /api/jobs` in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` to dispatch `GetJobSummariesQuery` instead of `GetJobsQuery` and return the `JobSummaryDto[]` response (per contract `contracts/get-jobs.md`)
- [X] T004 [P] Add `JobSummaryDto` record to `dotnet/src/Web.Client/Services/ApiClient.cs` with all fields from the contract (Id, JobCode, Title, Department, Organisation, PostingDate, Status, CurrentConfigVersionId, JobDescription, CreatedBy, CreatedAt, CreatedByName, TotalApplications, CompletedApplications)
- [X] T005 [P] Update `GetJobsAsync` method in `dotnet/src/Web.Client/Services/ApiClient.cs` to deserialize the response as `List<JobSummaryDto>` (or update return type accordingly)
- [X] T006 Add `DeleteJobAsync(string jobId)` method to `dotnet/src/Web.Client/Services/ApiClient.cs` — sends `DELETE /api/jobs/{jobId}`, returns bool success (per contract `contracts/delete-job.md`)

**Checkpoint**: Backend serves enhanced job data; frontend ApiClient can consume it and call delete. UI work can now begin.

---

## Phase 3: User Story 1 — Admin Deletes a Job from Dashboard (Priority: P1) 🎯 MVP

**Goal**: Admin clicks delete on a Dashboard job card → confirmation dialog → job removed without page reload.

**Independent Test**: Create a job (no applications), delete it from the Dashboard, verify the card disappears and the job is no longer listed after refresh.

### Backend — Delete Command

- [X] T007 Create `DeleteJobCommand` record and `DeleteJobCommandHandler` in `dotnet/src/Application/Jobs/Commands/DeleteJobCommand.cs` — handler injects `IJobRepository` and `ICurrentUserService`, checks `IsAdmin` (throw `UnauthorizedAccessException` if not), calls `GetByIdAsync` (return false/throw if null), then calls `DeleteAsync`. Cascade delete handled by EF Core (per research R1, R2)
- [X] T008 Add `DELETE /{jobId}` route to `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` — `.RequireAuthorization("AdminOnly")`, dispatch `DeleteJobCommand`, return 204 on success, 404 if job not found (per contract `contracts/delete-job.md` and research R6)

### Frontend — Confirmation Dialog & Dashboard Integration

- [X] T009 Create `ConfirmDeleteJobDialog.razor` in `dotnet/src/Web.Client/Components/` — parameters: `JobTitle` (string), `ApplicationCount` (int), `Visible` (bool), `OnConfirm` (EventCallback), `OnCancel` (EventCallback). Shows job title, Confirm/Cancel buttons. If `ApplicationCount > 0`, shows cascade warning: "This will also permanently delete {count} applications and all associated scoring data."
- [X] T010 Modify `dotnet/src/Web.Client/Pages/Dashboard.razor` — fetch `currentUser` via `Api.GetCurrentUserAsync()` in `OnInitializedAsync` to determine admin role (per research R7). Update job listing to use `JobSummaryDto`. Add delete button (🗑️) on each job card, visible only when `currentUser.Role == "admin"`. Wire delete button to open `ConfirmDeleteJobDialog` with the selected job's title and `TotalApplications`. On confirm: optimistically remove job from local list, call `Api.DeleteJobAsync(jobId)`, on failure re-insert job and show error toast (per research R9, FR-001, FR-003, FR-008, FR-010)

**Checkpoint**: Admin can delete jobs (with no applications) from the Dashboard. Core delete flow is functional.

---

## Phase 4: User Story 2 — Admin Deletes a Job with Applications (Priority: P1)

**Goal**: When deleting a job that has applications, the confirmation dialog warns with the application count before proceeding.

**Independent Test**: Create a job, upload applications, delete from Dashboard, verify the cascade warning shows the correct count and all applications are removed after confirmation.

- [X] T011 [US2] Verify `ConfirmDeleteJobDialog.razor` cascade warning renders when `ApplicationCount > 0` — this was built in T009 with the `ApplicationCount` parameter. Ensure the warning text matches FR-004: "This will also permanently delete {count} applications and all associated scoring data." No new file changes if T009 was implemented correctly; this task validates the behavior end-to-end (open dialog on a job with applications, confirm warning text, confirm deletion removes all associated data)

**Checkpoint**: Cascade warning displays correctly. Destructive deletion with full data removal works.

---

## Phase 5: User Story 3 — Admin Deletes a Job from Job Detail Page (Priority: P2)

**Goal**: Admin can also delete a job from the Job Detail page, with the same confirmation and cascade warning, then redirect to Dashboard.

**Independent Test**: Navigate to a job detail page, click delete, confirm, verify redirect to Dashboard with job removed.

- [X] T012 [US3] Modify `dotnet/src/Web.Client/Pages/JobDetail.razor` — fetch `currentUser` to check admin role. Add a Delete button visible only for admins. Wire to `ConfirmDeleteJobDialog` (reuse component from T009) with the job's title and application count. On confirm: call `Api.DeleteJobAsync(jobId)`, on success `NavigationManager.NavigateTo("/")` to redirect to Dashboard, on failure show error toast (per FR-002, FR-009, FR-010)

**Checkpoint**: Delete is available from both Dashboard and Job Detail page. Redirect works correctly.

---

## Phase 6: User Story 4 — Enhanced Job Cards Show Creator Name (Priority: P2)

**Goal**: Dashboard job cards display the full name of the person who created each job (or "Unknown User" fallback).

**Independent Test**: Log in, view Dashboard, verify each job card shows a human-readable creator name.

- [X] T013 [US4] Modify job card rendering in `dotnet/src/Web.Client/Pages/Dashboard.razor` — display `job.CreatedByName` on each card (e.g., "Created by {CreatedByName}"). The data is already available from the `JobSummaryDto` returned by T003/T005. Handle the "Unknown User" fallback (already resolved server-side in T002). Ensure all existing card information is preserved (FR-012, FR-013, FR-015)

**Checkpoint**: Creator names visible on all job cards.

---

## Phase 7: User Story 5 — Enhanced Job Cards Show Creation Date (Priority: P3)

**Goal**: Dashboard job cards display the creation date in a human-friendly format.

**Independent Test**: Create a job, verify the Dashboard card shows the correctly formatted creation date.

- [X] T014 [US5] Modify job card rendering in `dotnet/src/Web.Client/Pages/Dashboard.razor` — display `job.CreatedAt` formatted as "Created {date}" in a human-friendly format (e.g., `job.CreatedAt.ToString("MMM d, yyyy")` → "Mar 5, 2026"). The data is already available from `JobSummaryDto` (FR-011)

**Checkpoint**: Creation dates visible on all job cards.

---

## Phase 8: User Story 6 — Enhanced Job Cards Show Application Completion Status (Priority: P3)

**Goal**: Dashboard job cards show a progress indicator (e.g., "3 / 5 completed") for application processing status.

**Independent Test**: Create a job, upload applications (some processed, some pending), verify the card shows the correct ratio.

- [X] T015 [US6] Modify job card rendering in `dotnet/src/Web.Client/Pages/Dashboard.razor` — display application completion progress using `job.CompletedApplications` and `job.TotalApplications`. Show "X / Y completed" when `TotalApplications > 0`, show "0 applications" (or omit) when `TotalApplications == 0` (FR-014, SC-006, SC-007)

**Checkpoint**: Application progress visible on all job cards.

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Final validation and cleanup across all user stories.

- [ ] T016 Run quickstart.md validation — walk through all 7 verification steps in `specs/003-delete-jobs-enhanced-cards/quickstart.md` to confirm end-to-end behavior
- [ ] T017 [P] Verify non-admin authorization: log in as recruiter, confirm no delete buttons visible on Dashboard or Job Detail, confirm `DELETE /api/jobs/{id}` returns 403
- [ ] T018 [P] Verify edge cases: concurrent deletion (second delete returns 404), network error during delete (error toast shown, job re-inserted), job with 0 applications (no cascade warning), deleted creator user ("Unknown User" fallback)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 (T001, T002) — BLOCKS all user stories
- **User Stories (Phases 3–8)**: All depend on Foundational phase completion
  - US1 (Phase 3) and US4 (Phase 6) can proceed in parallel
  - US2 (Phase 4) depends on US1 (reuses dialog from T009)
  - US3 (Phase 5) depends on US1 (reuses dialog from T009)
  - US5 (Phase 7) and US6 (Phase 8) can proceed in parallel, independent of delete stories
- **Polish (Phase 9)**: Depends on all user stories being complete

### User Story Dependencies

- **US1 (P1)**: After Foundational → no story dependencies (creates the delete command + dialog)
- **US2 (P1)**: After US1 → validates cascade warning behavior built in US1
- **US3 (P2)**: After US1 → reuses `ConfirmDeleteJobDialog` component
- **US4 (P2)**: After Foundational → no story dependencies (display-only change)
- **US5 (P3)**: After Foundational → no story dependencies (display-only change)
- **US6 (P3)**: After Foundational → no story dependencies (display-only change)

### Within Each User Story

- Backend before frontend
- Models/DTOs before handlers/services
- Handlers before endpoints
- Endpoints before UI components

### Parallel Opportunities

- T004 and T005 can run in parallel (both modify ApiClient but different sections)
- T007 and T009 can run in parallel (backend command vs. frontend dialog — different files)
- US4, US5, US6 (Phases 6-8) are all display-only changes to Dashboard.razor — can be combined into a single editing pass if done sequentially, or parallelized if modifying distinct card sections
- T016, T017, T018 can all run in parallel (independent validation tasks)

---

## Parallel Example: After Foundational Phase

```text
Worker A (Delete Flow):     T007 → T008 → T010 → T011 → T012
Worker B (Enhanced Cards):  T013 → T014 → T015
```

Both workers start after Phase 2 completes. Worker A handles all delete-related tasks (US1 → US2 → US3). Worker B handles all card enhancement tasks (US4 → US5 → US6). They converge at Phase 9 for polish.

---

## Implementation Strategy

- **MVP (Ship first)**: Phase 1 + Phase 2 + Phase 3 (US1) — admin can delete jobs from Dashboard
- **Increment 2**: Phase 4 (US2) — cascade warning for jobs with applications
- **Increment 3**: Phase 5 (US3) — delete from Job Detail page
- **Increment 4**: Phase 6 + 7 + 8 (US4–US6) — enhanced card display (can ship independently)
- **Final**: Phase 9 — validation and edge case verification

**Total Stack B tasks**: 18

---
---

# Stack A Tasks: Delete Jobs and Enhanced Job Cards

**Stack**: Node.js/Express + React/TypeScript  
**Prerequisites**: Stack B tasks completed ✅ — same spec, same contracts  
**Organization**: Mirrors Stack B phase structure. Tasks are prefixed with `A` to distinguish from Stack B tasks.

## Format: `[ID] [P?] [Story] Description`

---

## Phase A1: Backend — Enhance GET /api/jobs + Add DELETE Route

**Purpose**: Modify the existing GET handler in `server/routes/jobs.ts` to include `createdByName` in the response, and add a new DELETE endpoint for admin job deletion.

- [ ] AT001 Enhance `GET /api/jobs` handler in `server/routes/jobs.ts` — after computing `jobsWithStats`, resolve `createdBy` to a full name: read users array from `getArray<StoredUser>(storage, AUTH_USERS)` (import `AUTH_USERS` from kv-keys), build `Map<string, string>` of `username → fullName`. For each job, add `createdByName: userMap.get(job.createdBy) || 'Unknown User'` to the response object. The `StoredUser` interface already exists in `users.ts` — either export it or define a minimal pick type locally.
- [ ] AT002 Add `DELETE /:jobId` route to `server/routes/jobs.ts` — use `requireRole('admin')` middleware. Handler: read jobs array from `getArray<Job>(storage, JOBS)`, find job by `jobId` param. If not found, return 404. Remove job from array, write back via `setArray`. Cascade: delete associated KV keys — `await storage.delete(jobApplicationsKey(jobId))` and `await storage.delete(jobVersionsKey(jobId))`. Audit log via `audit.appendEvent`. Return `{ success: true }`.

**Checkpoint**: `GET /api/jobs` now includes `createdByName` per job. `DELETE /api/jobs/:jobId` removes a job and its associated data.

---

## Phase A2: Frontend — API Client + Types

**Purpose**: Add delete API method, proxy bridge, and update the Job type to include `createdByName`.

- [ ] AT003 [P] Add optional `createdByName?: string` field to the `Job` interface in `src/types/index.ts` — this keeps backward compatibility with existing code that doesn't use the field.
- [ ] AT004 [P] Add `deleteJob()` method to `src/lib/api-real.ts` — signature: `deleteJob(jobId: string): Promise<void>`. Call `fetchJSON(\`\${API_BASE}/jobs/\${jobId}\`, { method: 'DELETE' })`.
- [ ] AT005 [P] Add `deleteJob()` to the proxy layer in `src/lib/api.ts` — delegate to `realAPI.deleteJob(...)` when in real mode.

**Checkpoint**: `api.deleteJob(...)` is callable from React components. `Job.createdByName` is available from the API.

---

## Phase A3: User Story 1 — Admin Deletes a Job from Dashboard (P1) 🎯 MVP

**Goal**: Admin clicks delete on a Dashboard job card → confirmation → job removed without page reload.

- [ ] AT006 [US1] Add `onDelete` prop to `JobCard` component in `src/components/JobCard.tsx` — optional callback `onDelete?: (jobId: string) => void`. Render a delete button (Trash icon from @phosphor-icons/react) in the card header area, next to the status badge. Button visible only when `onDelete` is provided. On click, call `e.stopPropagation()` then `onDelete(job.jobId)`. Style: destructive variant, small size.
- [ ] AT007 [US1] Add delete confirmation and handler to `src/components/DashboardView.tsx` — add state: `deletingJob: Job | null`, `showDeleteConfirm: boolean`. Add `handleDeleteClick(job)` to set the state and show an `AlertDialog` (or `Dialog`) confirmation. Show job title and, if `job.stats.totalApplications > 0`, a cascade warning with application count. On confirm: optimistically remove job from `jobs` state, call `api.deleteJob(jobId)`, on failure re-insert and show error toast. On cancel: clear state. Pass `onDelete={handleDeleteClick}` to each `<JobCard>` only when `currentUser?.role === 'admin'`.

**Checkpoint**: Admin can delete jobs from the Dashboard. Confirmation with cascade warning works.

---

## Phase A4: User Story 3 — Admin Deletes a Job from Job Detail Page (P2)

**Goal**: Admin can delete a job from the Job Detail page, with confirmation and redirect.

- [ ] AT008 [US3] Add delete button and confirmation to `src/components/JobDetailView.tsx` — add a delete button (Trash icon) in the header action buttons area, visible only when the current user is admin. Add `onDelete` prop to `JobDetailViewProps` or handle inline. Show confirmation dialog (same pattern as Dashboard). On confirm: call `api.deleteJob(jobId)`, on success call `onBack()` to navigate back to Dashboard, on failure show error toast.

**Checkpoint**: Delete from Job Detail page works with confirmation and redirect.

---

## Phase A5: User Story 4 — Enhanced Job Cards Show Creator Name (P2)

**Goal**: Dashboard job cards display the creator's full name.

- [ ] AT009 [US4] Add creator name display to `src/components/JobCard.tsx` — below the existing department/organization line, add `{job.createdByName && <p className="text-xs text-muted-foreground">Created by {job.createdByName}</p>}`. The data comes from the enhanced GET response (AT001).

**Checkpoint**: Creator names visible on all job cards.

---

## Phase A6: User Story 5 — Enhanced Job Cards Show Creation Date (P3)

**Goal**: Dashboard job cards display the creation date in a human-friendly format.

- [ ] AT010 [US5] Add creation date display to `src/components/JobCard.tsx` — add formatted creation date alongside or replacing the existing "days open" line. Format: `Created ${new Date(job.createdAt).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}` → "Created Mar 5, 2026". Display alongside existing "X days open" text.

**Checkpoint**: Creation dates visible on all job cards.

---

## Phase A7: User Story 6 — Enhanced Job Cards Show Completion Status (P3)

**Goal**: Job cards show application completion progress ("3 / 5 completed").

- [ ] AT011 [US6] Add completion progress display to `src/components/JobCard.tsx` — the card already shows completed/total stats in the grid. Add a concise summary line above or below the progress bar: when `stats.totalApplications > 0`, show `{stats.completed} / {stats.totalApplications} completed`. When 0 applications, the existing "No applications uploaded yet" message suffices.

**Checkpoint**: Application progress is explicit on all job cards.

---

## Phase A8: Polish & Verification

- [ ] AT012 Verify non-admin authorization: confirm delete buttons are not rendered for non-admin users on both Dashboard and Job Detail page. Confirm `DELETE /api/jobs/:jobId` returns 403 for non-admin callers.
- [ ] AT013 Verify edge cases: concurrent deletion (second delete returns 404), deleted creator user shows "Unknown User" fallback, job with 0 applications shows no cascade warning.

---

## Stack A Dependencies & Execution Order

### Phase Dependencies

- **Phase A1**: No dependencies — backend changes can start immediately
- **Phase A2**: Depends on Phase A1 (API contract must exist) — AT003, AT004, AT005 can run in parallel
- **Phase A3**: Depends on Phase A2 — AT006 and AT007 are sequential (different files, but AT007 uses AT006's `onDelete` prop)
- **Phase A4**: Depends on Phase A3 (reuses confirmation pattern)
- **Phases A5–A7**: Depend only on Phase A2 (need `createdByName` in the Job type) — can run in parallel with A3/A4

### Parallel Opportunities

- AT003, AT004, AT005 can all run in parallel (different files)
- Phases A5, A6, A7 (enhanced card display) can run in parallel with Phases A3, A4 (delete flow)
- AT012, AT013 can run in parallel (independent verification)

```text
Worker A (Delete Flow):     AT006 → AT007 → AT008
Worker B (Enhanced Cards):  AT009 → AT010 → AT011
```

Both workers start after Phase A2 completes.

---

## Stack A Notes

- Stack A's `GET /api/jobs` already computes stats inline by reading each job's applications from KV. The enhancement adds a user lookup step — this is an additional KV read (`AUTH_USERS`) per request, which is acceptable for the expected scale.
- The `createdBy` field on `Job` stores the username (e.g., `req.user?.username`), not a userId. The user lookup must match on `username`, not `userId`.
- The KV store `storage.delete(key)` method should be used for cascade — check if it exists on the `StorageProvider` interface. If not, write an empty array via `setArray` as an alternative.
- `JobCard.tsx` is a pure presentational component — it receives `job` and optional callbacks. The delete confirmation logic belongs in `DashboardView.tsx` (the parent), not in `JobCard`.
- `JobDetailView.tsx` already fetches the job and has its own action buttons area — the delete button fits naturally alongside the existing Edit and Upload buttons.
- The `DashboardView` receives `currentUser` as a prop from `App.tsx` — admin role checks use `currentUser?.role === 'admin'`.

**Total Stack A tasks**: 13
