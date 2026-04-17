# Tasks: Selective Azure Deployment

**Input**: Design documents from `/specs/006-selective-azure-deploy/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, quickstart.md, contracts/

**Tests**: No dedicated test tasks generated. The specification defines independent validation scenarios, but it does not request TDD or explicit test-first implementation tasks.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for traceability (`[US1]`, `[US2]`, `[US3]`, `[US4]`)
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

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup and blocks all user stories
- **User Story 1 (Phase 3)**: Depends on Foundational completion
- **User Story 2 (Phase 4)**: Depends on User Story 1 packaging and deployment scripts
- **User Story 3 (Phase 5)**: Depends on User Story 1 roots and scripts, then hardens User Story 2 workflow behavior
- **User Story 4 (Phase 6)**: Depends on User Story 1 live roots and User Story 2 workflow so shared-only and safe destroy paths can be completed
- **Polish (Phase 7)**: Depends on all desired user stories being complete

### User Story Dependencies

- **US1**: First deliverable and MVP after Phase 2
- **US2**: Depends on US1 because the workflow calls the Bash packaging and deployment entry points created there
- **US3**: Depends on US1 for Terraform roots and on US2 for workflow hardening
- **US4**: Depends on US1 for shared-root semantics and on US2 for `shared-only` workflow execution

### Within Each User Story

- Shared foundations before root composition
- Shared root before stack live roots
- Packaging scripts before workflow packaging/deploy jobs
- Environment contract and secret validation before workflow hardening
- Destroy protections after live-root output contracts are established

### Parallel Opportunities

- Setup profile templates `T003` and `T004` can run in parallel after `T002`
- Foundational Terraform modules `T006` through `T011` can run in parallel after `T005`
- Stack root shells `T013` and `T014` can run in parallel after `T012`
- US1 composition tasks `T015`, `T016`, `T020`, and `T021` can run in parallel
- US3 profile and runtime wiring tasks `T027`, `T028`, `T030`, `T031`, and `T032` can run in parallel
- Polish tasks `T037` and `T038` can run in parallel

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
5. Finish with documentation and quickstart validation

### Suggested MVP Scope

- Phase 1
- Phase 2
- Phase 3 (US1 only)

---

## Notes

- All deployment code and IaC tasks use Terraform and Bash only
- The workflow shape is intentionally a single file at `.github/workflows/selective-azure-deploy.yml`
- The Terraform live roots are intentionally `shared`, `stack-a`, and `stack-b`
- Reuse-aware modules must expose stable outputs whether a resource is created or reused
- Safe deprovisioning is intentionally handled by `infra/scripts/deprovision.sh`, not raw `terraform destroy`
