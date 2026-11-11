# Tasks: Selective Azure Deployment

**Input**: Design documents from `/specs/006-selective-azure-deploy/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, quickstart.md, contracts/

**Tests**: No dedicated test tasks generated. The specification defines independent validation scenarios, but it does not request TDD or explicit test-first implementation tasks.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for traceability (`[US1]`, `[US2]`, `[US3]`, `[US4]`, `[US5]`, `[US6]`)
- Each task includes exact file paths

## Phase 1: Setup

**Purpose**: Establish the repo-level deployment scaffolding and environment profile placeholders required by all later Terraform and Bash work.

- [x] T001 Update `.gitignore` to ignore `.env_local`, `.env_qa`, `.env_prod`, `.terraform/`, and `*.tfstate*` for the new Terraform/Bash deployment assets
- [x] T002 Create the development deployment profile template in `.env_local.example` with placeholder Azure subscription, naming, stack-target, and reuse settings for `dev`
- [x] T003 [P] Create the staging deployment profile template in `.env_qa.example` with placeholder Azure subscription, naming, stack-target, and reuse settings for `test`
- [x] T004 [P] Create the production deployment profile template in `.env_prod.example` with placeholder Azure subscription, naming, stack-target, and reuse settings for `prod`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Build the shared Bash and Terraform foundations that every selective deploy scenario depends on.

**⚠️ CRITICAL**: No user story work should begin until this phase is complete.

- [x] T005 Create shared Bash helpers in `infra/scripts/lib/common.sh` for loading `.env_*` files, validating required inputs, exporting `TF_VAR_*` values, and handling Git Bash Azure CLI path-conversion safeguards
- [x] T006 [P] Create the reuse-aware App Service plan module in `infra/terraform/modules/foundation/app-service-plan/main.tf`, `infra/terraform/modules/foundation/app-service-plan/variables.tf`, and `infra/terraform/modules/foundation/app-service-plan/outputs.tf`
- [x] T007 [P] Create the reuse-aware SQL module in `infra/terraform/modules/foundation/sql/main.tf`, `infra/terraform/modules/foundation/sql/variables.tf`, and `infra/terraform/modules/foundation/sql/outputs.tf`
- [x] T008 [P] Create the reuse-aware Key Vault module in `infra/terraform/modules/foundation/key-vault/main.tf`, `infra/terraform/modules/foundation/key-vault/variables.tf`, and `infra/terraform/modules/foundation/key-vault/outputs.tf`
- [x] T009 [P] Create the reuse-aware API Management module in `infra/terraform/modules/foundation/apim/main.tf`, `infra/terraform/modules/foundation/apim/variables.tf`, and `infra/terraform/modules/foundation/apim/outputs.tf`
- [x] T010 [P] Create the reuse-aware managed identities module in `infra/terraform/modules/foundation/identities/main.tf`, `infra/terraform/modules/foundation/identities/variables.tf`, and `infra/terraform/modules/foundation/identities/outputs.tf`
- [x] T011 [P] Create the shared App Service host building block in `infra/terraform/modules/foundation/app-service/main.tf`, `infra/terraform/modules/foundation/app-service/variables.tf`, and `infra/terraform/modules/foundation/app-service/outputs.tf`
- [x] T012 Create the shared root Terraform shell in `infra/terraform/live/shared/versions.tf`, `infra/terraform/live/shared/providers.tf`, and `infra/terraform/live/shared/variables.tf`
- [x] T013 [P] Create the Stack A root Terraform shell in `infra/terraform/live/stack-a/versions.tf`, `infra/terraform/live/stack-a/providers.tf`, and `infra/terraform/live/stack-a/variables.tf`
- [x] T014 [P] Create the Stack B root Terraform shell in `infra/terraform/live/stack-b/versions.tf`, `infra/terraform/live/stack-b/providers.tf`, and `infra/terraform/live/stack-b/variables.tf`

**Checkpoint**: The repository now has the shared Terraform/Bash foundation required for selective deployment work.

---

## Phase 3: User Story 1 - Deploy a Single Stack to Azure (Priority: P1) 🎯 MVP

**Goal**: Allow an operator to deploy `stack-a`, `stack-b`, or `both` with Terraform and Bash, while provisioning or reusing shared infrastructure through the three live roots.

**Independent Test**: Run `./infra/scripts/deploy.sh .env_local dev apply stack-a` and verify only shared + Stack A roots run and Stack A becomes reachable; repeat with `stack-b`.

### Implementation for User Story 1

- [x] T015 [P] [US1] Implement the Stack A composition module in `infra/terraform/modules/stack-a/main.tf`, `infra/terraform/modules/stack-a/variables.tf`, and `infra/terraform/modules/stack-a/outputs.tf` using the shared App Service building block and stable shared-root inputs
- [x] T016 [P] [US1] Implement the Stack B composition module in `infra/terraform/modules/stack-b/main.tf`, `infra/terraform/modules/stack-b/variables.tf`, and `infra/terraform/modules/stack-b/outputs.tf` using the shared App Service building block and stable shared-root inputs
- [x] T017 [US1] Implement the shared live root in `infra/terraform/live/shared/main.tf` and `infra/terraform/live/shared/outputs.tf` to create or reuse shared infrastructure and emit stable outputs for created or reused resources
- [x] T018 [US1] Implement the Stack A live root in `infra/terraform/live/stack-a/main.tf` and `infra/terraform/live/stack-a/outputs.tf` so Stack A consumes shared outputs without managing Stack B resources
- [x] T019 [US1] Implement the Stack B live root in `infra/terraform/live/stack-b/main.tf` and `infra/terraform/live/stack-b/outputs.tf` so Stack B consumes shared outputs without managing Stack A resources
- [x] T020 [P] [US1] Create the Stack A packaging script in `infra/scripts/package-stack-a.sh` to run the existing Node build steps and assemble an App Service deployment artifact for Stack A
- [x] T021 [P] [US1] Create the Stack B packaging script in `infra/scripts/package-stack-b.sh` to run `dotnet publish` for `dotnet/src/Web.Server/TalentMatch.Web.Server.csproj` and assemble an App Service deployment artifact for Stack B
- [x] T022 [US1] Implement the deployment wrapper in `infra/scripts/deploy.sh` to load `.env_*`, sequence `shared`, `stack-a`, and `stack-b` roots correctly for `shared-only`, `stack-a`, `stack-b`, and `both`, and print stable deployment outputs at the end of each run

**Checkpoint**: A single stack can be deployed to Azure through the Terraform/Bash operator flow without touching the other stack.

---

## Phase 4: User Story 2 - Automated CI/CD Pipeline with Stack Selection (Priority: P2)

**Goal**: Provide one GitHub Actions workflow that detects changed scopes or honors a manual target selection and deploys only the necessary roots and artifacts.

**Independent Test**: Push a change only under `src/` or `server/` and verify the workflow packages and deploys only Stack A; manually dispatch `stack-b` and verify only Stack B runs.

### Implementation for User Story 2

- [x] T023 [US2] Implement `infra/scripts/resolve-targets.sh` to merge `workflow_dispatch` target overrides with changed-path detection, map GitHub environments to `.env_*` and `dev|test|prod`, and emit `deploy_shared`, `deploy_stack_a`, `deploy_stack_b`, `package_stack_a`, and `package_stack_b`
- [x] T024 [US2] Create the single workflow in `.github/workflows/selective-azure-deploy.yml` with `push` and `workflow_dispatch` triggers, changed-path filters for `shared`, `stack_a`, and `stack_b`, and the `resolve-targets` job contract defined in `specs/006-selective-azure-deploy/contracts/workflow-dispatch.md`
- [x] T025 [US2] Extend `.github/workflows/selective-azure-deploy.yml` with the shared apply job, Stack A and Stack B package jobs, and Stack A and Stack B deploy jobs so the workflow preserves the shared-first dependency graph while still allowing stack package and deploy work to run in parallel when possible

**Checkpoint**: One workflow now supports both automatic selective deployment and manual stack selection.

---

## Phase 5: User Story 3 - Environment Configuration and Secrets Management (Priority: P2)

**Goal**: Make environment-specific settings and secrets flow through `.env_*`, Key Vault, Terraform outputs, and workflow execution without exposing secrets in source or logs.

**Independent Test**: Deploy the same stack to two environments and verify each uses the correct environment-specific settings and shared secret references, with no secret values appearing in workflow logs.

### Implementation for User Story 3

- [x] T026 [US3] Expand the development profile in `.env_local.example` with the full environment contract for location, stack target, reuse booleans, existing resource coordinates, and non-secret runtime settings required by `infra/scripts/deploy.sh`
- [x] T027 [P] [US3] Expand the staging profile in `.env_qa.example` with the full environment contract for location, stack target, reuse booleans, existing resource coordinates, and non-secret runtime settings required by `infra/scripts/deploy.sh`
- [x] T028 [P] [US3] Expand the production profile in `.env_prod.example` with the full environment contract for location, stack target, reuse booleans, existing resource coordinates, and non-secret runtime settings required by `infra/scripts/deploy.sh`
- [x] T029 [US3] Extend `infra/scripts/lib/common.sh` and `infra/scripts/deploy.sh` to validate missing reuse coordinates early, export environment-specific `TF_VAR_*` values without echoing secrets, and fail fast when required secret references or profile inputs are missing
- [x] T030 [P] [US3] Extend `infra/terraform/live/shared/main.tf` and `infra/terraform/live/shared/outputs.tf` to publish stable shared outputs for Key Vault URIs, SQL endpoints, APIM gateway URLs, and managed identity IDs regardless of whether the underlying resources were created or reused
- [x] T031 [P] [US3] Wire Stack A runtime settings in `infra/terraform/modules/stack-a/main.tf` so cloud deployments use `STORAGE_PROVIDER=azuresql`, Key Vault-backed SQL settings, and the shared APIM endpoint without introducing client-exposed secrets
- [x] T032 [P] [US3] Wire Stack B runtime settings in `infra/terraform/modules/stack-b/main.tf` so cloud deployments use `DatabaseProvider=sqlserver`, Key Vault-backed connection settings, and the shared APIM endpoint without introducing client-exposed secrets
- [x] T033 [US3] Harden secret handling in `.github/workflows/selective-azure-deploy.yml` so environment inputs, Terraform outputs, and deployment commands avoid printing secret values while still surfacing actionable failure messages

**Checkpoint**: Environment profiles, runtime configuration, and secrets management are production-safe and environment-isolated.

---

## Phase 6: User Story 4 - Shared Infrastructure Provisioning (Priority: P3)

**Goal**: Ensure shared infrastructure can be provisioned independently, reused safely, and deprovisioned only through the protected Bash flow.

**Independent Test**: Run `./infra/scripts/deploy.sh .env_qa test apply shared-only`, then deploy Stack A and Stack B separately and verify both consume the same shared outputs without duplicate shared resources; run the deprovision wrapper in dry-run mode and verify reused resources are marked protected.

### Implementation for User Story 4

- [x] T034 [US4] Implement the protected destroy wrapper in `infra/scripts/deprovision.sh` with `shared`, `stack-a`, `stack-b`, and `both` target handling, `.env_*` enforcement, `--dry-run` and `--force` options, and the `[DESTROY]` versus `[PROTECTED]` summary required by the plan
- [x] T035 [P] [US4] Finalize stable create-or-reuse outputs in `infra/terraform/modules/foundation/app-service-plan/outputs.tf`, `infra/terraform/modules/foundation/sql/outputs.tf`, `infra/terraform/modules/foundation/key-vault/outputs.tf`, `infra/terraform/modules/foundation/apim/outputs.tf`, and `infra/terraform/modules/foundation/identities/outputs.tf` so downstream roots never branch on ownership
- [x] T036 [US4] Update `.github/workflows/selective-azure-deploy.yml` to support manual `shared-only` runs that skip stack packaging and deployment jobs while still applying the shared root and surfacing shared outputs

**Checkpoint**: Shared infrastructure now has an independent lifecycle and safe deprovision flow.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Finish operator documentation and scenario validation across all stories.

- [x] T037 [P] Document the Terraform/Bash selective deployment flow in `infra/README.md`, including the three live roots, reuse-aware module behavior, stable outputs, workflow expectations, and safe deprovision usage
- [x] T038 [P] Refresh `specs/006-selective-azure-deploy/quickstart.md` so its commands and validation steps match `infra/scripts/deploy.sh`, `infra/scripts/deprovision.sh`, `.github/workflows/selective-azure-deploy.yml`, and the three Terraform live roots

---

## Phase 8: User Story 5 — Database Schema Isolation (Priority: P2)

**Goal**: Isolate all 15 application database tables under a dedicated `talentmatch` schema in Azure SQL. Local SQLite development remains completely unaffected. Both Stack A (Node.js) and Stack B (.NET) must use the schema consistently.

**Independent Test**: Deploy the database schema to an Azure SQL instance and verify all 15 application tables are created under the `talentmatch` schema (none under `dbo`), then run the full application test suite (`npm test` and `dotnet test dotnet/TalentMatch.slnx`) and verify all data operations succeed in both stacks and that local SQLite development is unaffected.

**Acceptance Scenarios** (from spec.md):
1. Fresh Azure SQL → `talentmatch` schema created, all tables under it, zero in `dbo`
2. Any CRUD operation → queries correctly reference `[talentmatch].[Table]`
3. Local SQLite → queries work without schema qualification — no change to dev experience
4. Existing dbo database → migration transfers all tables without data loss
5. New table added → developer uses `T('NewTable')` in one place for automatic qualification
6. Both stacks against same DB → both use `talentmatch` schema consistently

**Requirements**: FR-017, FR-018, FR-019, FR-020, FR-021, FR-022
**Success Criteria**: SC-009, SC-010, SC-011, SC-012

### Stack A Foundation (Node.js) — Table Name Helper & DDL

- [x] T039 [US5] Create the centralized schema qualification helper in `server/storage/table-names.ts` that exports a `T(tableName)` function returning `[talentmatch].[tableName]` when `isAzureSql` is true (from `server/storage/db.ts`) and plain `tableName` when false (SQLite); the schema constant `'talentmatch'` must be defined once in this file (FR-018, SC-011)
- [x] T040 [US5] Modify `server/storage/schema.sql` to add a schema creation preamble (`IF NOT EXISTS ... CREATE SCHEMA [talentmatch]`), prefix all 15 `CREATE TABLE` statements with `[talentmatch].`, update all `IF NOT EXISTS` guards to include `AND schema_id = SCHEMA_ID('talentmatch')`, and update all `CREATE INDEX` / `CREATE UNIQUE INDEX` statements to reference `[talentmatch].[TableName]` (FR-017)
- [x] T041 [US5] Modify `server/storage/db.ts` to add a schema creation step in the `initializeDatabase()` function's Azure SQL path — execute `IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch') EXEC('CREATE SCHEMA [talentmatch]')` before running `schema.sql`, providing defense-in-depth schema creation (FR-021)

### Stack A Repository Migration — T() Helper Adoption (~99 queries across 5 files)

- [x] T042 [P] [US5] Modify `server/storage/repos/application-repo.ts` to import `T` from `../table-names.js` and replace all ~45 hardcoded table name references (Applications, ApplicationDocuments, DocumentBlobs, ExtractionArtifacts, ScoringRuns, AggregatedResults, ManualReviews, Jobs, JobConfigVersions, Users) with `T('TableName')` calls in SELECT, INSERT, UPDATE, DELETE, and JOIN clauses (FR-018, FR-019)
- [x] T043 [P] [US5] Modify `server/storage/repos/job-repo.ts` to import `T` from `../table-names.js` and replace all ~17 hardcoded table name references (Jobs, JobConfigVersions, Applications, ScoringPrompts) with `T('TableName')` calls in all SQL queries (FR-018, FR-019)
- [x] T044 [P] [US5] Modify `server/storage/repos/audit-repo.ts` to import `T` from `../table-names.js` and replace all ~15 hardcoded table name references (ProcessingEvents, FailureQueueItems) with `T('TableName')` calls in all SQL queries (FR-018, FR-019)
- [x] T045 [P] [US5] Modify `server/storage/repos/prompt-repo.ts` to import `T` from `../table-names.js` and replace all ~12 hardcoded table name references (ScoringPrompts, PromptTestRuns, Jobs) with `T('TableName')` calls in all SQL queries (FR-018, FR-019)
- [x] T046 [P] [US5] Modify `server/storage/repos/user-repo.ts` to import `T` from `../table-names.js` and replace all ~10 hardcoded table name references (Users, PasswordResetRequests) with `T('TableName')` calls in all SQL queries (FR-018, FR-019)

### Stack B (.NET EF Core) — Default Schema Configuration

- [x] T047 [P] [US5] Modify `dotnet/src/Infrastructure/Persistence/AppDbContext.cs` to add `if (Database.IsSqlServer()) modelBuilder.HasDefaultSchema("talentmatch");` at the start of the `OnModelCreating` method (before existing entity configurations), so all EF Core-generated SQL targets the `talentmatch` schema on SQL Server while SQLite remains unaffected (FR-022)

### Infrastructure — Migration Script & Terraform

- [x] T048 [P] [US5] Create the idempotent migration script `infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql` that: (1) creates the `talentmatch` schema if absent, (2) transfers all 15 application tables from `[dbo]` to `[talentmatch]` using `ALTER SCHEMA [talentmatch] TRANSFER [dbo].[TableName]`, (3) skips tables already in the target schema, (4) wraps each transfer in TRY/CATCH for per-table error reporting, and (5) prints a summary of transferred vs skipped vs failed tables (FR-020, SC-012)
- [x] T049 [P] [US5] Modify `infra/terraform/modules/foundation/sql/main.tf` to add a provisioner (or `null_resource` with `local-exec`) that executes `IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch') EXEC('CREATE SCHEMA [talentmatch]')` against the Azure SQL database after creation, ensuring the schema exists before any application deployment (FR-021)

### Cleanup — Remove Orphaned Test

- [x] T050 [P] [US5] Delete `tests/unit/local-kv.test.ts` — orphaned test file referencing removed dead KV storage code that no longer exists in the codebase

**Checkpoint**: All 15 application tables are schema-qualified under `[talentmatch]` in Azure SQL. Both Stack A (via `T()` helper) and Stack B (via `HasDefaultSchema`) consistently use the schema. SQLite local development is completely unaffected. The migration script can safely transfer existing `dbo` databases.

---

## Phase 9: Polish & Documentation (US5)

**Purpose**: Update all relevant documentation to reflect the `talentmatch` schema configuration, migration process, and verification steps.

- [x] T051 [P] Update `infra/README.md` to add a "Database Schema Isolation" section documenting: the `talentmatch` schema convention, how the Terraform SQL module creates the schema, the `T()` helper pattern in Node.js repos, the `HasDefaultSchema` approach in .NET, the `migrate-schema-dbo-to-talentmatch.sql` migration script usage, and verification queries to confirm schema state
- [x] T052 [P] Update `specs/006-selective-azure-deploy/quickstart.md` to expand the existing "Database Schema Isolation (US5)" section with complete verification steps: schema existence check, table count validation (`SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('talentmatch')` must return 15), zero-dbo confirmation, SQLite unaffected verification (`npm test`), .NET stack verification (`dotnet test dotnet/TalentMatch.slnx`), and migration dry-run instructions for existing databases
- [x] T053 [P] Update `README.md` (project root) to revise the "Storage Configuration" and "Azure SQL" sections to document that Azure SQL deployments use the `talentmatch` schema (all tables created under `[talentmatch].[TableName]`), reference the `T()` helper for Node.js developers adding new tables, and note that local SQLite development is unaffected
- [x] T054 Run the US5 verification steps from `specs/006-selective-azure-deploy/quickstart.md` against local SQLite to confirm: `npm test` passes (all existing tests green), `dotnet test dotnet/TalentMatch.slnx` passes, and no regressions in data operations across both stacks

## Phase 10: User Story 6 — Private Network Connectivity (Priority: P2)

**Goal**: Configure both App Services with VNet Integration via a delegated subnet on an existing VNet for private Azure SQL connectivity, and apply IP access restrictions that deny all inbound traffic except explicitly allowed IPs. No application code changes — Terraform and Bash only.

**Independent Test**: Deploy with `AZ_VNET_REUSE=TRUE`, an existing VNet, and `AZ_ALLOWED_IPS` set. Verify: (1) `az webapp vnet-integration list` shows the integration subnet for both App Services; (2) `az webapp config access-restriction show` shows Allow rules for each allowed IP and a Deny default; (3) `nslookup sql-*.database.windows.net` from inside the App Service resolves to a private IP; (4) `terraform -chdir=infra/terraform/live/shared output integration_subnet_id` returns the subnet resource ID.

**Acceptance Scenarios** (from spec.md):
1. VNet reuse — no new VNet created (FR-025)
2. Existing subnet reuse — no new subnet created (FR-026 option A)
3. New delegated subnet creation with CIDR (FR-026 option B)
4. IP restrictions — Allow only listed IPs, deny all else (FR-024)
5. HTTP 403 from non-allowed IP (SC-014)
6. Both stacks share same integration subnet (FR-028)
7. SQL Private Endpoint reuse — no PE created (FR-027)
8. Private DNS resolves SQL to private IP (SC-013, SC-018)
9. CIDR validation — /26 minimum (FR-030)
10. Fail-fast on missing VNet config (FR-031) and empty allowed IPs (FR-032)

**Requirements**: FR-023 through FR-032
**Success Criteria**: SC-013 through SC-018

### Networking Foundation Module (NEW)

- [x] T055 [US6] Create the networking foundation module in `infra/terraform/modules/foundation/networking/variables.tf` with inputs: `vnet_name` (string, required), `vnet_resource_group` (string, required), `existing_subnet_name` (string, default `""`), `subnet_cidr` (string, default `""`), `subnet_name` (string, default `"snet-appservice-integration"`), and `tags` (map(string), default `{}`); include Terraform validation blocks enforcing: vnet_name not empty, vnet_resource_group not empty, exactly one of existing_subnet_name or subnet_cidr must be non-empty, and subnet_cidr prefix ≤26 when set (FR-030)
- [x] T056 [US6] Create the networking foundation module in `infra/terraform/modules/foundation/networking/main.tf` with: (1) `data "azurerm_virtual_network"` to look up the existing VNet by name and resource group (FR-025); (2) `data "azurerm_subnet"` with `count = var.existing_subnet_name != "" ? 1 : 0` for reuse mode; (3) `resource "azurerm_subnet"` with `count = var.subnet_cidr != "" ? 1 : 0`, delegation to `Microsoft.Web/serverFarms`, and the specified CIDR (FR-026); no Private Endpoint resources created (FR-027)
- [x] T057 [US6] Create the networking foundation module outputs in `infra/terraform/modules/foundation/networking/outputs.tf` exposing `vnet_id`, `vnet_name`, `integration_subnet_id` (stable regardless of reuse/create mode using conditional: `var.existing_subnet_name != "" ? data.azurerm_subnet.existing[0].id : azurerm_subnet.integration[0].id`), and `integration_subnet_name`

### App Service Module Modification (VNet Integration + IP Restrictions)

- [x] T058 [US6] Modify `infra/terraform/modules/foundation/app-service/variables.tf` to add two new variables: `virtual_network_subnet_id` (string, default `null`, description: "Integration subnet ID for VNet Integration") and `allowed_ips` (list(string), default `[]`, description: "Public IPs to allow — when non-empty, deny-all default is enforced")
- [x] T059 [US6] Modify `infra/terraform/modules/foundation/app-service/main.tf` to add `virtual_network_subnet_id = var.virtual_network_subnet_id` on the `azurerm_linux_web_app` resource, and inside `site_config` add: `vnet_route_all_enabled = var.virtual_network_subnet_id != null ? true : null`, `ip_restriction_default_action = length(var.allowed_ips) > 0 ? "Deny" : null`, and a `dynamic "ip_restriction"` block iterating over `var.allowed_ips` producing Allow rules with `ip_address = "${ip_restriction.value}/32"`, `priority = 100 + ip_restriction.key`, `name = "AllowIP-${ip_restriction.key}"` per the contract in `specs/006-selective-azure-deploy/contracts/terraform-module-and-script-interfaces.md`

### Stack Composition Module Pass-Through (Stack A + Stack B)

- [x] T060 [P] [US6] Modify `infra/terraform/modules/stack-a/variables.tf` to add `virtual_network_subnet_id` (string, default `null`) and `allowed_ips` (list(string), default `[]`); modify `infra/terraform/modules/stack-a/main.tf` to pass `virtual_network_subnet_id = var.virtual_network_subnet_id` and `allowed_ips = var.allowed_ips` through to the `module.app_service` call
- [x] T061 [P] [US6] Modify `infra/terraform/modules/stack-b/variables.tf` to add `virtual_network_subnet_id` (string, default `null`) and `allowed_ips` (list(string), default `[]`); modify `infra/terraform/modules/stack-b/main.tf` to pass `virtual_network_subnet_id = var.virtual_network_subnet_id` and `allowed_ips = var.allowed_ips` through to the `module.app_service` call

### Shared Live Root — Networking Module Integration

- [x] T062 [US6] Modify `infra/terraform/live/shared/variables.tf` to add new networking variables: `reuse_vnet` (bool, default `false`), `vnet_name` (string, default `""`), `vnet_resource_group` (string, default `""`), `existing_integration_subnet_name` (string, default `""`), `integration_subnet_cidr` (string, default `""`), `reuse_sql_private_endpoint` (bool, default `false`), and `allowed_ips` (list(string), default `[]`) — all defaults ensure backward compatibility with existing deployments that don't set networking variables
- [x] T063 [US6] Modify `infra/terraform/live/shared/main.tf` to add a `module "networking"` block (conditional on `var.reuse_vnet`) that calls `../../modules/foundation/networking` passing `vnet_name = var.vnet_name`, `vnet_resource_group = var.vnet_resource_group`, `existing_subnet_name = var.existing_integration_subnet_name`, `subnet_cidr = var.integration_subnet_cidr`, and `tags = local.tags`; use `count = var.reuse_vnet ? 1 : 0` so the module is skipped entirely when networking is not configured
- [x] T064 [US6] Modify `infra/terraform/live/shared/outputs.tf` to add `integration_subnet_id` output with `value = var.reuse_vnet ? module.networking[0].integration_subnet_id : ""` and `description = "Delegated subnet ID for App Service VNet Integration"`

### Stack Live Roots — Accept and Pass Networking Inputs

- [x] T065 [P] [US6] Modify `infra/terraform/live/stack-a/variables.tf` to add `integration_subnet_id` (string, default `""`) and `allowed_ips` (list(string), default `[]`); modify `infra/terraform/live/stack-a/main.tf` to pass `virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null` and `allowed_ips = var.allowed_ips` to the `module.stack_a` call
- [x] T066 [P] [US6] Modify `infra/terraform/live/stack-b/variables.tf` to add `integration_subnet_id` (string, default `""`) and `allowed_ips` (list(string), default `[]`); modify `infra/terraform/live/stack-b/main.tf` to pass `virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null` and `allowed_ips = var.allowed_ips` to the `module.stack_b` call

### Bash Script Extensions — Variable Export + Validation

- [x] T067 [US6] Modify `infra/scripts/lib/common.sh` to extend the `export_tf_vars` function with new networking TF_VAR exports: `TF_VAR_reuse_vnet` (from `AZ_VNET_REUSE` via `bool_to_tf`), `TF_VAR_vnet_name` (from `AZ_VNET_NAME`), `TF_VAR_vnet_resource_group` (from `AZ_VNET_RG`), `TF_VAR_existing_integration_subnet_name` (from `AZ_INTEGRATION_SUBNET_NAME`), `TF_VAR_integration_subnet_cidr` (from `AZ_INTEGRATION_SUBNET_CIDR`), `TF_VAR_reuse_sql_private_endpoint` (from `AZ_SQL_PRIVATE_ENDPOINT_REUSE` via `bool_to_tf`), and `TF_VAR_allowed_ips` (comma-separated `AZ_ALLOWED_IPS` converted to JSON array `["ip1","ip2"]` for Terraform list variable) — empty `AZ_ALLOWED_IPS` produces `[]` per the contract in `specs/006-selective-azure-deploy/contracts/terraform-module-and-script-interfaces.md`
- [x] T068 [US6] Modify `infra/scripts/lib/common.sh` to extend the `validate_reuse_coordinates` function with networking validations: when `AZ_VNET_REUSE=TRUE`, require `AZ_VNET_NAME` non-empty (FR-031), require `AZ_VNET_RG` non-empty (FR-031), require exactly one of `AZ_INTEGRATION_SUBNET_NAME` or `AZ_INTEGRATION_SUBNET_CIDR` (FR-026), validate CIDR prefix ≤26 when `AZ_INTEGRATION_SUBNET_CIDR` is set (FR-030), and require `AZ_ALLOWED_IPS` non-empty (FR-032) — all checks append to the `errors` array and fail fast before any Terraform execution
- [x] T069 [US6] Modify `infra/scripts/deploy.sh` to capture `integration_subnet_id` from the shared root output after a successful shared `apply` using `get_terraform_output "$TF_LIVE_DIR/shared" "integration_subnet_id"` and export it as `TF_VAR_integration_subnet_id` so stack-a and stack-b roots receive the subnet ID without manual intervention — only capture when the output is non-empty (backward compatible with deployments that don't use networking)

### Environment Profile Templates — Networking Variables

- [x] T070 [P] [US6] Modify `.env_local.example` to add a new "Private Network Connectivity (US6)" section with commented-out variables: `AZ_VNET_REUSE`, `AZ_VNET_NAME`, `AZ_VNET_RG`, `AZ_INTEGRATION_SUBNET_NAME`, `AZ_INTEGRATION_SUBNET_CIDR` (mutually exclusive with subnet name), `AZ_SQL_PRIVATE_ENDPOINT_REUSE`, and `AZ_ALLOWED_IPS` — include inline comments explaining each variable, the reuse-vs-create subnet choice, the /26 minimum CIDR requirement, and that local dev typically does not need networking
- [x] T071 [P] [US6] Modify `.env_qa.example` to add the same "Private Network Connectivity (US6)" section with `AZ_VNET_REUSE=TRUE` uncommented and placeholder values for `AZ_VNET_NAME`, `AZ_VNET_RG`, `AZ_INTEGRATION_SUBNET_NAME`, `AZ_SQL_PRIVATE_ENDPOINT_REUSE=TRUE`, and `AZ_ALLOWED_IPS` — staging typically uses existing VNet with existing subnet
- [x] T072 [P] [US6] Modify `.env_prod.example` to add the same "Private Network Connectivity (US6)" section with `AZ_VNET_REUSE=TRUE` uncommented and placeholder values matching the production convention — production always uses existing VNet and existing subnet

**Checkpoint**: All Terraform modules, live roots, Bash scripts, and environment profiles now support private network connectivity. Deploying with `AZ_VNET_REUSE=TRUE` and `AZ_ALLOWED_IPS` set will configure VNet Integration and IP restrictions on both App Services. Deploying without these variables works exactly as before (backward compatible).

---

## Phase 11: Polish & Documentation (US6)

**Purpose**: Update all relevant documentation and verification guides to reflect the private network connectivity configuration.

- [x] T073 [P] Update `infra/README.md` to add a "Private Network Connectivity (US6)" section documenting: the networking foundation module architecture, VNet data lookup pattern, subnet reuse-vs-create modes, the `integration_subnet_id` output flow (shared → stack roots → app-service), IP access restriction configuration, the `AZ_ALLOWED_IPS` requirement, backward compatibility with non-networked deployments, and required Azure RBAC permissions (Network Contributor on VNet resource group)
- [x] T074 [P] Update `specs/006-selective-azure-deploy/quickstart.md` to expand the existing "Private Network Connectivity (US6)" section with complete verification steps: VNet Integration check (`az webapp vnet-integration list`), IP restriction check (`az webapp config access-restriction show`), private DNS resolution check (`nslookup` from Kudu console), health endpoint from allowed IP, Terraform output verification (`terraform output integration_subnet_id`), and backward-compatibility note for deployments without networking
- [x] T075 Validate all US6 changes by running `terraform -chdir=infra/terraform/modules/foundation/networking validate`, `terraform -chdir=infra/terraform/live/shared validate`, `terraform -chdir=infra/terraform/live/stack-a validate`, and `terraform -chdir=infra/terraform/live/stack-b validate` to confirm no syntax or reference errors across the modified module chain
- [x] T076 [P] Add deployment build stamp diagnostics for both stacks: generate `build-info.json` during `infra/scripts/package-stack-a.sh` and `infra/scripts/package-stack-b.sh`, log stamp data to browser console at app startup in `src/main.tsx` and `dotnet/src/Web.Client/Program.cs`, and document verification in `specs/006-selective-azure-deploy/spec.md` plus `specs/006-selective-azure-deploy/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup and blocks all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational completion
- **User Story 2 (Phase 4)**: Depends on User Story 1 packaging and deployment scripts
- **User Story 3 (Phase 5)**: Depends on User Story 1 roots and scripts, then hardens User Story 2 workflow behavior
- **User Story 4 (Phase 6)**: Depends on User Story 1 live roots and User Story 2 workflow so shared-only and safe destroy paths can be completed
- **Polish US1–US4 (Phase 7)**: Depends on User Stories 1–4 being complete
- **User Story 5 (Phase 8)**: Depends on Phase 7 completion (US1–US4 infrastructure and documentation in place); within Phase 8, T039 (table-names.ts) blocks T042–T046 (repo files); T040/T041 (schema.sql/db.ts) can proceed in parallel with T039; T047–T050 are independent of Node.js tasks
- **Polish US5 (Phase 9)**: Depends on all Phase 8 tasks being complete
- **User Story 6 (Phase 10)**: Depends on US1's App Service module (`infra/terraform/modules/foundation/app-service/`), shared root (`infra/terraform/live/shared/`), stack roots, and deploy script; independent of US5 at the code level — no file conflicts between US5 and US6
- **Polish US6 (Phase 11)**: Depends on all Phase 10 tasks being complete

### User Story Dependencies

- **US1**: First deliverable and MVP after Phase 2
- **US2**: Depends on US1 because the workflow calls the Bash packaging and deployment entry points created there
- **US3**: Depends on US1 for Terraform roots and on US2 for workflow hardening
- **US4**: Depends on US1 for shared-root semantics and on US2 for `shared-only` workflow execution
- **US5**: Independent of US2/US3/US4 at the code level but depends on US1's Terraform SQL module (`infra/terraform/modules/foundation/sql/main.tf`) and US4's stable shared outputs; can be developed after Phase 7 without blocking or being blocked by other stories
- **US6**: Independent of US5 at the code level (no shared files). Depends on US1's App Service foundation module, stack composition modules, live roots, and deploy.sh. Can be developed after US5 (Phase 9) without conflicts, or in parallel with US5 if file-level coordination is managed

### Within User Story 6

- T055–T057 (networking module) must complete before T063 (shared root calls the module)
- T058–T059 (app-service modifications) must complete before T060–T061 (stack modules pass new variables)
- T055–T057 and T058–T059 can proceed **in parallel** (different modules, no file overlap)
- T060 and T061 (stack-a and stack-b composition) can run **in parallel** (different files)
- T062–T064 (shared live root) depend on T055–T057 (networking module must exist to be called)
- T065 and T066 (stack live roots) depend on T060–T061 (composition modules must accept new vars)
- T067–T068 (common.sh) can proceed **in parallel** with Terraform tasks (different file type)
- T069 (deploy.sh) depends on T064 (shared outputs must include `integration_subnet_id`)
- T070–T072 (.env examples) can proceed **in parallel** with all other tasks (documentation files only)
- T073–T074 (documentation) depend on all Phase 10 implementation tasks being complete
- T075 (validation) must run last — after all Phase 10 and T073–T074 documentation tasks

### Within User Story 5

- T039 (table-names.ts) must complete before T042–T046 (repo files that import `T`)
- T040 (schema.sql) and T041 (db.ts) can proceed in parallel with T039 (no import dependency)
- T042–T046 (5 repo files) can all run in parallel after T039 (different files, no shared state)
- T047 (AppDbContext.cs), T048 (migration script), T049 (Terraform), T050 (delete test) are all independent and can run in parallel with each other and with T040–T046
- T051–T053 (documentation) can all run in parallel after all Phase 8 implementation tasks are done
- T054 (verification) must run after all Phase 8 and T051–T053 documentation tasks

### Parallel Opportunities

- Setup profile templates `T003` and `T004` can run in parallel after `T002`
- Foundational Terraform modules `T006` through `T011` can run in parallel after `T005`
- Stack root shells `T013` and `T014` can run in parallel after `T012`
- US1 composition tasks `T015`, `T016`, `T020`, and `T021` can run in parallel
- US3 profile and runtime wiring tasks `T027`, `T028`, `T030`, `T031`, and `T032` can run in parallel
- Polish tasks `T037` and `T038` can run in parallel
- **US5 repo migrations `T042`, `T043`, `T044`, `T045`, `T046` can all run in parallel** (different files, all depend only on T039)
- **US5 cross-stack tasks `T047`, `T048`, `T049`, `T050` can run in parallel** with each other and with repo migration tasks
- **US5 documentation `T051`, `T052`, `T053` can all run in parallel** after Phase 8 completion
- **US6 Wave 1: `T055`–`T057` (networking module) and `T058`–`T059` (app-service module) run in parallel** (different directories)
- **US6 Wave 1: `T067`–`T068` (common.sh) and `T070`–`T072` (.env examples) run in parallel** with Wave 1 Terraform tasks
- **US6 Wave 2: `T060` and `T061` (stack composition modules) run in parallel** after app-service module is updated
- **US6 Wave 2: `T062`–`T064` (shared live root) run in parallel** with stack composition after networking module exists
- **US6 Wave 3: `T065` and `T066` (stack live roots) run in parallel** after composition modules are updated
- **US6 Wave 3: `T069` (deploy.sh) runs after shared outputs are defined**
- **US6 Wave 4: `T073` and `T074` (documentation) run in parallel** after all Phase 10 implementation
- **US6 Wave 5: `T075` (validation) runs last**

---

## Parallel Example: User Story 1

```bash
# Parallel module and packaging work for single-stack deployment:
Task: T015 Implement the Stack A composition module in infra/terraform/modules/stack-a/
Task: T016 Implement the Stack B composition module in infra/terraform/modules/stack-b/
Task: T020 Create infra/scripts/package-stack-a.sh
Task: T021 Create infra/scripts/package-stack-b.sh
```

## Parallel Example: User Story 2

```bash
# Resolve targets first, then complete the single workflow in sequence:
Task: T023 Implement infra/scripts/resolve-targets.sh
Task: T024 Create trigger and selection logic in .github/workflows/selective-azure-deploy.yml
Task: T025 Add shared/package/deploy jobs to .github/workflows/selective-azure-deploy.yml
```

## Parallel Example: User Story 3

```bash
# Parallel configuration and runtime wiring work after the base flow exists:
Task: T027 Expand .env_qa.example
Task: T028 Expand .env_prod.example
Task: T030 Extend infra/terraform/live/shared/main.tf and outputs.tf
Task: T031 Wire Stack A runtime settings in infra/terraform/modules/stack-a/main.tf
Task: T032 Wire Stack B runtime settings in infra/terraform/modules/stack-b/main.tf
```

## Parallel Example: User Story 4

```bash
# Shared lifecycle hardening after the workflow and roots exist:
Task: T034 Implement infra/scripts/deprovision.sh protections
Task: T035 Finalize stable foundation outputs
```

## Parallel Example: User Story 5

```bash
# Wave 1 — Foundation (must complete first):
Task: T039 Create server/storage/table-names.ts (T() helper)

# Wave 2 — All repo files + independent tasks in parallel:
Task: T040 Modify server/storage/schema.sql (schema preamble + prefix)
Task: T041 Modify server/storage/db.ts (schema init in startup)
Task: T042 Modify server/storage/repos/application-repo.ts (~45 T() calls)
Task: T043 Modify server/storage/repos/job-repo.ts (~17 T() calls)
Task: T044 Modify server/storage/repos/audit-repo.ts (~15 T() calls)
Task: T045 Modify server/storage/repos/prompt-repo.ts (~12 T() calls)
Task: T046 Modify server/storage/repos/user-repo.ts (~10 T() calls)
Task: T047 Modify dotnet/.../AppDbContext.cs (HasDefaultSchema)
Task: T048 Create infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql
Task: T049 Modify infra/terraform/modules/foundation/sql/main.tf
Task: T050 Delete tests/unit/local-kv.test.ts

# Wave 3 — Documentation (after all Wave 2 complete):
Task: T051 Update infra/README.md
Task: T052 Update specs/006-selective-azure-deploy/quickstart.md
Task: T053 Update README.md (project root)

# Wave 4 — Verification (after docs):
Task: T054 Run quickstart.md US5 verification steps
```

## Parallel Example: User Story 6

```bash
# Wave 1 — Foundation modules + scripts (all in parallel, different files):
Task: T055 Create infra/terraform/modules/foundation/networking/variables.tf
Task: T056 Create infra/terraform/modules/foundation/networking/main.tf
Task: T057 Create infra/terraform/modules/foundation/networking/outputs.tf
Task: T058 Modify infra/terraform/modules/foundation/app-service/variables.tf
Task: T059 Modify infra/terraform/modules/foundation/app-service/main.tf
Task: T067 Modify infra/scripts/lib/common.sh (TF_VAR exports)
Task: T068 Modify infra/scripts/lib/common.sh (validation)
Task: T070 Modify .env_local.example
Task: T071 Modify .env_qa.example
Task: T072 Modify .env_prod.example

# Wave 2 — Composition + shared root (after Wave 1 modules):
Task: T060 Modify infra/terraform/modules/stack-a/ (variables.tf + main.tf)
Task: T061 Modify infra/terraform/modules/stack-b/ (variables.tf + main.tf)
Task: T062 Modify infra/terraform/live/shared/variables.tf
Task: T063 Modify infra/terraform/live/shared/main.tf
Task: T064 Modify infra/terraform/live/shared/outputs.tf

# Wave 3 — Stack live roots + deploy script (after Wave 2):
Task: T065 Modify infra/terraform/live/stack-a/ (variables.tf + main.tf)
Task: T066 Modify infra/terraform/live/stack-b/ (variables.tf + main.tf)
Task: T069 Modify infra/scripts/deploy.sh

# Wave 4 — Documentation (after all implementation):
Task: T073 Update infra/README.md
Task: T074 Update specs/006-selective-azure-deploy/quickstart.md

# Wave 5 — Validation (after docs):
Task: T075 Run terraform validate across all modified roots
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational prerequisites
3. Complete Phase 3: User Story 1
4. Validate `stack-a` and `stack-b` independently through `infra/scripts/deploy.sh`

### Incremental Delivery

1. Deliver US1 to establish Terraform/Bash selective deployment
2. Add US2 to automate selective deployment in one workflow
3. Add US3 to harden configuration and secrets handling
4. Add US4 to complete shared-only and safe deprovision behavior
5. Finish with US1–US4 documentation and quickstart validation
6. Add US5 to isolate all database tables under the `talentmatch` schema
7. Finish with US5 documentation, verification, and orphaned test cleanup
8. Add US6 to configure private network connectivity and IP restrictions
9. Finish with US6 documentation, Terraform validation, and quickstart updates

### Suggested MVP Scope

- Phase 1
- Phase 2
- Phase 3 (US1 only)

### US6 Execution Plan (Current Focus)

1. **Wave 1** (T055–T059 + T067–T068 + T070–T072): Create networking module, modify app-service module, extend common.sh, update .env examples — all in parallel (10 task-files, no overlaps)
2. **Wave 2** (T060–T064): Wire networking through stack composition modules and shared live root — depends on Wave 1 modules
3. **Wave 3** (T065–T066 + T069): Wire stack live roots and capture shared output in deploy.sh — depends on Wave 2
4. **Wave 4** (T073–T074): Documentation updates — after all implementation
5. **Wave 5** (T075): Terraform validate across all roots — final pass

**Estimated scope**: 21 new tasks (T055–T075), 3 new files (networking module), 14 modified files (app-service module, 2 stack composition modules, 3 live roots, common.sh, deploy.sh, 3 .env examples, infra README, quickstart.md), 0 application code changes

---

## Notes

- All deployment code and IaC tasks use Terraform and Bash only
- The workflow shape is intentionally a single file at `.github/workflows/selective-azure-deploy.yml`
- The Terraform live roots are intentionally `shared`, `stack-a`, and `stack-b`
- Reuse-aware modules must expose stable outputs whether a resource is created or reused
- Safe deprovisioning is intentionally handled by `infra/scripts/deprovision.sh`, not raw `terraform destroy`
- The `talentmatch` schema name is a fixed convention (not configurable) per spec assumption
- `T()` helper uses `isAzureSql` from `db.ts` — SQLite paths never see schema-qualified names
- `schema-sqlite.sql` is explicitly unchanged by US5 (SQLite has no schema concept)
- The `dotnet/src/Infrastructure/Persistence/ApplicationRepository.cs` does not exist — no raw SQL migration needed in the .NET stack beyond the `HasDefaultSchema` change
- The 2 `ExecuteSqlRaw` calls in `dotnet/src/Web.Server/Program.cs` target `__EFMigrationsHistory` (SQLite-only path) and need no schema change
- US6 is purely infrastructure — no application code changes needed (FR-029)
- All new US6 variables default to `null`/`""`/`[]`/`false` ensuring backward compatibility with T001–T054 deployments
- VNet is always reused (FR-025) — `reuse_vnet` controls whether the networking module is invoked at all
- Both stacks share one delegated integration subnet (FR-028) — subnet ID flows through shared root output
- No Private Endpoint creation — `AZ_SQL_PRIVATE_ENDPOINT_REUSE=true` means existing PE on VNet handles SQL connectivity
- CIDR validation enforced at two levels: Terraform variable validation (module-level) and Bash validation in common.sh (fail-fast before Terraform runs)
- `AZ_ALLOWED_IPS` is required when networking is configured — deploying without IP restrictions is not permitted (FR-032)
- The `integration_subnet_id` output flow mirrors the existing `app_service_plan_id` pattern: shared root → TF_VAR export in deploy.sh → stack root variable → composition module → foundation app-service
