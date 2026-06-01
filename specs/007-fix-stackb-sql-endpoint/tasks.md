# Tasks: Fix Stack B Azure SQL Bootstrap and Wire AWR Endpoint

**Input**: Design documents from `/specs/007-fix-stackb-sql-endpoint/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, quickstart.md ✅

**Tests**: Automated test-code tasks were not requested; runtime verification tasks are included.

**Organization**: Tasks grouped by user story. US1 and US2 are co-P1 and independent (different files). US3 is a post-code deployment verification phase.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2)
- Exact file paths included in descriptions

## Path Conventions

- **C# server**: `dotnet/src/Web.Server/`
- **Terraform live roots**: `infra/terraform/live/stack-b/`
- **Scripts**: `infra/scripts/`

---

## Phase 1: Verification (Pre-Implementation Checks)

**Purpose**: Confirm assumptions from research.md before making changes

- [x] T001 Verify Stack B module accepts `extra_app_settings` by inspecting `infra/terraform/modules/stack-b/variables.tf` (line 77) and confirming `merge(... var.extra_app_settings)` in `infra/terraform/modules/stack-b/main.tf` (line 35)
- [x] T002 Verify `common.sh` already exports `TF_VAR_awr_seq_api_endpoint` and remains unchanged in git diff by inspecting `infra/scripts/lib/common.sh` (line 132)
- [x] T011 Verify FR-004 scope guard by confirming no changes to `Task.Run` wrapper or `ExecuteWithSqlWarmupRetryAsync` retry flow in `dotnet/src/Web.Server/Program.cs` outside SQL command execution method swap
- [x] T012 Verify FR-005 scope guard by confirming `server/storage/schema.sql` remains unchanged in git diff
- [x] T014 Verify FR-009 scope guard for environment config by confirming no git diff changes in `.env_qa` and `.env_qa.example`

---

## Phase 2: User Story 1 — Complete Database Schema Creation on Azure (Priority: P1) 🎯 MVP

**Goal**: Fix the SQL schema bootstrap so `EnsureSharedAzureSqlSchemaIfNeeded` executes all DDL batches without `FormatException`, by replacing `ExecuteSqlRaw` with ADO.NET `DbCommand.ExecuteNonQuery()`.

**Independent Test**: Deploy Stack B to Azure against a fresh (empty) SQL database and verify all tables defined in `server/storage/schema.sql` are created without errors.

### Implementation

- [x] T003 [US1] Replace `ExecuteSqlRaw` with ADO.NET `DbCommand.ExecuteNonQuery()` in `EnsureSharedAzureSqlSchemaIfNeeded` in `dotnet/src/Web.Server/Program.cs` (lines 324–342): get the underlying `DbConnection` via `db.Database.GetDbConnection()`, manage connection open/close with `shouldClose` guard (matching the SQLite pattern at lines 287–322), iterate batches using `connection.CreateCommand()` + `ExecuteNonQuery()` instead of `db.Database.ExecuteSqlRaw(batch)`

**Checkpoint**: `EnsureSharedAzureSqlSchemaIfNeeded` now uses ADO.NET for literal SQL execution. Curly braces in `DEFAULT '{}'` values are no longer misinterpreted as format placeholders.

---

## Phase 3: User Story 2 — AWR API Connectivity from Stack B (Priority: P1)

**Goal**: Wire the `AWR_SEQ_API_ENDPOINT` environment variable into Stack B's Terraform live root so Stack B can connect to the AWR API for scoring, extraction, and health checks.

**Independent Test**: Run `terraform plan` for Stack B and verify `AWR_SEQ_API_ENDPOINT` appears in planned app settings when `TF_VAR_awr_seq_api_endpoint` is set; verify no change when empty.

### Implementation

- [x] T004 [P] [US2] Add `awr_seq_api_endpoint` variable declaration to `infra/terraform/live/stack-b/variables.tf` — mirror Stack A's declaration at `infra/terraform/live/stack-a/variables.tf` lines 88–92 (type `string`, default `""`, description for Stack B)
- [x] T005 [US2] Add conditional local and pass `extra_app_settings` to module in `infra/terraform/live/stack-b/main.tf` — add `stack_b_extra_app_settings` local in the `locals` block (mirroring Stack A's pattern at `infra/terraform/live/stack-a/main.tf` lines 14–16) and add `extra_app_settings = local.stack_b_extra_app_settings` argument to the `module "stack_b"` block

**Checkpoint**: `terraform plan` in `infra/terraform/live/stack-b/` shows `AWR_SEQ_API_ENDPOINT` in app settings when the variable is provided, and shows no change when it is empty.

---

## Phase 4: User Story 3 — Post-Fix Deployment Verification (Priority: P2)

**Goal**: Rebuild, redeploy, and verify the combined fix produces a fully working Stack B deployment.

**Depends on**: US1 (T003) and US2 (T004–T005) both complete.

**Independent Test**: Full end-to-end — schema tables all created, health endpoint shows AWR API connectivity, no-regression checks pass, and transient SQL startup interruption recovers via the existing retry flow.

### Deployment & Verification

- [ ] T006 [US3] Rebuild Stack B by running `bash infra/scripts/package-stack-b.sh` and confirm build succeeds with artifact at `artifacts/stack-b/`
- [ ] T007 [US3] Deploy Stack B artifact to Azure App Service using `az webapp deploy` per `specs/007-fix-stackb-sql-endpoint/quickstart.md` Step 2
- [ ] T008 [US3] Apply Terraform for Stack B by running `terraform plan -out=tfplan` then `terraform apply tfplan` in `infra/terraform/live/stack-b/` — verify `AWR_SEQ_API_ENDPOINT` appears in the plan output (SC-004)
- [ ] T009 [US3] Verify schema bootstrap success (SC-001, SC-002): query Azure SQL `INFORMATION_SCHEMA.TABLES` and explicitly assert `table_count = 17`; verify schema-derived `CREATE TABLE` count is also `17`; check application logs for absence of `FormatException`
- [ ] T017 [US3] Verify idempotent restart behavior (US1 AC3): restart Stack B after successful bootstrap and confirm no duplicate table creation errors, no bootstrap failure, and stable startup logs
- [ ] T018 [US3] Verify NFR-001 retry safety (SC-006): induce a transient SQL connectivity failure during startup, confirm retry logs, and confirm eventual successful bootstrap completion
- [ ] T010 [US3] Verify AWR API health check (SC-003): hit Stack B health endpoint and confirm `awrApi` dependency reports status other than `"skipped"`
- [ ] T013 [US3] Verify SC-005 no-regression scope: confirm Stack A health endpoint remains unchanged, `terraform plan` for non-target roots shows no unintended drift, and non-Azure schema bootstrap path remains successful
- [ ] T019 [US3] Verify malformed AWR endpoint edge case: deploy with malformed `AWR_SEQ_API_ENDPOINT`, confirm app startup remains stable, and health dependency reports unreachable/error (not `"skipped"`)
- [ ] T015 [US3] Verify NFR-002 missing-schema-file diagnostic: run a controlled deployment validation where `server/storage/schema.sql` is absent from the artifact and confirm logs emit a clear "schema file not found" bootstrap error
- [ ] T016 [US3] Verify NFR-002 SQL-execution diagnostic: run a controlled failure-path validation with an intentionally invalid SQL batch in a temporary test artifact and confirm startup logs include actionable SQL execution error details

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Verification)**: No dependencies — start immediately
- **Phase 2 (US1 — SQL fix)**: Can start after Phase 1 (or in parallel if confident)
- **Phase 3 (US2 — Terraform fix)**: Can start after Phase 1 (or in parallel with Phase 2 — different files)
- **Phase 4 (US3 — Deploy & Verify)**: Depends on BOTH Phase 2 and Phase 3 completion

### User Story Dependencies

- **US1 (P1)**: Independent — touches only `dotnet/src/Web.Server/Program.cs`
- **US2 (P1)**: Independent — touches only `infra/terraform/live/stack-b/variables.tf` and `infra/terraform/live/stack-b/main.tf`
- **US3 (P2)**: Depends on US1 + US2 — deployment verification of combined fixes

### Parallel Opportunities

- **T001 and T002** can run in parallel (read-only verification of different files)
- **T003 and T004/T005** can run in parallel (US1 is C# changes, US2 is Terraform changes — completely different files)
- **T004** can run in parallel with T005 (different Terraform files), though T005 references the local that uses T004's variable
- **T006–T010** are sequential (build → deploy → apply → verify schema → verify health)
- **T017** runs after T009 to validate idempotent restart behavior
- **T018** runs after T009 to validate retry-safe transient-failure recovery
- **T013** runs after T010 as final no-regression verification
- **T019** runs after T010 as malformed-endpoint edge-case validation
- **T015** runs after T009 to validate missing-schema diagnostic behavior
- **T016** runs after T009 to validate SQL-execution diagnostic behavior

### Parallel Example: US1 + US2

```text
# These can execute simultaneously — zero file overlap:
Worker A: T003 [US1] Fix ExecuteSqlRaw in Program.cs
Worker B: T004 [US2] Add variable to stack-b/variables.tf
Worker B: T005 [US2] Add local + extra_app_settings to stack-b/main.tf
```

---

## Implementation Strategy

### MVP First (US1 + US2 in Parallel)

1. Complete Phase 1: Verify assumptions (T001–T002)
2. Complete Phase 2 + Phase 3 in parallel: Fix SQL bootstrap (T003) + Wire Terraform (T004–T005)
3. **STOP and VALIDATE**: `dotnet build` for C# change; `terraform validate` + `terraform plan` for Terraform changes
4. Complete Phase 4: Full deployment verification (T006–T010, then T017, T018, T013, T019, T015, and T016)

### Incremental Delivery

1. T001–T002 → Assumptions confirmed
2. T003 → SQL bootstrap fix done → can verify locally if SQLite path exercises same pattern
3. T004–T005 → Terraform wiring done → `terraform plan` confirms correctness
4. T006–T010 → Combined deployment verification
5. T017 + T018 → Idempotent restart and retry-safety verification
6. T013 + T019 → No-regression and malformed-endpoint edge-case verification
7. T015 + T016 → Bootstrap diagnostic verification (missing-file + SQL execution failure) → feature complete

---

## Notes

- **Primary code-change scope is 3 files**: `Program.cs`, `stack-b/variables.tf`, `stack-b/main.tf` — zero new code files; runtime verification and diagnostics tasks are additionally included.
- **No schema SQL changes** (FR-005): `server/storage/schema.sql` is read-only
- **No `.env_qa` or `common.sh` changes** (FR-009): already correctly configured
- **No shared module changes**: Stack B module already accepts `extra_app_settings`
- FR-004 and FR-005 now have explicit verification tasks (T011, T012)
- FR-009 now has explicit verification coverage without overlap: `common.sh` (T002) and `.env_qa` files (T014)
- SC-005 now has an explicit no-regression verification task (T013)
- NFR-002 diagnostics now have explicit verification coverage for missing-file and SQL-execution failure paths (T015, T016)
- The SQL fix mirrors the existing SQLite bootstrap pattern (Program.cs lines 287–322)
- The Terraform fix mirrors the existing Stack A pattern (live/stack-a/main.tf lines 14–16)
- Commit after each user story phase for clean git history
