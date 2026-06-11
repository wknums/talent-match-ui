# Infrastructure: Selective Azure Deployment

This directory contains all deployment automation for the TalentMatch platform, supporting selective deployment of Stack A (Node.js/Express), Stack B (.NET Blazor WASM), or both to Azure.

## Architecture

### Three Terraform Live Roots

The infrastructure uses three independent Terraform state roots to enable selective deployment and safe lifecycle management:

```text
infra/terraform/live/
├── shared/     # Shared infrastructure (Resource Group, App Service Plan, SQL, Key Vault, APIM, Identities)
├── stack-a/    # Stack A App Service (Node.js/Express)
└── stack-b/    # Stack B App Service (.NET Blazor WASM)
```

- **Shared root** provisions or reuses shared Azure resources consumed by both stacks.
- **Stack A root** deploys only the Node.js/Express app host.
- **Stack B root** deploys only the .NET Blazor WASM app host.

### Reuse-Aware Foundation Modules

Each shared resource module supports a **create-or-reuse** pattern:

```text
infra/terraform/modules/foundation/
├── app-service-plan/   # Shared compute plan
├── sql/                # Azure SQL Server + Database
├── key-vault/          # Key Vault for secrets
├── apim/               # API Management gateway
├── identities/         # User-assigned managed identities
└── app-service/        # App Service building block (always created)
```

When `reuse=true`, the module uses a `data` source to look up the existing resource. When `reuse=false`, it creates the resource. **Outputs remain stable** regardless of ownership.

### Stack Composition Modules

```text
infra/terraform/modules/
├── stack-a/   # Node.js/Express app composition
└── stack-b/   # .NET Blazor WASM app composition
```

## Environment Profiles

| Profile | Terraform Env | GitHub Env | Usage |
|---------|---------------|------------|-------|
| `.env_local` | `dev` | `development` | Local development and testing |
| `.env_qa` | `test` | `staging` | QA/staging on Azure |
| `.env_prod` | `prod` | `production` | Production on Azure |

Start from the tracked examples:

```bash
cp .env_local.example .env_local
cp .env_qa.example .env_qa
cp .env_prod.example .env_prod
```

## Deployment Commands

### Local / CLI Deployment

```bash
# Deploy shared + Stack A to dev
./infra/scripts/deploy.sh .env_local dev apply stack-a

# Deploy shared + Stack B to dev
./infra/scripts/deploy.sh .env_local dev apply stack-b

# Deploy both stacks to QA
./infra/scripts/deploy.sh .env_qa test apply both

# Plan shared-only changes in production
./infra/scripts/deploy.sh .env_prod prod plan shared-only
```

### Usage Pattern

```bash
./infra/scripts/deploy.sh <env-file> <tf-environment> <action> <target>
```

| Argument | Options |
|----------|---------|
| `env-file` | `.env_local`, `.env_qa`, `.env_prod` |
| `tf-environment` | `dev`, `test`, `prod` |
| `action` | `plan`, `apply` |
| `target` | `shared-only`, `stack-a`, `stack-b`, `both` |

## Full Local Azure Deployment (Infra + Packaging + App Deploy)

This runbook mirrors the CI workflow but executes manually from your terminal.

### Prerequisites

- Azure CLI, Terraform, Node.js/npm, and .NET SDK installed.
- Run from repo root.
- Use Git Bash on Windows.
- Target environment profile exists (`.env_local`, `.env_qa`, or `.env_prod`).

```bash
# Authenticate and select subscription
az login

# Optional but recommended: set your active subscription explicitly
az account set --subscription "<subscription-id-or-name>"
```

### Stack A: Full Deploy (Shared Infra + Stack A Infra + Package + Zip Deploy)

```bash
# 1) Provision/update shared + Stack A infrastructure
./infra/scripts/deploy.sh .env_qa test apply stack-a

# 2) Build/package Stack A artifact (artifacts/stack-a.zip)
./infra/scripts/package-stack-a.sh

# 3) Load resource group from profile and resolve deployed app name
set -a
source .env_qa
set +a
STACK_A_APP_NAME="$(terraform -chdir=infra/terraform/live/stack-a output -raw app_name)"

# 4) Ensure on-host build/install is enabled for zip deploy
az webapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --settings SCM_DO_BUILD_DURING_DEPLOYMENT=true ENABLE_ORYX_BUILD=true

# 5) Deploy packaged zip to App Service
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --src-path artifacts/stack-a.zip \
  --type zip \
  --async true
```

### Stack B: Full Deploy (Shared Infra + Stack B Infra + Package + Zip Deploy)

```bash
# 1) Provision/update shared + Stack B infrastructure
./infra/scripts/deploy.sh .env_qa test apply stack-b

# 2) Publish/package Stack B artifact (artifacts/stack-b.zip)
./infra/scripts/package-stack-b.sh

# 3) Deploy packaged zip to App Service (loads env + resolves app name)
./infra/scripts/deploy-stack-b-app.sh .env_qa artifacts/stack-b.zip
```

### Both Stacks: Full Deploy (One Infra Pass + Both Packages + Both App Deploys)

```bash
# 1) Provision/update shared + both stack infrastructures
./infra/scripts/deploy.sh .env_qa test apply both

# 2) Build both deployable artifacts
./infra/scripts/package-stack-a.sh
./infra/scripts/package-stack-b.sh

# 3) Load resource group and resolve app names
set -a
source .env_qa
set +a
STACK_A_APP_NAME="$(terraform -chdir=infra/terraform/live/stack-a output -raw app_name)"
STACK_B_APP_NAME="$(terraform -chdir=infra/terraform/live/stack-b output -raw app_name)"

# 4) Ensure Stack A on-host build/install is enabled for zip deploy
az webapp config appsettings set \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --settings SCM_DO_BUILD_DURING_DEPLOYMENT=true ENABLE_ORYX_BUILD=true

# 5) Deploy both artifacts
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --src-path artifacts/stack-a.zip \
  --type zip \
  --async true

az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_B_APP_NAME" \
  --src-path artifacts/stack-b.zip \
  --type zip \
  --async true
```

### Plan-Only Flow (No Packaging/App Deploy)

`plan` only validates infrastructure changes. Packaging and `az webapp deploy` are only needed for `apply` when you are publishing app code.

```bash
./infra/scripts/deploy.sh .env_qa test plan both
```

## Stack A Startup Command (Azure App Service)

Stack A is deployed as a Linux App Service and should use this startup command:

```bash
npm start
```

Important notes:
- Do not use `npm run` without a script name. It fails at startup.
- The startup command is managed by Terraform in `infra/terraform/modules/stack-a/main.tf` via `app_command_line`.
- If you change startup command in the Azure portal, the next Terraform apply can overwrite it.

## Stack A Packaging and On-Host Build Behavior

`infra/scripts/package-stack-a.sh` now creates a lean zip artifact that excludes `node_modules` to speed packaging and reduce local zip CPU/file-count overhead.

Deployment implications:
- App Service must build on deploy (Oryx) so production dependencies are restored on-host.
- Required app settings: `SCM_DO_BUILD_DURING_DEPLOYMENT=true` and `ENABLE_ORYX_BUILD=true`.
- These are now managed by Terraform in `infra/terraform/modules/stack-a/main.tf`.

Operational expectations:
- Local packaging is significantly faster.
- First deployment after a code change shifts part of the time to server-side build logs during Zip Deploy.
- Runtime startup still uses the compiled server entrypoint (`node server/index.js`).

## GitHub Actions Workflow

The workflow at `.github/workflows/selective-azure-deploy.yml` supports:

- **Push triggers**: Automatically detects changed scopes and deploys only affected stacks.
- **Manual dispatch**: Choose environment, target, and action explicitly.

### Changed-Path Detection

| Scope | Paths |
|-------|-------|
| Shared | `infra/**`, `.github/workflows/**`, `.env_*.example` |
| Stack A | `src/**`, `server/**`, `package.json`, `vite.config.ts`, `tsconfig*.json`, `tailwind.config.js` |
| Stack B | `dotnet/**` |

### Workflow Jobs

1. **resolve-targets** — Determines which scopes to deploy
2. **plan-or-apply-shared** — Applies shared infrastructure first
3. **package-stack-a** — Builds and packages Stack A (parallel with package-stack-b)
4. **package-stack-b** — Publishes and packages Stack B (parallel with package-stack-a)
5. **deploy-stack-a** — Deploys Stack A (after shared + package)
6. **deploy-stack-b** — Deploys Stack B (after shared + package)

## Database Schema Isolation

All 15 application tables are isolated under the `[talentmatch]` schema in Azure SQL. Local SQLite development is completely unaffected.

## Azure SQL Wake-Up Behavior

Pay-as-you-go Azure SQL databases can pause when idle and may take 30-90 seconds to accept connections again. The application is documented and implemented to tolerate that wake-up window.

### Runtime Behavior

| Stack | Resilience Behavior | Defaults |
|-------|---------------------|----------|
| **Stack A (Node.js)** | Retries initial `mssql` connect with bounded exponential backoff in `server/storage/db.ts` | `8` attempts, `2000ms` initial delay, `15000ms` max delay |
| **Stack B (.NET)** | Applies EF Core `EnableRetryOnFailure`, raises SQL connect timeout, and retries startup migration/seed work | `6` provider retries, `15s` max retry delay, minimum `90s` connect timeout |

### Operator Guidance

1. Keep the SQL connection string connect timeout at `90` seconds or higher for Azure-hosted environments.
2. Expect startup logs showing retry scheduling, success-after-retry, or retry-budget exhaustion if the database is waking from idle.
3. For Stack A, tune retry behavior with `AZURE_SQL_WAKEUP_MAX_ATTEMPTS`, `AZURE_SQL_WAKEUP_INITIAL_DELAY_MS`, and `AZURE_SQL_WAKEUP_MAX_DELAY_MS` if your environment has different idle resume characteristics.
4. For Azure-hosted deployments, prefer Entra managed identity auth with `AZURE_SQL_SERVER_FQDN`, `AZURE_SQL_DATABASE_NAME`, and `AZURE_CLIENT_ID` instead of a password-based SQL connection string secret.

### How It Works

| Stack | Mechanism | Details |
|-------|-----------|---------|
| **Stack A (Node.js)** | `T()` helper in `server/storage/table-names.ts` | Returns `[talentmatch].[Table]` for Azure SQL, plain `Table` for SQLite |
| **Stack B (.NET)** | `HasDefaultSchema("talentmatch")` in `AppDbContext.cs` | Applied only when `Database.IsSqlServer()` is true |
| **Terraform** | `terraform_data.ensure_schema` in `modules/foundation/sql/main.tf` | Creates schema via `az sql db query` after database provisioning |

### Schema Creation (Defense in Depth)

The `talentmatch` schema is created at three levels:

1. **Terraform provisioner** — `infra/terraform/modules/foundation/sql/main.tf` creates the schema immediately after database creation
2. **Application startup** — `server/storage/db.ts` `initializeDatabase()` runs `CREATE SCHEMA` before DDL
3. **DDL preamble** — `server/storage/schema.sql` includes a schema creation guard at the top

### Adding a New Table (Node.js)

```typescript
import { T } from '../table-names.js'

// In your repo file — one call per table reference:
const result = await pool.request()
  .query(`SELECT * FROM ${T('NewTable')} WHERE Id = @id`)
```

### Migration Script (dbo → talentmatch)

For existing databases with tables under `[dbo]`, run the idempotent migration:

```bash
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "$(cat infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql)"
```

The script transfers all 15 tables via `ALTER SCHEMA`, skips tables already in the target schema, and prints a summary.

### Verification Queries

```sql
-- Schema exists?
SELECT name FROM sys.schemas WHERE name = 'talentmatch';

-- All 15 tables under talentmatch?
SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('talentmatch');
-- Expected: 15

-- Zero application tables in dbo?
SELECT COUNT(*) FROM sys.tables
WHERE schema_id = SCHEMA_ID('dbo')
  AND name IN ('Users','PasswordResetRequests','Jobs','JobConfigVersions',
               'Applications','ApplicationDocuments','DocumentBlobs',
               'ExtractionArtifacts','ScoringRuns','AggregatedResults',
               'ManualReviews','ScoringPrompts','PromptTestRuns',
               'FailureQueueItems','ProcessingEvents');
-- Expected: 0
```

## Safe Deprovisioning

**Always use the deprovision wrapper** — never run raw `terraform destroy`.

```bash
# Preview what would be destroyed
./infra/scripts/deprovision.sh .env_local dev stack-a --dry-run

# Destroy Stack A only
./infra/scripts/deprovision.sh .env_local dev stack-a

# Destroy Stack B only (with force for CI)
./infra/scripts/deprovision.sh .env_qa test stack-b --force

# Destroy shared infrastructure (requires separate confirmation)
./infra/scripts/deprovision.sh .env_prod prod shared
```

### Reuse Protection

Resources with `*_REUSE=TRUE` in the `.env_*` file are:
- Never created as Terraform-managed resources (only `data` sources)
- Marked as `[PROTECTED]` in the deprovision summary
- Never destroyed, even with `--force`

### Deprovision Order

1. Stack-specific resources first (`stack-a`, `stack-b`, or `both`)
2. Shared infrastructure separately (requires dedicated `shared` target)

## Reuse Configuration

To reuse an existing resource, set the reuse flag and provide coordinates in your `.env_*` file:

```ini
AZ_SQL_REUSE=TRUE
SQL_SERVER_NAME=sql-shared-qa
SQL_DATABASE_NAME=awrdb
SQL_RG=rg-shared-platform
```

The deploying identity needs at least `Reader` access on external resource groups containing reused resources.

## Private Network Connectivity (US6)

Both App Services can be configured with VNet Integration and IP access restrictions for private Azure SQL connectivity.

### Architecture

```text
┌─────────────────────────────────────────────────────────────┐
│  Existing VNet (data source lookup — never created)          │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  Delegated Subnet (reuse or create)                  │   │
│  │  Delegation: Microsoft.Web/serverFarms               │   │
│  │                                                      │   │
│  │  ┌─────────────┐    ┌─────────────┐                 │   │
│  │  │  Stack A     │    │  Stack B     │                │   │
│  │  │  App Service │    │  App Service │                │   │
│  │  └──────┬──────┘    └──────┬──────┘                 │   │
│  │         │                   │                        │   │
│  └─────────┼───────────────────┼────────────────────────┘   │
│            │  Private DNS      │                             │
│            ▼                   ▼                             │
│  ┌──────────────────────────────────────┐                   │
│  │  SQL Private Endpoint (pre-existing) │                   │
│  └──────────────────────────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
```

### Networking Foundation Module

Path: `infra/terraform/modules/foundation/networking/`

The module performs a data source lookup on an existing VNet (never creates one) and either:
- **Reuse mode**: Looks up an existing delegated subnet by name (`existing_subnet_name`)
- **Create mode**: Creates a new delegated subnet with the specified CIDR (`subnet_cidr`)

The module outputs a stable `integration_subnet_id` regardless of mode.

### Output Flow

```
shared root (networking module) → integration_subnet_id output
    → TF_VAR_integration_subnet_id (captured by deploy.sh)
        → stack-a/stack-b live roots → composition modules → app-service module
```

### Subnet Modes

| Mode | Variable | Description |
|------|----------|-------------|
| Reuse | `AZ_INTEGRATION_SUBNET_NAME` | Looks up existing delegated subnet |
| Create | `AZ_INTEGRATION_SUBNET_CIDR` | Creates new subnet with delegation (minimum /26) |

These are mutually exclusive — set exactly one.

### IP Access Restrictions

When `AZ_ALLOWED_IPS` is set, all App Services enforce a **deny-all default** with explicit Allow rules for each listed IP. Traffic from non-listed IPs receives HTTP 403.

### Environment Profile Configuration

```ini
# .env_qa or .env_prod
AZ_VNET_REUSE=TRUE
AZ_VNET_NAME=vnet-awr-platform
AZ_VNET_RG=rg-awr-networking
AZ_INTEGRATION_SUBNET_NAME=snet-appservice-integration
AZ_SQL_PRIVATE_ENDPOINT_REUSE=TRUE
AZ_ALLOWED_IPS=203.0.113.10,198.51.100.20
```

### Backward Compatibility

Deployments that do not set `AZ_VNET_REUSE=TRUE` work exactly as before — no VNet Integration, no IP restrictions. All new variables default to empty/false.

### Required Azure RBAC Permissions

The deploying identity needs **Network Contributor** on the VNet resource group to create subnets or configure VNet Integration. For reuse-only mode (existing subnet), **Reader** access on the VNet resource group is sufficient.

### Validation Rules

The deploy script validates before any Terraform execution:
- `AZ_VNET_NAME` and `AZ_VNET_RG` must be set when `AZ_VNET_REUSE=TRUE`
- Exactly one of `AZ_INTEGRATION_SUBNET_NAME` or `AZ_INTEGRATION_SUBNET_CIDR` must be set
- CIDR prefix must be ≤26 when creating a new subnet
- `AZ_ALLOWED_IPS` must be non-empty when VNet is configured

## Scripts Reference

| Script | Purpose |
|--------|---------|
| `infra/scripts/deploy.sh` | Main deployment wrapper |
| `infra/scripts/deprovision.sh` | Safe destroy with reuse protection |
| `infra/scripts/resolve-targets.sh` | Workflow target resolution |
| `infra/scripts/package-stack-a.sh` | Build and package Stack A |
| `infra/scripts/package-stack-b.sh` | Publish and package Stack B |
| `infra/scripts/lib/common.sh` | Shared Bash helpers |
