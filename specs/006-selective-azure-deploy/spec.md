# Feature Specification: Selective Azure Deployment

**Feature Branch**: `006-selective-azure-deploy`  
**Created**: 2026-04-14  
**Status**: Draft  
**Input**: User description: "Selectively deploy Stack A or Stack B to Azure"

## Background

The talent matching platform supports two technology stacks: Stack A (React/TypeScript/Express) and Stack B (.NET Blazor WASM / Clean Architecture). Both stacks must maintain feature parity per the project constitution (v1.1.0), but deployment needs vary — some environments may only need Stack A, others only Stack B, and some both.

Currently, there is no defined deployment pipeline or infrastructure-as-code for either stack. This feature establishes the ability to selectively deploy one or both stacks to Azure, along with the shared infrastructure they depend on (Azure SQL, Azure OpenAI via APIM), while maintaining independent lifecycle management for each stack.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Deploy a Single Stack to Azure (Priority: P1)

A developer or DevOps engineer needs to deploy just one stack (either Stack A or Stack B) to a target Azure environment. They select which stack to deploy, and the system provisions all required infrastructure (compute, shared services if not already present) and deploys the application. This is the foundation of the deployment capability.

**Why this priority**: Without the ability to deploy a single stack, no other deployment scenario is possible. This is the core capability that all other stories build upon.

**Independent Test**: Can be fully tested by selecting "Stack A only" (or "Stack B only"), triggering deployment, and verifying the application is running and accessible in Azure with all dependent services provisioned.

**Acceptance Scenarios**:

1. **Given** no Azure resources exist for the target environment, **When** a developer triggers deployment of Stack A only, **Then** all required Azure infrastructure (compute, shared services) is provisioned and Stack A is deployed and accessible via its public endpoint.
2. **Given** no Azure resources exist for the target environment, **When** a developer triggers deployment of Stack B only, **Then** all required Azure infrastructure (compute, shared services) is provisioned and Stack B is deployed and accessible via its public endpoint.
3. **Given** shared infrastructure (Azure SQL, APIM) already exists from a prior deployment, **When** a developer deploys the other stack, **Then** the new stack reuses the existing shared infrastructure without duplicating it.
4. **Given** a stack is already deployed, **When** a developer redeploys the same stack, **Then** the existing deployment is updated in place without downtime to shared services.
5. **Given** a deployment is in progress, **When** the infrastructure provisioning fails, **Then** the deployment stops with a clear error message identifying which resource failed and why.

---

### User Story 2 — Automated CI/CD Pipeline with Stack Selection (Priority: P2)

A team wants code changes merged to specific branches to automatically trigger deployment of the appropriate stack(s). The CI/CD pipeline determines which stack(s) changed and deploys only what's necessary. Manual pipeline triggers also allow selecting which stack(s) to deploy.

**Why this priority**: Automated deployment is essential for a sustainable development workflow but depends on the foundational deployment capability from Story 1.

**Independent Test**: Can be tested by pushing a code change to a Stack A source file, verifying only Stack A's pipeline runs, and confirming Stack B remains untouched. Can also be tested by manually triggering the pipeline with "Stack B only" selected.

**Acceptance Scenarios**:

1. **Given** the CI/CD pipeline is configured, **When** a developer pushes changes that only affect Stack A source files (`src/`, `server/`, `package.json`), **Then** only Stack A is built and deployed.
2. **Given** the CI/CD pipeline is configured, **When** a developer pushes changes that only affect Stack B source files (`dotnet/`), **Then** only Stack B is built and deployed.
3. **Given** the CI/CD pipeline is configured, **When** a developer pushes changes affecting both stacks, **Then** both stacks are built and deployed.
4. **Given** the CI/CD pipeline is configured, **When** a developer manually triggers the pipeline with a specific stack selection (A, B, or both), **Then** only the selected stack(s) are deployed regardless of which files changed.
5. **Given** a pipeline run is in progress for Stack A, **When** a separate change triggers Stack B deployment, **Then** both pipelines run independently without blocking each other.

---

### User Story 3 — Environment Configuration and Secrets Management (Priority: P2)

A developer or DevOps engineer needs to configure environment-specific settings (connection strings, API keys, feature flags) and secrets for each stack and each target environment (development, staging, production). Secrets must never appear in source code or build logs.

**Why this priority**: Secure configuration management is a prerequisite for any production deployment and is tightly coupled with Stories 1 and 2. It shares priority with Story 2 because neither automated deployment nor manual deployment is production-ready without it.

**Independent Test**: Can be tested by configuring secrets for a target environment and verifying the deployed application can access them at runtime, while confirming secrets never appear in pipeline logs or source code.

**Acceptance Scenarios**:

1. **Given** a stack deployment is configured, **When** the deployment runs, **Then** environment-specific configuration values (`STORAGE_PROVIDER`, connection strings, API endpoints) are injected into the deployed application without hardcoding.
2. **Given** secrets are stored in the secrets management system, **When** the CI/CD pipeline runs, **Then** secrets are available to the deployment process but are never printed in logs or artifacts.
3. **Given** multiple environments exist (development, staging, production), **When** the same stack is deployed to different environments, **Then** each environment uses its own isolated set of configuration values and secrets.
4. **Given** a shared secret (e.g., database connection string) is needed by both stacks, **When** both stacks are deployed to the same environment, **Then** both stacks reference the same shared secret without duplication.
5. **Given** a secret value needs to be rotated, **When** the secret is updated in the secrets management system, **Then** the next deployment picks up the new value without code changes.

---

### User Story 4 — Shared Infrastructure Provisioning (Priority: P3)

The platform's shared services — database and AI gateway — must be provisioned once and shared across whichever stacks are deployed. Shared infrastructure is deployed independently of any specific stack, and each stack's deployment references it.

**Why this priority**: Shared infrastructure is important for cost efficiency and consistency, but the core deployment stories (1 and 2) can initially provision resources inline. This story formalises the separation, which becomes critical as the number of environments and stacks grows.

**Independent Test**: Can be tested by provisioning shared infrastructure alone (without deploying either stack), then deploying Stack A and verifying it connects to the shared database and AI gateway endpoint.

**Acceptance Scenarios**:

1. **Given** no shared infrastructure exists, **When** shared infrastructure provisioning is triggered, **Then** the database, API gateway with AI backend, and any other shared services are created and configured.
2. **Given** shared infrastructure exists, **When** a stack deployment references it, **Then** the stack connects to the existing shared resources using outputs (e.g., connection strings, endpoints) from the shared infrastructure.
3. **Given** shared infrastructure exists, **When** shared infrastructure provisioning is re-triggered, **Then** it updates existing resources idempotently without data loss.
4. **Given** only Stack A is deployed, **When** Stack A is removed, **Then** shared infrastructure remains intact for future Stack B deployment.

---

### Edge Cases

- What happens when a deployment targets an environment where the other stack is already deployed? — The existing stack's deployment remains unaffected; only the targeted stack is provisioned or updated.
- What happens when shared infrastructure provisioning fails midway? — The deployment uses idempotent infrastructure-as-code, so re-running the provisioning completes the remaining resources without duplicating what was already created.
- What happens when a stack's source code hasn't changed but infrastructure definitions have? — The CI/CD pipeline detects infrastructure file changes and runs the provisioning step even if application code is unchanged.
- What happens when both stacks are deployed and one needs to be removed? — Removing one stack's compute resources does not affect the other stack or shared infrastructure.
- What happens when environment configuration references a secret that doesn't exist? — The deployment fails fast with a clear error identifying the missing secret before any resources are provisioned or updated.
- What happens when `STORAGE_PROVIDER` is set to `local` in a cloud environment? — The system operates with local KV storage; the shared database is provisioned but unused until `STORAGE_PROVIDER` is changed to `azuresql`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST support deploying Stack A independently to Azure with all required compute and networking resources.
- **FR-002**: System MUST support deploying Stack B independently to Azure with all required compute and networking resources.
- **FR-003**: System MUST support deploying both stacks simultaneously to the same Azure environment, sharing common infrastructure.
- **FR-004**: System MUST provision shared infrastructure (database, API gateway with AI backend) independently of either stack's deployment.
- **FR-005**: System MUST allow shared infrastructure to be deployed once and referenced by any stack deployed to the same environment.
- **FR-006**: System MUST provide infrastructure-as-code definitions for all Azure resources, enabling repeatable and version-controlled provisioning.
- **FR-007**: System MUST provide CI/CD pipeline configuration that detects which stack(s) were affected by code changes and deploys only those stacks.
- **FR-008**: System MUST support manual pipeline triggers with explicit stack selection (Stack A, Stack B, or both).
- **FR-009**: System MUST inject environment-specific configuration (`STORAGE_PROVIDER`, connection strings, API endpoints) into deployed applications without hardcoding values in source code.
- **FR-010**: System MUST store and manage secrets (API keys, connection strings, certificates) in a centralised secrets management system, never in source code or build logs.
- **FR-011**: System MUST support at least three isolated environments: development, staging, and production.
- **FR-012**: System MUST ensure all infrastructure provisioning is idempotent — re-running a deployment produces the same result without duplicating resources or losing data.
- **FR-013**: System MUST provide deployment outputs (endpoint URLs, resource identifiers) after successful provisioning for verification and integration.
- **FR-014**: System MUST fail fast with clear, actionable error messages when required secrets, configuration, or prerequisites are missing.
- **FR-015**: System MUST ensure removing one stack's deployment does not affect the other stack or shared infrastructure.
- **FR-016**: System MUST enforce that secrets are never exposed in pipeline logs, build artifacts, or client-side bundles (consistent with constitution Principle IV — no secrets prefixed `VITE_`).

### Key Entities

- **Deployment Target**: Represents a specific stack (A, B, or both) being deployed to a specific environment — includes stack selection, environment name, and configuration parameters.
- **Infrastructure Module**: A self-contained unit of infrastructure-as-code that provisions a specific set of Azure resources — categorised as "shared" (database, API gateway) or "stack-specific" (compute, networking per stack).
- **Environment Configuration**: A set of key-value pairs and secret references specific to one environment (dev, staging, production) — includes `STORAGE_PROVIDER`, connection strings, API endpoints, and feature flags.
- **Pipeline Workflow**: A CI/CD workflow definition that orchestrates build, infrastructure provisioning, and application deployment for one or more stacks — supports both automatic (push-triggered) and manual triggers.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can deploy Stack A to a new Azure environment within 15 minutes of triggering deployment, with the application accessible via its public endpoint.
- **SC-002**: A developer can deploy Stack B to a new Azure environment within 15 minutes of triggering deployment, with the application accessible via its public endpoint.
- **SC-003**: Deploying one stack does not trigger any build, test, or deployment steps for the other stack.
- **SC-004**: Re-running a deployment with no changes completes successfully with no resource duplication and no data loss.
- **SC-005**: All secrets used by deployed applications are retrievable only at runtime and never appear in pipeline logs, source code, or build artifacts.
- **SC-006**: A new environment (development, staging, or production) can be fully provisioned from scratch by running the infrastructure-as-code and deployment pipeline — no manual portal steps required.
- **SC-007**: Shared infrastructure (database, API gateway) is provisioned exactly once per environment, regardless of how many stacks are deployed.
- **SC-008**: 100% of Azure resources are defined in infrastructure-as-code — no manually created resources exist outside of IaC definitions.

## Assumptions

- An Azure subscription with appropriate permissions is available for deployment.
- The CI/CD platform is GitHub Actions, consistent with the repository being hosted on GitHub.
- Deployed cloud environments default to `azuresql` for `STORAGE_PROVIDER` unless explicitly overridden.
- Three environments — development, staging, and production — follow standard naming conventions for resource groups and resources.
- Shared infrastructure includes the database and API gateway (with AI backend). Additional shared services (e.g., SignalR, Blob Storage referenced in the constitution's system communication diagram) will be added as future features require them.
- The `main` branch targets production; feature branches or a `develop` branch target development/staging environments.
- Deployment authentication uses federated credentials (OIDC) to avoid long-lived secrets for pipeline authentication.

## Dependencies

- Azure subscription with appropriate role assignments for the deploying identity.
- GitHub repository with Actions enabled and federated credential trust configured with Azure.
- Azure OpenAI resource provisioned and accessible via the API gateway (gateway configuration is in-scope; AI model deployment may be a prerequisite).
- Existing application code for both stacks must build successfully before deployment can be tested end-to-end.

## Out of Scope

- Application code changes to support cloud deployment (e.g., adding health check endpoints, modifying startup logic) — those are implementation concerns for the planning phase.
- Custom domain names, DNS configuration, or SSL certificate management.
- Monitoring, alerting, or observability setup (application performance monitoring, log analytics) — should be a separate feature.
- Auto-scaling configuration beyond default hosting plan settings.
- Database schema migrations or seed data — handled by application startup or separate migration tooling.
- Multi-region deployment or disaster recovery.
- Cost optimisation or reserved instance planning.
- Azure SignalR, Blob Storage, or Service Bus provisioning — referenced in the system communication architecture but deferred until features requiring them are implemented.

## Appendix A — Terraform Resource Reuse PRD

The following PRD is incorporated in full as additional guidance for this feature. It captures enterprise resource-reuse constraints and operational safeguards that inform the Terraform-based implementation of selective Azure deployment.

# PRD: Azure Resource Reuse for AWR Client Infrastructure

> **Status**: Not Implemented  
> **Last updated**: 2026-02-13  
> **Owner**: Platform Team

---

## 1. Problem Statement

The AWR CV MAtch Client Terraform infrastructure deploys all Azure resources fresh in every environment. In many enterprise scenarios, shared resources (e.g., a central Log Analytics workspace, a shared APIM gateway, or a pre-existing Azure SQL Server) already exist in other resource groups or subscriptions. Re-provisioning duplicates wastes cost, creates governance friction, and blocks teams that must use centrally managed infrastructure.

## 2. Goal

Allow each environment (local dev, QA/staging, production) to **selectively reuse existing Azure resources** instead of creating new ones, controlled via simple `.env` files and without changing the Terraform module interface.

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement |
|----|-------------|
| FR-1 | Each reusable resource type has a boolean `*_REUSE` flag in the `.env` file. |
| FR-2 | When `*_REUSE=TRUE`, Terraform must **not** provision that resource; it must look up the existing resource by name and resource group using a `data` source. |
| FR-3 | When `*_REUSE=FALSE` (or not set), Terraform must provision the resource as before. |
| FR-4 | Module outputs remain identical regardless of whether the resource was created or reused, ensuring downstream modules (e.g., `app_host`, `functions_host`) work without modification. |
| FR-5 | Role assignments on shared resources are **skipped** when reusing (the shared-resource owner manages access). |
| FR-6 | Three environment-specific `.env` files control reuse: `.env_local` (local/dev), `.env_qa` (staging), `.env_prod` (production). |

### 3.2 Supported Reusable Resources

| .env Flag | Terraform Variable | Module | Data Source |
|-----------|-------------------|--------|-------------|
| `AZ_STORAGE_REUSE` | `reuse_storage` | `modules/storage` | `azurerm_storage_account` |
| `AZ_APPINSIGHTS_REUSE` | `reuse_appinsights` | `modules/application_insights` | `azurerm_application_insights` |
| `AZ_SERVICE_BUS_REUSE` | `reuse_service_bus` | `modules/service_bus` | `azurerm_servicebus_namespace` |
| `AZ_SQL_REUSE` | `reuse_sql` | `modules/sql` | `azurerm_mssql_server` + `azurerm_mssql_database` |
| `AZ_KEY_VAULT_REUSE` | `reuse_key_vault` | `modules/key_vault` | `azurerm_key_vault` |
| `AZ_APIM_REUSE` | `reuse_apim` | `modules/apim` | `azurerm_api_management` |
| `AZ_LOGANALYTICS_REUSE` | `reuse_loganalytics` | `modules/log_analytics` | `azurerm_log_analytics_workspace` |
| `AZ_IDENTITIES_REUSE` | `reuse_identities` | `modules/identities` | `azurerm_user_assigned_identity` (×2) |

### 3.3 Existing Resource Details

When a `*_REUSE` flag is `TRUE`, the corresponding resource details must be provided:

| Resource | Required .env Variables |
|----------|------------------------|
| Storage | `AZ_STORAGE_NAME`, `AZ_STORAGE_RG` |
| App Insights | `AZ_APPINSIGHTS_NAME`, `AZ_APPINSIGHTS_RG` |
| Service Bus | `AZ_SERVICE_BUS_NAME`, `AZ_SERVICE_BUS_RG` |
| SQL | `SQL_SERVER`, `SQL_DATABASE`, `SQL_RG` |
| Key Vault | `AZ_KEY_VAULT_NAME`, `AZ_KEY_VAULT_RG` |
| APIM | `AZ_APIM_NAME`, `AZ_APIM_RG` |
| Log Analytics | `AZ_LOGANALYTICS_NAME`, `AZ_LOGANALYTICS_RG` |
| Identities | `AZ_IDENTITIES_API_NAME`, `AZ_IDENTITIES_FUNC_NAME`, `AZ_IDENTITIES_RG` |

### 3.4 Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | Backward compatible — all `*_REUSE` flags default to `false`; existing deployments are unaffected. |
| NFR-2 | Existing resources may live in **any resource group** (not restricted to the deployment RG). |
| NFR-3 | The Terraform service principal must have **Reader** access to the resource groups containing reused resources. |

## 4. Architecture

### 4.1 Data Flow

```text
.env_local / .env_qa / .env_prod
				|
				v
 deploy.ps1 / deploy.sh          (wrapper script)
				|
				|  Reads AZ_*_REUSE and AZ_*_NAME/RG vars
				|  Exports TF_VAR_reuse_*, TF_VAR_existing_*
				|
				v
 terraform plan / apply
				|
				|-- module (reuse=false) --> resource block (count=1) --> creates resource
				|
				'-- module (reuse=true)  --> data source  (count=1)  --> reads existing resource
																		 resource block (count=0) --> skipped
```

### 4.2 Module Pattern

Every reusable module follows this pattern:

```hcl
# Input variables
variable "reuse"                   { type = bool, default = false }
variable "existing_name"           { type = string, default = "" }
variable "existing_resource_group" { type = string, default = "" }

# Conditional data source
data "azurerm_<resource>" "existing" {
	count               = var.reuse ? 1 : 0
	name                = var.existing_name
	resource_group_name = var.existing_resource_group
}

# Conditional resource creation
resource "azurerm_<resource>" "main" {
	count = var.reuse ? 0 : 1
	...
}

# Unified output
output "id" {
	value = var.reuse ? data.azurerm_<resource>.existing[0].id
										 : azurerm_<resource>.main[0].id
}
```

### 4.3 Environment Mapping

| .env File | Terraform Env | Purpose |
|-----------|---------------|---------|
| `.env_local` | `envs/dev` | Local development and testing |
| `.env_qa` | `envs/test` | QA / staging on Azure |
| `.env_prod` | `envs/prod` | Production on Azure |

## 5. Usage

### 5.1 Quick Start — Reuse SQL and Log Analytics in QA

Edit `.env_qa`:

```ini
AZ_SQL_REUSE=TRUE
SQL_SERVER=sql-shared-qa
SQL_DATABASE=awrdb
SQL_RG=rg-shared-platform

AZ_LOGANALYTICS_REUSE=TRUE
AZ_LOGANALYTICS_NAME=law-central-qa
AZ_LOGANALYTICS_RG=rg-observability
```

Deploy:

```powershell
# PowerShell (Windows)
.\infra\scripts\deploy.ps1 -EnvFile .env_qa -TfEnv test -Action plan

# Bash (Linux / CI)
./infra/scripts/deploy.sh .env_qa test plan
```

### 5.2 Deploy All Fresh (default)

Leave all `*_REUSE=FALSE` (or omit them) — identical to the original behavior.

### 5.3 Direct Terraform (without wrapper)

You can also set the variables directly in `terraform.tfvars`:

```hcl
reuse_sql                = true
existing_sql_server_name = "sql-shared"
existing_sql_db_name     = "awrdb"
existing_sql_rg          = "rg-shared"
```

## 6. Deprovisioning (Safe Destroy)

### 6.1 Overview

Deprovisioning tears down Terraform-managed infrastructure while **guaranteeing that reuse-flagged resources are never destroyed**. This is enforced at two levels:

1. **Terraform structural guarantee**: Reuse-flagged modules have `count = 0` on all managed resources, so nothing exists in Terraform state to destroy. Data sources referencing existing resources are read-only and are never deleted by `terraform destroy`.

2. **Script-level safeguards**: The dedicated deprovision scripts require an `.env` file (preventing accidental full destruction with default `reuse=false`), display a colour-coded table of `[DESTROY]` vs `[PROTECTED]` resources, and require explicit confirmation.

### 6.2 Dedicated Deprovision Scripts

| Script | Platform | Usage |
|--------|----------|-------|
| `infra/scripts/deprovision.ps1` | Windows / PowerShell | `.\infra\scripts\deprovision.ps1 -EnvFile .env_qa -TfEnv test` |
| `infra/scripts/deprovision.sh` | Linux / macOS / CI | `./infra/scripts/deprovision.sh .env_qa test` |

#### Parameters

| Parameter | PS1 Name | Bash Arg | Description |
|-----------|----------|----------|-------------|
| Env file | `-EnvFile` | `$1` | Path to `.env_local`, `.env_qa`, or `.env_prod`. **Required.** |
| TF env | `-TfEnv` | `$2` | Terraform environment: `dev`, `test`, or `prod`. **Required.** |
| Dry run | `-DryRun` | `--dry-run` | Run `terraform plan -destroy` without destroying anything. |
| Force | `-Force` | `--force` | Skip interactive confirmation (CI/CD pipelines only). |

#### Example: Dry run to see what would be destroyed

```powershell
.\infra\scripts\deprovision.ps1 -EnvFile .env_qa -TfEnv test -DryRun
```

#### Example: Destroy with confirmation prompt

```bash
./infra/scripts/deprovision.sh .env_prod prod
# Shows table, requires typing "prod" to confirm
```

#### Example: CI/CD force destroy

```bash
./infra/scripts/deprovision.sh .env_qa test --force
```

### 6.3 Safety Guarantees

| Risk | Mitigation |
|------|------------|
| Running `terraform destroy` directly (bypassing wrapper, all reuse defaults to `false`) | Deprovision scripts **require** an `.env` file; the script refuses to run without one |
| Accidentally destroying a reuse-flagged resource | Resources with `reuse=true` have `count=0`, so nothing is in Terraform state to destroy |
| No visibility into what will be destroyed | Scripts display colour-coded `[DESTROY]` / `[PROTECTED]` summary before any action |
| Fat-finger confirmation | Interactive mode requires typing the exact environment name (e.g., `prod`) to proceed |
| CI/CD pipeline accidentally destroys protected resources | Even with `--force`, TF_VAR_reuse_* flags are always set from the `.env` file |

### 6.4 Deploy Script Destroy Fallback

The `deploy.ps1` / `deploy.sh` scripts also support `Action = destroy`, but when invoked:

1. A warning banner recommends using the dedicated deprovision script instead.
2. A `[DESTROY]` / `[PROTECTED]` summary is shown inline.
3. Interactive confirmation (type the env name) is required unless `-AutoApprove` / `--auto-approve` is set.
4. All `TF_VAR_reuse_*` flags are set from the `.env` file, ensuring reused resources remain safe.

## 7. Security Considerations

| Concern | Mitigation |
|---------|------------|
| Cross-RG access | The deploying service principal needs **Reader** on the external RG to use `data` sources |
| Role assignments | Skipped for reused resources — the shared-resource owner manages RBAC |
| Secrets in .env files | `.env_*` files are git-ignored; secrets should be in Key Vault, not .env files |
| Drift detection | Terraform only reads (not manages) reused resources; drift is the external team's responsibility |

## 8. Files Changed

### New Files

| File | Purpose |
|------|---------|
| `.env_local` | Reuse config + runtime config for local dev |
| `.env_qa` | Reuse config + runtime config for QA/staging |
| `.env_prod` | Reuse config + runtime config for production |
| `infra/scripts/deploy.ps1` | PowerShell wrapper: .env → TF_VAR → terraform |
| `infra/scripts/deploy.sh` | Bash wrapper: .env → TF_VAR → terraform |
| `infra/scripts/deprovision.ps1` | PowerShell: safe destroy with reuse protection |
| `infra/scripts/deprovision.sh` | Bash: safe destroy with reuse protection |
| `PRD.md` | This document |

### Modified Terraform Modules (8)

Each module gained `reuse`, `existing_name`, `existing_resource_group` variables, a conditional `data` source, `count` guards on resources, and unified outputs:

- `modules/storage/main.tf`
- `modules/application_insights/main.tf`
- `modules/service_bus/main.tf`
- `modules/sql/main.tf`
- `modules/key_vault/main.tf`
- `modules/apim/main.tf`
- `modules/log_analytics/main.tf`
- `modules/identities/main.tf`

### Modified Environment Compositions (3 × 3 files)

- `envs/{dev,test,prod}/variables.tf` — added 8 reuse booleans + existing resource detail variables
- `envs/{dev,test,prod}/main.tf` — pass reuse flags to each module
- `envs/{dev,test,prod}/terraform.tfvars.example` — added example reuse configuration

## 9. Testing Plan

| Scenario | Expected Result |
|----------|-----------------|
| All `*_REUSE=FALSE` | All resources created fresh (no behavioral change from before) |
| Single resource reused (e.g., SQL) | SQL module uses data source; all other modules create fresh |
| Multiple resources reused | Each flagged module uses data source; others create fresh |
| `*_REUSE=TRUE` without name/RG | Terraform plan fails with clear error (data source requires name) |
| Wrapper script with invalid env file | Script exits with descriptive error |
| Deprovision with all `*_REUSE=FALSE` | All reusable resources + always-managed resources are destroyed |
| Deprovision with some `*_REUSE=TRUE` | Only non-reused resources destroyed; reused resources untouched |
| Deprovision without .env file | Script refuses to run with clear error message |
| Deprovision dry run (`-DryRun`) | Shows `terraform plan -destroy` output but destroys nothing |
| Deprovision confirmation mismatch | User types wrong env name → script aborts |
| Raw `terraform destroy` (no wrapper) | All resources destroyed (expected — no reuse flags set); documented as unsupported |
