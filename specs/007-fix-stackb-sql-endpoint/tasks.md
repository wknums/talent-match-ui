# Tasks: Fix Stack B Azure SQL Bootstrap and Wire AWR Endpoint

**Input**: Design documents from `/specs/007-fix-stackb-sql-endpoint/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, quickstart.md ✅

**Tests**: Not requested — no test tasks included.

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
- [x] T002 Verify `common.sh` already exports `TF_VAR_awr_seq_api_endpoint` by inspecting `infra/scripts/lib/common.sh` (line 132)

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

**Independent Test**: Full end-to-end — schema tables all created, health endpoint shows AWR API connectivity.

### Deployment & Verification

- [ ] T006 [US3] Rebuild Stack B by running `bash infra/scripts/package-stack-b.sh` and confirm build succeeds with artifact at `artifacts/stack-b/`
- [ ] T007 [US3] Deploy Stack B artifact to Azure App Service using `az webapp deploy` per `specs/007-fix-stackb-sql-endpoint/quickstart.md` Step 2
- [ ] T008 [US3] Apply Terraform for Stack B by running `terraform plan -out=tfplan` then `terraform apply tfplan` in `infra/terraform/live/stack-b/` — verify `AWR_SEQ_API_ENDPOINT` appears in the plan output (SC-004)
- [ ] T009 [US3] Verify schema bootstrap success (SC-001, SC-002): query Azure SQL `INFORMATION_SCHEMA.TABLES` for table count matching `server/storage/schema.sql` CREATE TABLE count; check application logs for absence of `FormatException`
- [ ] T010 [US3] Verify AWR API health check (SC-003): hit Stack B health endpoint and confirm `awrApi` dependency reports status other than `"skipped"`

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
4. Complete Phase 4: Full deployment verification (T006–T010)

### Incremental Delivery

1. T001–T002 → Assumptions confirmed
2. T003 → SQL bootstrap fix done → can verify locally if SQLite path exercises same pattern
3. T004–T005 → Terraform wiring done → `terraform plan` confirms correctness
4. T006–T010 → Combined deployment verification → feature complete

---

## Notes

- **3 files changed**: `Program.cs`, `stack-b/variables.tf`, `stack-b/main.tf` — zero new files
- **No schema SQL changes** (FR-005): `server/storage/schema.sql` is read-only
- **No `.env_qa` or `common.sh` changes** (FR-009): already correctly configured
- **No shared module changes**: Stack B module already accepts `extra_app_settings`
- The SQL fix mirrors the existing SQLite bootstrap pattern (Program.cs lines 287–322)
- The Terraform fix mirrors the existing Stack A pattern (live/stack-a/main.tf lines 14–16)
- Commit after each user story phase for clean git history
