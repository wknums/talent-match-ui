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

---

## R-008: Schema Qualification Strategy for Node.js Raw SQL (US5)

### Decision
Use a **centralized `T()` helper function** in a new `server/storage/table-names.ts` module that returns schema-qualified table names for Azure SQL and unqualified names for SQLite.

### Rationale
- There are ~88 raw SQL queries across 5 repository files (`application-repo.ts`, `job-repo.ts`, `audit-repo.ts`, `prompt-repo.ts`, `user-repo.ts`) that reference 15 tables by hardcoded name.
- The existing `adaptSqlForSqlite()` function in `db.ts` translates SQL Server syntax to SQLite but operates on complete query strings — not individual table references. Inserting schema stripping into it would require regex-based schema removal which is fragile and error-prone.
- A `T('TableName')` helper gives the developer explicit control at the call site, satisfies FR-018 (centralized qualification) and SC-011 (one location per table), and composes cleanly with template literals already used in all repos.

### Implementation Pattern
```typescript
import { isAzureSql } from './db.js'
const SCHEMA = 'talentmatch'
export function T(tableName: string): string {
  return isAzureSql ? `[${SCHEMA}].[${tableName}]` : tableName
}
```

Usage in repos:
```typescript
// Before
await pool.request().query(`SELECT * FROM Users WHERE Id = @id`)
// After
await pool.request().query(`SELECT * FROM ${T('Users')} WHERE Id = @id`)
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| String-replace in `adaptSqlForSqlite` to strip schema prefixes | Fragile regex; would require hardcoding `talentmatch.` in every query first, then stripping it — double the work |
| Table name constants object (e.g., `TABLES.Users`) | Adds an indirection layer with no benefit over `T()` since the function already centralizes the logic |
| Separate SQL files per provider | Would duplicate ~88 queries and violate DRY; maintenance nightmare |
| ORM/query builder for Node.js | Massive scope change not justified by the current requirement (YAGNI) |

---

## R-009: Schema Qualification Strategy for .NET EF Core (US5)

### Decision
Use **`modelBuilder.HasDefaultSchema("talentmatch")`** conditionally when SQL Server is the active provider.

### Rationale
- EF Core's `HasDefaultSchema` natively prefixes all mapped table names with the specified schema in generated SQL — this is the canonical EF Core approach.
- The 15 entity types all use default table naming conventions (DbSet property names), so a single call qualifies everything.
- SQLite has no schema concept; `HasDefaultSchema` on SQLite would cause errors or be silently ignored depending on the provider. The conditional `Database.IsSqlServer()` guard prevents this.
- The 2 `ExecuteSqlRaw` calls in `Program.cs` are already gated by `db.Database.IsSqlite()` and reference only `__EFMigrationsHistory` (EF internal table), so they need no schema change.

### Implementation
```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    if (Database.IsSqlServer())
        modelBuilder.HasDefaultSchema("talentmatch");
    // ... existing entity configurations
}
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Per-entity `.ToTable("TableName", "talentmatch")` | Would require 15 individual calls instead of 1; violates SC-011 |
| Separate DbContext subclasses per provider | Over-engineered for a single schema line; YAGNI |
| EF Core migration to move tables | EF migrations cannot `ALTER SCHEMA TRANSFER`; the native SQL script is more appropriate |

---

## R-010: dbo→talentmatch Migration Strategy (US5)

### Decision
Use a **standalone idempotent SQL script** (`infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql`) that uses `ALTER SCHEMA [talentmatch] TRANSFER [dbo].[TableName]` for each table.

### Rationale
- `ALTER SCHEMA TRANSFER` is SQL Server's native mechanism for moving tables between schemas. It is a metadata-only operation — no data is copied, moved, or reformatted.
- The operation is fast regardless of table size since it only updates system catalog entries.
- Foreign key relationships, indexes, and constraints travel with the table automatically.
- The script must be idempotent: it checks whether each table exists in `dbo` before attempting transfer, and skips tables already in `talentmatch`.

### Migration Script Structure
```sql
-- 1. Ensure target schema exists
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch')
    EXEC('CREATE SCHEMA [talentmatch]');

-- 2. Transfer each table (idempotent per-table)
-- Example for one table:
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'Users' AND schema_id = SCHEMA_ID('dbo'))
    AND NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users' AND schema_id = SCHEMA_ID('talentmatch'))
    ALTER SCHEMA [talentmatch] TRANSFER [dbo].[Users];
-- ... repeated for all 15 tables
```

### Table Transfer Order
Tables with foreign keys must be transferred in dependency-safe order. Since `ALTER SCHEMA TRANSFER` moves the table reference (not data), and FK constraints follow the table, all 15 tables can be transferred in any order. However, for clarity the script follows the creation order from `schema.sql`:

1. Users, 2. PasswordResetRequests, 3. Jobs, 4. JobConfigVersions, 5. Applications, 6. ApplicationDocuments, 7. DocumentBlobs, 8. ExtractionArtifacts, 9. ScoringRuns, 10. AggregatedResults, 11. ManualReviews, 12. ScoringPrompts, 13. PromptTestRuns, 14. FailureQueueItems, 15. ProcessingEvents

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| `SELECT INTO` + `DROP` pattern | Copies data, loses constraints, requires rebuilding indexes and FKs — high risk, slow on large tables |
| EF Core migration with `Sql()` | Would couple the migration to the .NET deployment pipeline; the script should be runnable independently |
| Rename tables with schema prefix in name | Not a real schema; would not achieve enterprise isolation goals |

---

## R-011: Schema Creation at Database Provisioning Time (US5)

### Decision
Add schema creation to **two locations**: (1) the Terraform SQL module provisioner for fresh databases, and (2) the `initializeDatabase()` function in `server/storage/db.ts` for application startup.

### Rationale
- FR-021 requires the schema to exist before any table creation or query execution.
- Fresh Terraform-provisioned databases should have the schema created as part of infrastructure setup.
- Application startup (`initializeDatabase()`) should also ensure the schema exists as a safety net — this handles cases where the app starts before Terraform runs or when using a reused database.
- Both paths use the same idempotent SQL: `IF NOT EXISTS ... CREATE SCHEMA`.

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Only in Terraform | Application startup would fail if schema doesn't exist yet |
| Only in application code | Terraform should establish database structure as part of IaC |
| Separate schema management tool | Over-engineered for a single `CREATE SCHEMA` command (YAGNI) |

---

## R-012: VNet Integration for App Service (US6)

### Decision
Use the `virtual_network_subnet_id` attribute on `azurerm_linux_web_app` to configure **regional VNet Integration** for both App Services.

### Rationale
- Azure App Service regional VNet Integration routes all outbound traffic from the app through the specified delegated subnet on the VNet. This is a single attribute on the `azurerm_linux_web_app` resource — no separate resource block is needed.
- The subnet must be delegated to `Microsoft.Web/serverFarms` to allow App Service VNet Integration. Multiple App Services on the same App Service Plan can share one delegated subnet.
- Once VNet-integrated, the App Service can resolve Azure SQL Private Endpoint hostnames via the existing Private DNS zone (`privatelink.database.windows.net`) linked to the VNet. No application code changes or connection string changes are needed — the DNS resolution transparently routes to the private IP.
- The `virtual_network_subnet_id` attribute is idempotent — reapplying the same value on redeployment is a no-op.

### Implementation Pattern
```hcl
resource "azurerm_linux_web_app" "main" {
  # ... existing attributes ...
  virtual_network_subnet_id = var.virtual_network_subnet_id  # NEW

  site_config {
    # ... existing ...
    vnet_route_all_enabled = true  # Route ALL outbound traffic through VNet (not just RFC1918)
  }
}
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Azure Private Endpoint for App Service (inbound) | Not needed — App Services must remain publicly accessible (with IP restrictions); VNet Integration is outbound only |
| `azurerm_app_service_virtual_network_swift_connection` | Deprecated in favor of inline `virtual_network_subnet_id` attribute on the web app resource |
| Separate VNet per App Service | Violates FR-028 (both stacks share same subnet) and wastes address space |

---

## R-013: IP Access Restrictions on App Service (US6)

### Decision
Use **`ip_restriction` blocks inside the `site_config` block** of `azurerm_linux_web_app` combined with `ip_restriction_default_action = "Deny"` to enforce allow-list-based access control.

### Rationale
- The `azurerm_linux_web_app` resource supports `ip_restriction` blocks directly in `site_config`. Each block specifies an `ip_address` (CIDR notation), `action = "Allow"`, `priority`, and a `name`.
- Setting `ip_restriction_default_action = "Deny"` ensures all traffic not matching an explicit Allow rule is blocked with HTTP 403.
- The `AZ_ALLOWED_IPS` environment variable provides a comma-separated list of IPs. The Bash wrapper converts these to Terraform's `list(object)` format via `TF_VAR_allowed_ips`.
- IP restrictions in Azure App Service are evaluated by the platform before the request reaches the application — there is no application-level middleware needed.

### Implementation Pattern
```hcl
resource "azurerm_linux_web_app" "main" {
  site_config {
    ip_restriction_default_action = "Deny"

    dynamic "ip_restriction" {
      for_each = var.allowed_ips
      content {
        ip_address = "${ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100 + ip_restriction.key
        name       = "AllowIP-${ip_restriction.key}"
      }
    }
  }
}
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Azure Front Door / WAF | Over-engineered for simple IP allow-listing; adds cost and complexity (YAGNI) |
| NSG on the integration subnet | NSGs on delegated App Service subnets control outbound, not inbound App Service traffic |
| Application-level IP middleware | Not platform-enforced; must be replicated across both stacks; less secure |

---

## R-014: Networking Module Architecture and Placement (US6)

### Decision
Create a new **`modules/foundation/networking/`** module called from `live/shared/` that handles VNet data lookup, subnet reuse-or-create, and outputs the integration subnet ID.

### Rationale
- The VNet is an existing resource that is always reused (`AZ_VNET_REUSE=true` is the only supported mode per FR-025). The networking module uses an `azurerm_virtual_network` data source to look it up by name and resource group.
- The integration subnet has two modes (FR-026): (a) reuse an existing subnet by name, or (b) create a new delegated subnet with a specified CIDR. This is a variation of the standard reuse pattern — instead of `reuse` boolean, the choice is driven by whether `existing_subnet_name` is provided.
- The Private Endpoint for SQL is reused (`AZ_SQL_PRIVATE_ENDPOINT_REUSE=true`) — no PE resources are created. The module simply does not create any PE resources. The existing PE and Private DNS zone on the VNet handle connectivity automatically.
- Placing the module in `live/shared/` (not in stack roots) is correct because both stacks share the same integration subnet (FR-028), and VNet/subnet resources are shared infrastructure.
- The networking module outputs `integration_subnet_id`, which flows through shared outputs → stack root variables → stack composition → foundation app-service, following the same pattern as `app_service_plan_id`.

### Module Decision: Subnet Reuse vs Create
```
IF existing_subnet_name != "" THEN
  → data "azurerm_subnet" "integration" (look up existing)
ELSE IF subnet_cidr != "" THEN
  → resource "azurerm_subnet" "integration" (create new with delegation)
  → validate CIDR is at least /26
ELSE
  → ERROR: one of subnet_name or subnet_cidr must be specified
END
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Inline VNet data sources in `live/shared/main.tf` | Violates the established foundation module pattern; harder to test and maintain |
| Networking config in each stack root | Violates FR-028 (shared subnet); would create duplicate subnets or race conditions |
| Support VNet creation (`reuse_vnet=false`) | Explicitly out of scope per FR-025 ("creating a new VNet from scratch is not supported") |

---

## R-015: CIDR Validation and Subnet Sizing (US6)

### Decision
Use a **Terraform `validation` block** on the `subnet_cidr` variable to enforce the minimum /26 prefix length, and add a **Bash-level pre-check** in `validate_reuse_coordinates()` for early feedback.

### Rationale
- Azure requires a minimum subnet size of /26 (64 addresses) for App Service VNet Integration delegation. Smaller subnets cause deployment failures with opaque Azure error messages.
- Terraform's `validation` block can parse the CIDR prefix and reject values < /26 at plan time, before any Azure API calls.
- The Bash wrapper provides an even earlier check during the `validate_reuse_coordinates` phase, giving operators clear feedback before Terraform even runs.
- When the subnet is being reused (existing), no CIDR validation is needed — the subnet already exists and Azure has already validated its size.

### Validation Pattern (Terraform)
```hcl
variable "subnet_cidr" {
  type    = string
  default = ""
  validation {
    condition     = var.subnet_cidr == "" || (
      can(cidrhost(var.subnet_cidr, 0)) &&
      tonumber(split("/", var.subnet_cidr)[1]) <= 26
    )
    error_message = "Integration subnet CIDR must be at least /26 (e.g., 10.0.1.0/26). Smaller subnets are not supported for App Service VNet Integration delegation."
  }
}
```

### Alternatives Considered
| Option | Rejected Because |
|--------|-----------------|
| Let Azure reject the subnet size | Error message is opaque and occurs late in the deployment pipeline |
| Only Bash validation, no Terraform | Terraform validation is authoritative and works for direct `terraform plan` users who bypass the wrapper |
| Default CIDR in module | Violates the principle of explicit configuration; CIDR must come from the operator |