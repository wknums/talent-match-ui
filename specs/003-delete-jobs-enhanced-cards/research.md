# Research — 003-delete-jobs-enhanced-cards

## R1: Delete Command Pattern

**Decision**: Mirror `DeleteUserCommand` — single `record` command with collocated handler, injecting
`IJobRepository` and `ICurrentUserService`.
**Rationale**: `DeleteUserCommand` follows this exact pattern: check permissions in handler, delegate
to repository. The `IJobRepository.DeleteAsync(string id)` method already exists and handles the actual
deletion. EF Core cascade deletes handle associated Applications, ConfigVersions, ScoringRuns, etc.
(confirmed: all FK relationships use `DeleteBehavior.Cascade` in the migration).
**Alternatives considered**: Adding a FluentValidation validator — rejected because the only validation
is admin role check, which is better handled in the handler + endpoint auth policy (double protection).

## R2: Cascade Delete Safety

**Decision**: Rely on EF Core cascade delete configuration. No manual deletion of child entities needed.
**Rationale**: The `InitialCreate` migration configures `ReferentialAction.Cascade` on all FK
relationships from Job → Application → ScoringRun/AggregatedResult/Documents/etc. When SQLite (or
Azure SQL) deletes a Job row, all dependent rows are automatically removed. The `IJobRepository.DeleteAsync`
implementation uses EF Core's `Remove` + `SaveChangesAsync`, which triggers the cascades.
**Alternatives considered**: Manual deletion of child entities before parent — rejected as unnecessary
complexity. The database handles this correctly.

## R3: Application Count for Cascade Warning

**Decision**: The `GET /api/jobs` enhanced response will include `TotalApplications` and
`CompletedApplications` counts per job. The delete confirmation dialog on the frontend reads these
counts — no separate API call needed for the cascade warning.
**Rationale**: The spec requires the cascade warning to show the number of applications that will be
deleted (FR-004). Including counts in the job list response means the Dashboard already has this data
when the user clicks delete. This avoids an extra API round-trip.
**Alternatives considered**: Separate `GET /api/jobs/{id}/application-count` endpoint — rejected
because it adds latency to the delete flow and the count data is useful for the enhanced cards anyway
(FR-014).

## R4: Creator Name Resolution

**Decision**: Resolve `CreatedBy` (userId) to `User.FullName` in a new `GetJobSummariesQuery` that
joins the User table. Return `"Unknown User"` when the User record is missing.
**Rationale**: The spec requires FR-012 (creator name) and FR-013 (fallback). The Application layer
can use `IUserRepository` to look up users. A left-join-style lookup (null-coalescing to "Unknown User")
handles deleted users gracefully.
**Alternatives considered**:
1. Denormalize: store `CreatedByName` on the Job entity — rejected because it creates stale data if
   the user's name is updated.
2. Resolve in the endpoint (Presentation layer) — rejected because it leaks data-joining logic out of
   the Application layer, violating Clean Architecture.

## R5: GetJobSummariesQuery vs Modifying GetJobsQuery

**Decision**: Create a new `GetJobSummariesQuery` that returns a `JobSummaryDto` list, rather than
modifying `GetJobsQuery`.
**Rationale**: `GetJobsQuery` returns raw `Job` entities and is used by the `GetJobsQueryHandler` for
RBAC filtering. The new query needs to return a different shape (DTO with joined UserName + aggregated
application counts). Creating a separate query keeps the existing handler clean and follows the CQRS
principle of purpose-specific queries. The new handler reuses the same RBAC logic.
**Alternatives considered**: Modifying `GetJobsQuery` to return DTOs — rejected because it would
change the return type for all consumers and break the handler's current contract.

## R6: Endpoint Authorization for DELETE

**Decision**: Add `DELETE /{jobId}` to the existing jobs endpoint group with `.RequireAuthorization("AdminOnly")`.
**Rationale**: The jobs group currently uses `.RequireAuthorization()` (any authenticated user). The
DELETE route needs admin-only. Chain `.RequireAuthorization("AdminOnly")` on the specific `MapDelete`
call (same pattern as individual routes in `UsersEndpoints.cs`).
**Alternatives considered**: Creating a separate admin-only route group — rejected as over-engineering
for a single route.

## R7: Dashboard CurrentUser Access

**Decision**: Fetch `currentUser` via `Api.GetCurrentUserAsync()` in `Dashboard.razor`'s
`OnInitializedAsync` to check admin role for showing/hiding the delete button.
**Rationale**: The `UserMenu.razor` component already fetches the current user this way. The Dashboard
doesn't currently have the user context but needs it to conditionally render the delete button (FR-006).
**Alternatives considered**: Cascading parameter from `MainLayout` — would require refactoring the
layout, rejected per YAGNI.

## R8: Confirmation Dialog Design

**Decision**: Create a simple `ConfirmDeleteJobDialog.razor` component with parameters for job title
and application count. Renders inline (not a separate page). Constitution requires dialogs to be
resizable and draggable with scroll bars.
**Rationale**: The existing codebase uses inline modal overlays (see `CreateJobDialog` usage in
Dashboard, upload modal in JobDetail). A dedicated component keeps the Dashboard/JobDetail pages clean.
The dialog shows: job title, optional cascade warning with count, Confirm/Cancel buttons.
**Alternatives considered**: Generic reusable confirmation dialog — rejected per YAGNI; this is the only
destructive action in the app.

## R9: Optimistic UI Update on Delete

**Decision**: Remove the job card from the local list immediately on confirmation, then call the API.
If the API call fails, re-insert the job and show an error toast.
**Rationale**: Constitution Principle VI requires responsive UI. Optimistic updates with rollback on
failure is the stated pattern. The Dashboard already has a `jobs` list that's locally mutable.
**Alternatives considered**: Wait for API response before removing — acceptable but less responsive.
Given the destructive nature, optimistic removal with error rollback provides both responsiveness and
safety.

## R10: "Completed" Application Status

**Decision**: Count applications with `Status == "Completed"` as complete for the progress indicator.
**Rationale**: The `Application` entity uses string status with values including `"Queued"`,
`"Completed"`, etc. The `SystemStatsDto` already counts `Completed` as a separate category,
confirming this is the canonical status value.
**Alternatives considered**: Using `FinalDecision != null` as a proxy for completion — rejected
because it conflates different concepts (decision vs processing status).

## R11: Rubric Approval Status Impact on Job Deletion

**Decision**: Deleting a job also permanently removes all associated `JobConfigVersion` records, which
contain the rubric data (`RubricJson`, `MustHaveCriteriaJson`, `DesiredCriteriaJson`) and its
implicit approval status. No special handling is needed for rubric approval state during deletion.
**Rationale**: The rubric approval status is determined by whether `RubricJson` is non-null in the
job's current config version (see `JobDetail.razor` line 168: `hasApprovedRubric = config?.RubricJson != null`).
This is not a separate field — it's an implicit status derived from the presence of rubric data.
When a job is cascade-deleted, all config versions (including rubric data) are removed via
EF Core `DeleteBehavior.Cascade` (FK: Job → JobConfigVersion). The confirmation dialog already
warns about all associated data being deleted. There is no separate "rubric approval" entity or
status field that needs independent tracking or warning.
**Alternatives considered**:
1. Adding a separate rubric approval warning to the delete dialog — rejected because the existing
   cascade warning ("X applications and all associated scoring data") already covers this implicitly.
   The rubric is part of the config version, which is part of "associated data."
2. Preventing deletion of jobs with approved rubrics — rejected because the spec explicitly allows
   admins to delete any job (FR-001, FR-002) without rubric-status preconditions.

## R12: Stack A KV Cascade Delete — Rubric and Config Data

**Decision**: In Stack A, cascade delete must explicitly remove the job's config versions key
(`jobVersionsKey(jobId)`) which contains all rubric data, in addition to the applications key.
**Rationale**: The KV store has no FK cascade mechanism. Each key must be explicitly deleted.
The `jobVersionsKey(jobId)` stores the array of `JobConfigVersion` objects, each containing
`rubric: RubricCategory[]`, `mustHaves`, and `desiredCriteria`. Deleting this key removes
all rubric configuration data, including any implied approval state (a rubric with categories
present is considered "approved" for prompt generation purposes per FR-033 in spec 001).
**Alternatives considered**: Leaving orphaned KV keys — rejected because it would leave stale
data and violate the spec requirement that "all associated data" is removed (FR-005).
