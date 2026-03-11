# Tasks: Edit Job UI for Stack B

**Input**: Design documents from `/specs/004-edit-job-stack-b/`
**Prerequisites**: spec.md (user stories with priorities)
**Tests**: Not explicitly requested — test tasks omitted.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Blazor client**: `dotnet/src/Web.Client/` (Pages/, Components/, Services/)
- **Backend**: `dotnet/src/Web.Server/Endpoints/` (no changes needed per FR-012)
- **Stack A reference**: `src/components/` (React — read-only reference for parity)

## Key Existing Files

| File | Role |
|------|------|
| `dotnet/src/Web.Client/Pages/JobDetail.razor` | Job Detail page — needs edit button added to action bar |
| `dotnet/src/Web.Client/Components/CreateJobDialog.razor` | Create Job dialog (~480 lines) — needs edit mode support |
| `dotnet/src/Web.Client/Services/ApiClient.cs` | API client — `UpdateJobConfigAsync` (returns `Task<bool>`), `GetJobConfigAsync` (returns `Task<JobConfigDto?>`), `ApiException` class, `EnsureSuccessOrThrowAsync` helper |
| `src/components/CreateJobDialog.tsx` | Stack A reference — has working edit mode via `editingJob` prop (lines 25, 59–74, 342–358) |
| `src/components/JobDetailView.tsx` | Stack A reference — has edit button via `onEditJob` callback (line 176) |

## API Constraints

The existing `PUT /api/jobs/{jobId}/config` endpoint (in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` line 176) accepts `UpdateJobConfigRequest` mapped to `UpdateJobConfigCommand`:
- `RubricJson`, `MustHaveCriteriaJson`, `DesiredCriteriaJson` (string?, JSON-serialized)
- `ScoringRunCount` (int), `AggregationStrategy` (string)
- `LonglistThreshold`, `ShortlistThreshold`, `VarianceThreshold` (double)

`UpdateConfigDto` (client-side) and `JobConfigDto` mirror these fields.

`JobDto`: `Id`, `JobCode`, `Title`, `Department`, `Organisation`, `PostingDate`, `Status`, `CurrentConfigVersionId`, `JobDescription`, `CreatedBy`, `CreatedAt`.

Job metadata fields (title, jobCode, department, organisation, postingDate, jobDescription) are loaded from `JobDto` via `GET /api/jobs/{jobId}` for display context but are **not updateable** via existing endpoints (FR-012 prohibits backend changes). The edit dialog displays these fields as read-only context while config fields are editable.

**Note**: Stack A's React `api-real.ts` sends all fields (including metadata) to `PUT /api/jobs/{jobId}/config`, but the backend `UpdateJobConfigCommand` silently ignores metadata fields — only config fields are persisted. The Blazor implementation should only send config fields to keep the contract honest.

## Existing Dialog State (CreateJobDialog.razor @code section)

- **Parameters**: `Visible` (bool), `OnCreated` (EventCallback), `OnCancel` (EventCallback)
- **Form fields**: `title`, `department`, `organisation`, `jobDescription` (string), `postingDate` (DateTime), `scoringRunCount` (int), `aggregationStrategy` (string), `longlistThreshold`, `shortlistThreshold`, `varianceThreshold` (double)
- **Collections**: `rubricCategories` (List\<RubricCategoryInput\>), `mustHaveCriteria` (List\<string\>), `desiredCriteria` (List\<DesiredCriterionInput\>)
- **Records**: `RubricCategoryInput(Name, Weight, Description)`, `DesiredCriterionInput(Qualification, Description)`
- **Submit**: Serializes fields → calls `Api.CreateJobAsync()` → sets `success`/`message`
- **Tabs**: `Tab.Upload` (default), `Tab.Manual` — upload tab handles document extraction, manual entry has form fields

---

## Phase 1: Setup

**Purpose**: No new project setup needed — this feature modifies existing Blazor components. Verify existing infrastructure.

- [X] T001 Verify existing API infrastructure by confirming `UpdateJobConfigAsync` in `dotnet/src/Web.Client/Services/ApiClient.cs` accepts `(string jobId, UpdateConfigDto config)` and returns `Task<bool>`, `GetJobConfigAsync` returns `Task<JobConfigDto?>`, and `PUT /api/jobs/{jobId}/config` endpoint exists in `dotnet/src/Web.Server/Endpoints/JobsEndpoints.cs` (line 176)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Store job configuration data in JobDetail page state so it can be passed to the edit dialog.

**⚠️ CRITICAL**: The edit dialog needs both `JobDto` (metadata) and `JobConfigDto` (config) to pre-populate fields. `JobDetail.razor` currently calls `Api.GetJobConfigAsync(JobId)` in `LoadPromptStatus()` but only uses the result to check `hasApprovedRubric` — the `config` local variable is discarded. It must be stored as component state.

- [X] T002 Add `JobConfigDto? jobConfig` state field to `JobDetail.razor` `@code` section in `dotnet/src/Web.Client/Pages/JobDetail.razor` (alongside existing state fields like `showDeleteDialog`), and in `LoadPromptStatus()` change `var config = await Api.GetJobConfigAsync(JobId);` to `jobConfig = await Api.GetJobConfigAsync(JobId);` then use `jobConfig` instead of the local `config` variable for the `hasApprovedRubric` check

**Checkpoint**: Foundation ready — `JobDetail.razor` now holds both `job` (JobDto) and `jobConfig` (JobConfigDto) in state, enabling edit dialog pre-population.

---

## Phase 3: User Story 1 + User Story 2 — Edit an Existing Job's Configuration / Pre-populated Fields (Priority: P1) 🎯 MVP

**Goal**: Add an edit button to the Job Detail page that opens the existing Create Job dialog in edit mode, with all configuration fields pre-populated from the current job config. On save, call `PUT /api/jobs/{jobId}/config` and refresh the view.

**Why combined**: US1 (edit flow) and US2 (pre-population accuracy) are inseparable — you cannot test editing without pre-population, and pre-population is meaningless without the edit flow. Both are P1.

**Independent Test**: Navigate to any existing job → click edit button → verify all config fields are pre-populated with current values → modify a field (e.g., shortlistThreshold) → save → confirm the Job Detail view reflects the updated value → reopen the edit dialog → verify the modified value persists.

### Implementation for User Story 1 + 2

- [X] T003 [P] [US1] Add edit button to the Job Detail page header in `dotnet/src/Web.Client/Pages/JobDetail.razor`: add a `private bool showEditDialog;` state field in the `@code` section (alongside `showDeleteDialog`), and add a pencil/edit button in the action buttons area (after the "Manage Prompts" button) styled consistently with sibling buttons (e.g., `background: #2563eb; color: white; border: none; border-radius: 4px; padding: 0.5rem 1rem; cursor: pointer;`), with `@onclick="() => showEditDialog = true"` and text "✏️ Edit Job". Reference Stack A pattern at `src/components/JobDetailView.tsx` line 176.

- [X] T004 [P] [US1] Add edit mode parameters to `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add three new `[Parameter]` properties alongside existing ones (`Visible`, `OnCreated`, `OnCancel`): `[Parameter] public JobDto? EditingJob { get; set; }`, `[Parameter] public JobConfigDto? EditingConfig { get; set; }`, `[Parameter] public EventCallback OnUpdated { get; set; }`. Add a computed property `private bool IsEditMode => EditingJob != null;`.

- [X] T005 [US1] Add field pre-population logic to `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: implement `protected override void OnParametersSet()` that, when `IsEditMode && EditingConfig != null` and a `private bool _populated` guard is false, populates form fields: deserialize `EditingConfig.RubricJson` into `rubricCategories` (as `List<RubricCategoryInput>` with Name/Weight/Description using `System.Text.Json.JsonSerializer`), `EditingConfig.MustHaveCriteriaJson` into `mustHaveCriteria` (as `List<string>`), `EditingConfig.DesiredCriteriaJson` into `desiredCriteria` (as `List<DesiredCriterionInput>` with Qualification/Description); set `scoringRunCount = EditingConfig.ScoringRunCount`, `aggregationStrategy = EditingConfig.AggregationStrategy`, `longlistThreshold = EditingConfig.LonglistThreshold`, `shortlistThreshold = EditingConfig.ShortlistThreshold`, `varianceThreshold = EditingConfig.VarianceThreshold`; populate read-only metadata from `EditingJob` (`title = EditingJob.Title`, `department = EditingJob.Department`, `organisation = EditingJob.Organisation`, `postingDate = EditingJob.PostingDate`, `jobDescription = EditingJob.JobDescription ?? ""`); set `_populated = true` and `activeTab = Tab.Manual`. Handle null JSON strings gracefully (initialize empty lists). Reference Stack A pattern at `src/components/CreateJobDialog.tsx` lines 59–74.

- [X] T006 [US1] Update dialog title and submit logic in `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: change the dialog `<h4>` title from `"Create New Job"` to `@(IsEditMode ? "Edit Job Configuration" : "Create New Job")`. Change the submit button text from `"Create Job"` to `@(IsEditMode ? "Update Configuration" : "Create Job")`. In the `Submit()` method, add an `if (IsEditMode)` branch before the existing create logic that: serializes rubric/criteria JSON using the same serialization pattern as the create path, calls `var result = await Api.UpdateJobConfigAsync(EditingJob!.Id, new UpdateConfigDto(rubricJson, mustHaveJson, desiredJson, scoringRunCount, aggregationStrategy, longlistThreshold, shortlistThreshold, varianceThreshold))`, sets `success = result` and `message = result ? "Configuration updated!" : "Failed to update."`, and on success invokes `await OnUpdated.InvokeAsync()`. Keep the existing create branch in an `else` clause.

- [X] T007 [US1] Add job metadata display section to edit mode in `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: when `IsEditMode`, render a read-only info panel at the top of the manual-entry form area (before the editable config fields) showing job metadata from `EditingJob` — Title, JobCode, Department, Organisation, PostingDate (formatted as date string), JobDescription — in a styled container (`background: #f3f4f6; padding: 1rem; border-radius: 6px; margin-bottom: 1rem; border: 1px solid #e5e7eb;`) with labels in bold and values beside them. Hide the metadata input fields (title, department, organisation, postingDate, jobDescription inputs) when `IsEditMode` since they are not updateable via existing endpoints. Hide the Upload Spec tab buttons when `IsEditMode` since document extraction is only for new jobs, and force `activeTab = Tab.Manual`.

- [X] T008 [US1] Wire up the edit dialog in `dotnet/src/Web.Client/Pages/JobDetail.razor`: add a conditional block after the existing `ConfirmDeleteJobDialog` usage that renders `<CreateJobDialog>` when `showEditDialog` is true, passing `Visible="true"`, `EditingJob="job"`, `EditingConfig="jobConfig"`, `OnUpdated="OnEditCompleted"`, `OnCancel="() => showEditDialog = false"`. Implement `private async Task OnEditCompleted()` method in the `@code` section that sets `showEditDialog = false`, re-fetches `job = await Api.GetJobAsync(JobId)`, calls `await LoadPromptStatus()` (which now stores `jobConfig` per T002), and calls `StateHasChanged()` to refresh the view with updated values per FR-007.

- [X] T009 [US2] Add form field reset logic for dialog reuse in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add a `private void ResetForm()` method that resets all form fields to their defaults (`title = ""`, `department = ""`, `organisation = ""`, `jobDescription = ""`, `postingDate = DateTime.Today`, `scoringRunCount = 3`, `aggregationStrategy = "median"`, `longlistThreshold = 70`, `shortlistThreshold = 85`, `varianceThreshold = 15`, clear `rubricCategories`, `mustHaveCriteria`, `desiredCriteria` lists, set `_populated = false`, `message = null`, `success = false`, `activeTab = Tab.Upload`). Call `ResetForm()` from the cancel/backdrop-click handler and after successful submit (both create and edit paths) to ensure the next dialog open starts clean and pre-population re-triggers correctly when reopened.

**Checkpoint**: At this point, users can click the edit button on any Job Detail page, see a dialog pre-populated with current config values, modify fields, save, and see updated values reflected — fully functional MVP.

---

## Phase 4: User Story 3 — Validation Feedback Before Saving (Priority: P2)

**Goal**: Prevent saving invalid data by validating required fields and rubric weight totals, displaying clear inline error messages per FR-005 and FR-006.

**Independent Test**: Open the edit dialog → set rubric weights to sum to 0.5 → click Save → verify weight validation error appears and save is blocked. Correct the weights to sum to 1.0 → verify message clears and save succeeds. In create mode: clear the title field → click Save → verify title validation error appears.

### Implementation for User Story 3

- [X] T010 [US3] Add validation state and logic to `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add a `private Dictionary<string, string> validationErrors = new();` field and a `private bool Validate()` method that: clears `validationErrors`, then checks (1) in create mode only (`!IsEditMode`): `title` is not empty/whitespace → error key `"title"` with message `"Title is required"`, (2) `rubricCategories` has at least one entry with non-empty Name and Weight > 0 → error key `"rubric"` with message `"At least one rubric category with a name and weight is required"`, (3) if `rubricCategories` has entries: weight sum equals 1.0 within ±0.01 tolerance → error key `"rubricWeights"` with message `$"Rubric weights must sum to 1.0 (currently {weightSum:F2})"`. Returns `true` if `validationErrors` is empty. Call `Validate()` at the start of `Submit()` and return early if it returns false. Note: title validation only applies in create mode since title is read-only in edit mode.

- [X] T011 [US3] Add inline validation message display to `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: below the title input field, conditionally render `@if (validationErrors.ContainsKey("title")) { <span style="color: #dc2626; font-size: 0.85rem;">@validationErrors["title"]</span> }`. Below the rubric categories section (near the existing weight sum display), add similar `<span>` elements for `"rubric"` and `"rubricWeights"` error keys styled consistently (`color: #dc2626; font-size: 0.85rem;`). The existing weight sum display already shows the total — enhance it to change color to red and show validation error text when `"rubricWeights"` is present.

- [X] T012 [US3] Add real-time validation clearing in `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: when the title input value changes, remove the `"title"` key from `validationErrors` if present. When rubric weight inputs change, remove `"rubricWeights"` key from `validationErrors`. When rubric categories are added or removed, remove `"rubric"` key from `validationErrors`. This ensures validation messages disappear as the user corrects errors without requiring another save attempt (per FR-006, acceptance scenario 3).

**Checkpoint**: At this point, validation prevents saving invalid data with clear inline feedback. Combined with Phase 3, the edit flow is fully validated.

---

## Phase 5: User Story 4 — Error Handling on Save Failure (Priority: P3)

**Goal**: Gracefully handle API errors during save — display a user-friendly error message, keep the dialog open with edits preserved, and allow retry per FR-008.

**Independent Test**: Open the edit dialog → modify a field → simulate a save failure (e.g., disconnect network or return error from API) → verify error message appears → verify dialog stays open with edits intact → restore connectivity → click Save again → verify success.

### Implementation for User Story 4

- [X] T013 [US4] Update `UpdateJobConfigAsync` to throw `ApiException` on failure in `dotnet/src/Web.Client/Services/ApiClient.cs`: change the method to use the `EnsureSuccessOrThrowAsync` pattern already used by other methods (e.g., `CreateUserAsync`, `DeleteUserAsync`) — replace `return response.IsSuccessStatusCode;` with `await EnsureSuccessOrThrowAsync(response, "Failed to update job configuration.");` and change return type from `Task<bool>` to `Task` (void — success is implied by no exception). Update the call site in `CreateJobDialog.razor` `Submit()` method to remove the `var result =` / `success = result` pattern and instead just `await` the call directly.

- [X] T014 [US4] Add error handling to edit mode submit in `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: wrap the `UpdateJobConfigAsync` call (from T006) in a `try { ... } catch (ApiException ex) { message = $"Failed to save: {ex.Message}"; success = false; } catch (Exception) { message = "An unexpected error occurred. Please try again."; success = false; }`. On success (no exception): set `success = true`, `message = "Configuration updated!"`, invoke `await OnUpdated.InvokeAsync()`, call `ResetForm()`. On error: do NOT close the dialog, do NOT reset form fields — the user's edits must be preserved for retry per FR-008 acceptance scenarios 1-3. Add a `private bool isSaving;` flag, set to `true` before the API call and `false` in a `finally` block; disable the Save button with `disabled="@isSaving"` to prevent double-submits during the async operation.

- [X] T015 [US4] Style the error message display in `CreateJobDialog.razor` in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: update the existing message display area (currently a simple `<p>` with green/red color) to use a more prominent error banner: when `!success && message != null`, render in a `<div style="background: #fef2f2; border: 1px solid #fca5a5; color: #991b1b; padding: 0.75rem 1rem; border-radius: 6px; display: flex; justify-content: space-between; align-items: center; margin: 0.5rem 0;">` with the message text and a dismiss `<button @onclick="() => message = null" style="background: none; border: none; color: #991b1b; cursor: pointer; font-size: 1.1rem;">✕</button>`. Keep the existing green success styling as-is for success messages.

**Checkpoint**: All user stories are now complete. The edit flow handles the happy path (US1+US2), validates input (US3), and gracefully handles failures (US4).

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Edge cases, accessibility, and improvements that affect multiple user stories.

- [X] T016 [P] Handle scrolling for large content in `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: verify the existing `.dialog-body` CSS with `overflow-y: auto` handles many rubric categories and long job descriptions gracefully in edit mode. If scrolling is clipped by the read-only metadata panel (added in T007), adjust layout (e.g., ensure the metadata panel is outside the scrollable area, or add `max-height` constraint). Test with 10+ rubric categories per spec edge case.

- [X] T017 [P] Verify concurrent edit behavior in `dotnet/src/Web.Client/Pages/JobDetail.razor`: confirm that `OnEditCompleted()` (added in T008) properly refetches both `job` and `jobConfig` so reopening the edit dialog after a save always shows the latest server state (last-write-wins per spec edge cases). Verify that the `_populated` flag reset (in `ResetForm()` from T009) allows re-population with fresh data. No code changes expected — this is a verification task.

- [X] T018 [P] Add accessibility attributes to edit button and dialog in `dotnet/src/Web.Client/Pages/JobDetail.razor` and `dotnet/src/Web.Client/Components/CreateJobDialog.razor`: add `aria-label="Edit job configuration"` to the edit button (added in T003), add `role="dialog"` and `aria-modal="true"` to the `.dialog-window` div, and add `aria-label="Close dialog"` to the close button.

- [X] T019 Verify feature parity with Stack A by comparing the edit dialog behavior in `dotnet/src/Web.Client/Components/CreateJobDialog.razor` against the reference implementation in `src/components/CreateJobDialog.tsx`: confirm all config fields from `UpdateConfigDto` are editable, pre-population matches Stack A pattern (lines 59–74), save calls the correct endpoint, dialog title changes in edit mode, cancel discards changes (FR-009), and the dialog open/close lifecycle matches including form reset.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — verification only
- **Foundational (Phase 2)**: Depends on Phase 1 — stores config in JobDetail state
- **US1+US2 (Phase 3)**: Depends on Phase 2 — needs `jobConfig` data available in JobDetail
- **US3 (Phase 4)**: Depends on Phase 3 — validation applies to the edit submit flow built in Phase 3
- **US4 (Phase 5)**: Depends on Phase 3 — error handling applies to the save flow built in Phase 3; independent of Phase 4
- **Polish (Phase 6)**: Depends on Phase 3 minimum; ideally after Phase 4+5

### User Story Dependencies

- **US1+US2 (P1)**: Can start after Foundational (Phase 2) — core edit flow and pre-population
- **US3 (P2)**: Can start after US1+US2 — adds validation to the existing edit submit flow
- **US4 (P3)**: Can start after US1+US2 — adds error handling to the existing edit submit flow
- **US3 and US4 are independent of each other** and can be worked in parallel

### Within Each User Story

- Parameters/state additions before logic that uses them
- Pre-population before submit handling
- Core flow before edge cases

### Parallel Opportunities

- **Phase 3**: T003 (edit button in JobDetail.razor) and T004 (parameters in CreateJobDialog.razor) can run in parallel — different files
- **Phase 4 + Phase 5**: US3 (validation) and US4 (error handling) can proceed in parallel after Phase 3
- **Phase 6**: T016, T017, T018 can all run in parallel — independent concerns in different files/aspects

---

## Parallel Example: Phase 3 (US1+US2)

```text
# Parallel group 1 (different files):
T003: Add edit button to JobDetail.razor
T004: Add edit mode parameters to CreateJobDialog.razor

# Sequential after group 1:
T005: Pre-population logic (depends on T004 parameters)
T006: Submit logic update (depends on T004 parameters, T005 pre-population)
T007: Metadata display section (depends on T004 IsEditMode property)
T008: Wire up dialog in JobDetail (depends on T003 showEditDialog, T004 parameters, T006 submit)
T009: Reset logic (depends on T005 _populated flag, T006 submit flow)
```

---

## Implementation Strategy

### MVP First (User Stories 1+2 Only)

1. Complete Phase 1: Verify existing API (T001)
2. Complete Phase 2: Store config in JobDetail state (T002)
3. Complete Phase 3: Edit flow with pre-population (T003–T009)
4. **STOP and VALIDATE**: Test edit flow independently — open dialog, verify pre-population, modify a field, save, confirm persistence
5. Deploy/demo if ready — this delivers the core editing capability

### Incremental Delivery

1. Phase 1+2 → Foundation ready
2. Phase 3 (US1+US2) → Core edit with pre-population → **MVP!**
3. Phase 4 (US3) → Adds input validation → Deploy
4. Phase 5 (US4) → Adds error resilience → Deploy
5. Phase 6 → Polish and accessibility → Deploy

### Key Files Modified

| File | Tasks |
|------|-------|
| `dotnet/src/Web.Client/Pages/JobDetail.razor` | T002, T003, T008, T017, T018 |
| `dotnet/src/Web.Client/Components/CreateJobDialog.razor` | T004, T005, T006, T007, T009, T010, T011, T012, T014, T015, T016, T018 |
| `dotnet/src/Web.Client/Services/ApiClient.cs` | T013 |

---

## Notes

- [P] tasks = different files, no dependencies on incomplete tasks
- [Story] label maps task to specific user story for traceability
- US1 and US2 are combined because they are inseparable (edit flow requires pre-population)
- No backend changes per FR-012 — all work is in the Blazor WebAssembly client
- Job metadata fields (title, jobCode, dept, org, postingDate, jobDescription) displayed as read-only context in edit mode since no update endpoint exists; only config fields (`UpdateConfigDto` properties) are editable
- Stack A's React code sends metadata fields to the config endpoint but the backend ignores them — the Blazor implementation correctly sends only config fields
- The existing `CreateJobDialog.razor` pattern (draggable, resizable, tabbed) is reused — no new component needed
- `UpdateJobConfigAsync` already exists in `ApiClient.cs` — the edit dialog calls it on save
- `ApiException` already exists with `EnsureSuccessOrThrowAsync` helper — reuse for error handling in T013
- JSON deserialization in T005 must handle null JSON strings (fields may be null for jobs with no rubric/criteria configured)
- Each save creates a new configuration version (handled by existing backend per FR-011)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
