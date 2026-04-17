# Research: Selective Azure Deployment

**Feature**: 006-selective-azure-deploy
**Date**: 2026-04-17
**Status**: Complete

## R-001: Azure Compute Hosting for Stack A and Stack B

### Decision
Use **Azure App Service** (Linux plan) for both stacks.

- **Stack A** (React/Express/Node 20+): App Service with Node 20 LTS runtime. The Express server serves Vite-built static files and API traffic from `node dist-server/index.js`.
- **Stack B** (.NET Blazor WASM hosted): App Service with .NET 10 runtime. `dotnet/src/Web.Server` remains the publish and deployment entry point.

### Rationale
- App Service supports both runtime stacks without adding container orchestration.
- Both stacks remain server-hosted web apps, so Container Apps and AKS add complexity with no feature benefit.
- App Service supports managed identity, app settings, Key Vault references, and straightforward GitHub Actions deployment.

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Azure Container Apps | Requires containerization and adds operational overhead not justified by the feature scope |
| Azure Static Web Apps | Cannot host the Express or ASP.NET Core server components |
| Azure Functions | Would require restructuring both stacks into function-shaped workloads |
| AKS | Violates the plan's simplicity constraint |

### SKU Recommendations
| Environment | SKU | Rationale |
|-------------|-----|-----------|
| Development | B1 | Lowest-cost baseline for operator validation |
| Staging | B2 | Higher memory and CPU for integration testing |
| Production | S1 or P1V3 | Supports higher availability and scale needs |

---

## R-002: Infrastructure as Code and Operator Interface

### Decision
Use **Terraform plus Bash only** for deployment assets.

### Rationale
- Terraform and Bash are mandated by the constitution.
- The spec now requires create-or-reuse behavior, environment-backed safeguards, and explicit deprovision flows. Terraform roots plus Bash wrappers express that more cleanly than raw CLI commands.
- A single operator interface based on `.env_local`, `.env_qa`, and `.env_prod` keeps local and CI usage aligned.

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Bicep | Conflicts with the constitution and with the appended Terraform reuse PRD |
| Raw Terraform commands only | Too easy to omit required reuse flags or run unsafe destroy operations |
| PowerShell-first scripts | The constitution requires Bash for deployment automation |

---

## R-003: Terraform State and Module Topology

### Decision
Use **three Terraform live roots** under `infra/terraform/live`: `shared`, `stack-a`, and `stack-b`.

### Rationale
- Shared infrastructure must outlive either stack.
- Selective deploy and selective destroy are safer when stack state is isolated.
- Reused resources must remain data-only and cannot appear in a state root that might later be destroyed.

### Implications
- `live/shared` owns shared resources and outputs consumed by both stacks.
- `live/stack-a` and `live/stack-b` own only stack-specific app host configuration and deployment-time settings.
- Reused resources are looked up via `data` sources and must not be imported into state.

---

## R-004: CI/CD Workflow Architecture

### Decision
Use **one GitHub Actions workflow** at `.github/workflows/selective-azure-deploy.yml` with a Bash-based target-resolution step.

### Workflow Structure
```text
.github/workflows/
└── selective-azure-deploy.yml       # Push + workflow_dispatch entry point

infra/scripts/
├── resolve-targets.sh               # Resolves shared/stack-a/stack-b deployment booleans
├── deploy.sh                        # Loads .env and runs terraform plan/apply
├── deprovision.sh                   # Loads .env and runs protected destroy flow
├── package-stack-a.sh               # Builds and packages Stack A
└── package-stack-b.sh               # Publishes and packages Stack B
```

### Change Detection
| Scope | Paths |
|-------|-------|
| `shared` | `infra/**`, `.github/workflows/**`, `.env_*.example` |
| `stack_a` | `src/**`, `server/**`, `package.json`, `package-lock.json`, `vite.config.ts`, `tsconfig*.json`, `tailwind.config.js` |
| `stack_b` | `dotnet/**` |

### Trigger Behavior
| Trigger | Default Behavior |
|---------|------------------|
| Push to `main` | Resolve `production`, detect changed scopes, deploy only affected targets |
| Push to `develop` | Resolve `staging`, detect changed scopes, deploy only affected targets |
| `workflow_dispatch` | User selects `shared-only`, `stack-a`, `stack-b`, or `both`; manual choice overrides path detection |

### Rationale
- One workflow matches the plan and removes contract drift.
- Bash target resolution keeps local and CI behavior consistent.
- Shared infrastructure can be applied first without requiring multiple reusable workflows.

---

## R-005: Secrets, Reuse, and Ownership Boundaries

### Decision
Use **GitHub OIDC for pipeline authentication**, **Azure Key Vault for application secrets**, and **Terraform create-or-reuse modules** for supported shared resources.

### Architecture
```text
GitHub Environment secrets
    |
    v
azure/login@v2
    |
    v
selective-azure-deploy.yml
    |
    +--> infra/scripts/deploy.sh --> terraform live/shared
    |                                (create or lookup shared resources)
    |
    +--> package-stack-a.sh / package-stack-b.sh
    |
    +--> stack roots consume stable outputs from shared
```

### Reuse Rules
- Each reusable module accepts a `reuse` boolean and existing resource coordinates.
- When reuse is enabled, the module must look up the resource via `data` sources and still emit the same outputs.
- Role assignments and ownership-changing actions are skipped for reused shared resources.
- Reused resources may live in different resource groups, so the deploying identity needs at least `Reader` on those external groups.

### Constitution Compliance
- No `VITE_` secrets or client-exposed credentials.
- No long-lived deployment secrets.
- Shared AI access still remains behind API Management.

---

## R-006: Environment Profiles and Naming

### Decision
Use **environment-specific `.env` profiles** as the deployment contract.

### Profile Mapping
| Env File | Terraform Environment Key | GitHub Environment |
|----------|---------------------------|--------------------|
| `.env_local` | `dev` | `development` |
| `.env_qa` | `test` | `staging` |
| `.env_prod` | `prod` | `production` |

### Naming Convention
| Resource | Pattern |
|----------|---------|
| Resource Group | `rg-talentmatch-{env}` |
| App Service Plan | `plan-talentmatch-{env}` |
| App Service (Stack A) | `app-talentmatch-node-{env}` |
| App Service (Stack B) | `app-talentmatch-blazor-{env}` |
| SQL Server | `sql-talentmatch-{env}` |
| SQL Database | `sqldb-talentmatch-{env}` |
| API Management | `apim-talentmatch-{env}` |
| Key Vault | `kv-talentmatch-{env}` |
| Managed Identity (A) | `id-talentmatch-node-{env}` |
| Managed Identity (B) | `id-talentmatch-blazor-{env}` |

Where `{env}` is `dev`, `test`, or `prod` inside Terraform and maps to `development`, `staging`, or `production` in GitHub environments.

---

## R-007: Safe Deprovisioning

### Decision
Use **a dedicated Bash deprovision wrapper** instead of raw `terraform destroy`.

### Rationale
- The spec appendix requires reuse-flagged resources to remain protected.
- Operators need a visible summary of destroyable versus protected resources.
- Stack-only teardown must remain the default path so shared infrastructure is not destroyed accidentally.

### Required Behavior
- `deprovision.sh` requires an explicit `.env_*` file.
- The script prints a `[DESTROY]` and `[PROTECTED]` summary before acting.
- Destroying `live/shared` is a separate action from destroying `live/stack-a` or `live/stack-b`.