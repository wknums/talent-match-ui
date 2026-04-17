# Implementation Plan: Selective Azure Deployment

**Branch**: `006-selective-azure-deploy` | **Date**: 2026-04-16 | **Spec**: `specs/006-selective-azure-deploy/spec.md`
**Input**: Feature specification from `/specs/006-selective-azure-deploy/spec.md`

## Summary

Enable selective Azure deployment for Stack A (`src/` + `server/`) and Stack B (`dotnet/`) using Terraform and Bash only, with one GitHub Actions workflow that can deploy shared infrastructure, Stack A, Stack B, or both. The revised plan now treats resource reuse as a first-class requirement: each environment loads a dedicated `.env` profile, Terraform modules can either create or look up supported Azure resources, outputs stay stable in both paths, reused shared resources are treated as externally owned, and deprovisioning defaults to protecting anything marked for reuse.

## Technical Context

**Language/Version**: Bash for deployment automation; Terraform 1.9+ for IaC; GitHub Actions YAML for CI/CD; TypeScript 5.7 / Node 20+ for Stack A; C# / .NET 10 for Stack B  
**Primary Dependencies**: Terraform `azurerm` provider; Azure CLI for bootstrap and smoke checks; GitHub Actions `azure/login@v2`, `hashicorp/setup-terraform`, and changed-path detection; existing Stack A build scripts in `package.json`; existing Stack B publish/test commands rooted at `dotnet/src/Web.Server/TalentMatch.Web.Server.csproj`  
**Storage**: Shared SQLite in local development; Azure SQL in cloud. Stack A cloud config remains `STORAGE_PROVIDER=azuresql` + `AZURE_SQL_CONNECTION_STRING`; Stack B remains `DatabaseProvider=sqlserver` + `ConnectionStrings__DefaultConnection`  
**Testing**: `npm run build`, `npm run build:server`, `npm test`; `dotnet test dotnet/TalentMatch.slnx`; `dotnet publish dotnet/src/Web.Server/TalentMatch.Web.Server.csproj`; `terraform fmt -check`, `terraform validate`, `terraform plan`; Bash wrapper dry runs for deploy and deprovision  
**Target Platform**: GitHub-hosted Linux runners deploying to Azure App Service, Azure SQL, API Management, Key Vault, and supporting shared Azure resources across development, staging, and production environments; reused resources may live in different resource groups in the same subscription  
**Project Type**: Dual-stack web application with new Terraform, Bash deployment wrappers, and GitHub Actions orchestration  
**Performance Goals**: Fresh environment provisioning plus one-stack deployment within the 15-minute success criteria from the spec; unchanged stacks must not be built or deployed on selective runs; reusing shared resources must remove duplicate provisioning from the critical path  
**Constraints**: Constitution v1.1.1 requires Terraform and Bash for all deployment code and IaC; secrets must never appear in source, logs, artifacts, or client bundles; environment isolation is required for development, staging, and production; reused resources must be read-only from Terraform's perspective; shared resource role assignments must not be managed when the resource is reused; destroy flows must not remove reuse-flagged resources; Windows Git Bash compatibility still matters for Azure CLI usage  
**Scale/Scope**: Two independently deployable stacks, three isolated environments, one shared infrastructure set per environment, one workflow for selective CI/CD, one Bash-driven operator interface for local/CI deploy and safe deprovision, and appendix-defined reuse flags for enterprise environments

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable | ✅ PASS | Terraform plans, state, workflow runs, and explicit Bash entry points create an auditable deployment trail; no portal-only target state is assumed. |
| II | Layered Architecture | ✅ PASS | The plan adds deployment assets around the existing stacks without moving application behavior out of `server/`, `src/`, or `dotnet/src/`. |
| III | Storage Abstraction | ✅ PASS | Cloud deployment still uses each stack's existing storage switches rather than wiring business code directly to Azure SQL. |
| IV | Security Defaults | ✅ PASS | OIDC remains the pipeline auth model, secrets live in Key Vault, reused shared resources are treated as externally governed, and no `VITE_` secrets are introduced. |
| V | LLM Integration Discipline | ✅ PASS | Shared infra still includes API Management because the constitution requires the AI boundary to stay behind the gateway. |
| VII | Simplicity & YAGNI | ✅ PASS | The plan keeps one workflow, Bash wrappers, and three Terraform state roots. It does not expand into containers, AKS, or unrelated Azure services just because Appendix A describes a generic reuse pattern. |
| VIII | System Communication Architecture | ✅ PASS | Shared infra remains centered on Azure SQL and APIM, which matches the platform boundary in the constitution. |
| IX | Clean Architecture (.NET) | ✅ PASS | Stack B deployment targets `dotnet/src/Web.Server` publish output only and does not alter the Clean Architecture boundaries. |

**GATE RESULT**: ✅ ALL PASS - proceed with implementation planning.

## Project Structure

### Documentation (this feature)

```text
specs/006-selective-azure-deploy/
├── plan.md              # This file
├── research.md          # Phase 0 research output
├── data-model.md        # Phase 1 data/resource model
├── quickstart.md        # Phase 1 operator quickstart
├── contracts/           # Phase 1 interface contracts
└── tasks.md             # Phase 2 output (not edited here)
```

### Source Code (repository root)

```text
.github/
└── workflows/
    └── selective-azure-deploy.yml      # NEW: push + workflow_dispatch selective deploy

infra/
├── scripts/
│   ├── deploy.sh                       # NEW: .env -> TF_VAR -> terraform plan/apply wrapper
│   ├── deprovision.sh                  # NEW: safe destroy wrapper with reuse protection
│   ├── resolve-targets.sh              # NEW: merges manual stack selection with changed-path detection
│   ├── package-stack-a.sh              # NEW: build/package Stack A for App Service deployment
│   └── package-stack-b.sh              # NEW: publish/package Stack B for App Service deployment
└── terraform/
    ├── modules/
    │   ├── foundation/
    │   │   ├── app-service-plan/       # NEW: shared compute plan when managed here
    │   │   ├── sql/                    # NEW: create-or-reuse Azure SQL server/database
    │   │   ├── key-vault/              # NEW: create-or-reuse Key Vault
    │   │   ├── apim/                   # NEW: create-or-reuse API Management gateway
    │   │   ├── identities/             # NEW: create-or-reuse user-assigned identities
    │   │   └── app-service/            # NEW: shared app-host building block for both stacks
    │   ├── stack-a/                    # NEW: Stack A app settings and host composition
    │   └── stack-b/                    # NEW: Stack B app settings and host composition
    └── live/
        ├── shared/                     # NEW: separate state for shared infra lifecycle
        ├── stack-a/                    # NEW: separate state for Stack A lifecycle
        └── stack-b/                    # NEW: separate state for Stack B lifecycle

.env_local.example                      # NEW: local/dev reuse and deployment variables
.env_qa.example                         # NEW: staging reuse and deployment variables
.env_prod.example                       # NEW: production reuse and deployment variables
.env.example                            # EXISTING: application runtime variables
package.json                            # EXISTING: Stack A build/start entry points
server/
├── index.ts                            # EXISTING: Stack A runtime entry point
├── services/
│   └── awr-auth.ts                     # EXISTING: Stack A cloud auth settings
└── storage/
    ├── factory.ts                      # EXISTING: `STORAGE_PROVIDER` switch
    └── azure-sql.ts                    # EXISTING: Azure SQL contract for Stack A

dotnet/
└── src/
    ├── Infrastructure/
    │   ├── DependencyInjection.cs      # EXISTING: Stack B database provider wiring
    │   └── Services/
    │       └── AwrAuthHandler.cs       # EXISTING: Stack B cloud auth settings
    └── Web.Server/
        ├── Program.cs                  # EXISTING: server host and endpoint surface
        └── TalentMatch.Web.Server.csproj
                                         # EXISTING: Stack B publish/deploy entry point
```

**Structure Decision**: Keep deployment assets in `infra/` and leave application code in place. Use three Terraform live roots (`shared`, `stack-a`, `stack-b`) so that shared infra can outlive either stack and stack-specific destroy/apply operations do not accidentally mutate unrelated resources. Use Bash wrappers plus `.env_local`, `.env_qa`, and `.env_prod` as the operator interface rather than a second environment-management system. Track example env files in git and ignore the concrete `.env_*` files used in CI or local execution.

## Deployment Model

| Concern | Plan |
|---------|------|
| Environment inputs | `deploy.sh` and `deprovision.sh` load `.env_local`, `.env_qa`, or `.env_prod`, map them to Terraform environment keys (`dev`, `test`, `prod`), and export `TF_VAR_*` inputs for stack selection, naming, reuse flags, and existing resource coordinates. GitHub environments remain `development`, `staging`, and `production`; the wrapper performs the naming translation. |
| State separation | `infra/terraform/live/shared` owns shared resources and shared outputs. `infra/terraform/live/stack-a` and `infra/terraform/live/stack-b` own only their respective app hosts and deployment-specific settings. |
| Shared resources in current scope | Resource group, App Service plan, Azure SQL, Key Vault, API Management, and user-assigned identities are in scope because they are required by the current spec and repo layout. |
| Appendix A reuse flags | The Bash/Terraform interface adopts the appendix-defined `*_REUSE` pattern. The first implementation wires the resources needed by this feature now and preserves the same naming convention for later resource types so the operator contract does not churn. |
| Output contract | Every reusable module must emit the same outputs regardless of whether it created or reused the resource. Downstream roots and scripts consume stable outputs such as resource IDs, hostnames, secret URIs, managed identity IDs, and gateway/database endpoints without branching on ownership. |
| Workflow orchestration | One workflow resolves the target environment and stack selection, ensures shared infra is planned/applied before stack-specific deploys when needed, then packages and deploys only the requested stack artifacts. |

## Reuse And Deprovision Rules

| Concern | Plan |
|---------|------|
| Selective reuse | Each reusable Terraform module uses the same create-or-lookup pattern: `reuse=false` creates the resource, `reuse=true` switches to a `data` source lookup using explicit name and resource-group inputs from the `.env_*` file. |
| Per-environment reuse configuration | `.env_local`, `.env_qa`, and `.env_prod` each carry their own reuse booleans and required existing-resource coordinates. This allows, for example, QA to reuse a central SQL server and APIM while local/dev creates fresh shared resources. |
| Consistent outputs | Module outputs must always resolve to the active resource ID, name, URI, and hostname from either `resource` or `data` blocks. Neither `live/shared` nor stack roots may need special-case logic for reused resources. |
| RBAC ownership boundary | Role assignments, access policies, and other ownership-changing actions are created only for resources managed by this feature's Terraform state. When a shared resource is reused, Terraform must skip those ownership changes entirely and assume the external owner has already granted the required access. |
| Cross-resource-group implications | Reused resources may live outside the deployment resource group. The `.env_*` contract therefore requires both resource name and resource group for every reused dependency, and the deploying identity must have at least `Reader` on those external resource groups. |
| Destroy protections | Reused resources must never appear as managed Terraform resources, only as `data` sources. `deprovision.sh` must require an explicit `.env_*` file, display a `[DESTROY]` versus `[PROTECTED]` summary, and make stack-only teardown the default path. Destroying the shared root remains a separate, explicitly confirmed action. |
| Safe stack removal | Removing Stack A or Stack B only destroys that stack's Terraform state root and compute resources. Shared infrastructure is not touched unless a dedicated shared deprovision action is invoked. |

## Workflow Strategy

| Trigger | Behavior |
|---------|----------|
| `push` to deployment branches | Detect changes in `src/**`, `server/**`, `package.json`, `vite.config.ts`, `tsconfig*.json`, `tailwind.config.js`, `dotnet/**`, `infra/**`, `.env_*.example`, and `.github/workflows/**`, then resolve `shared`, `stack-a`, `stack-b`, or both. |
| `workflow_dispatch` | Accept explicit environment and target selection: `shared-only`, `stack-a`, `stack-b`, or `both`. Manual selection overrides path detection. |
| Shared-first sequencing | When shared infra needs to change, the workflow plans/applies `live/shared` first, then packages and deploys Stack A and/or Stack B against the resulting outputs. |
| Artifact packaging | Stack A uses `npm run build` + `npm run build:server`; Stack B uses `dotnet publish dotnet/src/Web.Server/TalentMatch.Web.Server.csproj`; deployment wrappers push those outputs to their app hosts only when the corresponding stack is selected. |

## Complexity Tracking

> No constitution violations require justification.

| Decision | Why Needed | Simpler Alternative Rejected Because |
|----------|------------|--------------------------------------|
| Separate Terraform roots and state for `shared`, `stack-a`, and `stack-b` | FR-004, FR-005, FR-015, and Appendix A's destroy-safety rules require independent lifecycle management | A single conditional root would make omission of one target look like desired deletion and would couple stack removal to shared infra risk |
| `.env_*` plus Bash wrappers as the deployment interface | The revised spec explicitly introduces per-environment reuse configuration and safe Bash-driven operation | Relying only on raw `terraform` commands or scattered tfvars files would make reuse flags easier to omit and destroy flows easier to misuse |
| Stable create-or-reuse module outputs | Downstream roots and scripts must behave identically whether a dependency is created or reused | Branching every consumer on `reuse=true` would spread ownership logic across the codebase and make drift harder to reason about |
| Skipping RBAC changes on reused shared resources | Appendix A makes shared-resource ownership external when reuse is enabled | Attempting to manage role assignments on reused resources would overstep ownership boundaries and fail in centrally governed environments |
| Dedicated deprovision flow with explicit protection summary | The revised spec adds safe destroy requirements for reused resources and for shared infra persistence | Telling operators to run raw `terraform destroy` would bypass the very safeguards the feature is supposed to provide |
