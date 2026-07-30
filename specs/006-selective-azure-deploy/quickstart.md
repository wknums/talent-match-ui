# Quickstart: Selective Azure Deployment

**Feature**: 006-selective-azure-deploy
**Prerequisites**: Azure subscription, GitHub repository with Actions enabled, Azure CLI, Git Bash, Terraform 1.9+

## One-Time Setup

### 1. Create Azure AD App Registration and Federated Credentials

```bash
APP_ID=$(az ad app create --display-name "talentmatch-github-deploy" --query appId -o tsv | tr -d '\r')
az ad sp create --id "$APP_ID"

TENANT_ID=$(az account show --query tenantId -o tsv | tr -d '\r')
SUB_ID=$(az account show --query id -o tsv | tr -d '\r')

for ENV in development staging production; do
  az ad app federated-credential create --id "$APP_ID" --parameters "{
    \"name\": \"github-${ENV}\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:YOUR_ORG/awr-cv-match-client:environment:${ENV}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
done

printf 'AZURE_CLIENT_ID=%s\n' "$APP_ID"
printf 'AZURE_TENANT_ID=%s\n' "$TENANT_ID"
printf 'AZURE_SUBSCRIPTION_ID=%s\n' "$SUB_ID"
```

### 2. Create Managed Resource Groups

```bash
for ENV in dev test prod; do
  az group create \
    --name "rg-talentmatch-${ENV}" \
    --location australiaeast \
    --tags environment="$ENV" project=talentmatch managedBy=terraform
done
```

### 3. Assign Deployment Identity Access

```bash
export MSYS_NO_PATHCONV=1

for ENV in dev test prod; do
  az role assignment create \
    --assignee "$APP_ID" \
    --role Contributor \
    --scope "/subscriptions/${SUB_ID}/resourceGroups/rg-talentmatch-${ENV}"
done
```

Grant at least `Reader` on any external resource groups that contain reused SQL, Key Vault, APIM, or identity resources.

### 4. Configure GitHub Environments

Create `development`, `staging`, and `production` environments and add:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Add required reviewers to `production`.

### 5. Create Deployment Profiles

```bash
cp .env_local.example .env_local
cp .env_qa.example .env_qa
cp .env_prod.example .env_prod
```

Set the reuse flags and existing resource coordinates needed for each environment.

## Local and CI Deployment Commands

### Deploy Shared Infrastructure and Stack A in Dev

```bash
./infra/scripts/deploy.sh .env_local dev apply stack-a
```

### Deploy Shared Infrastructure and Stack B in Dev

```bash
./infra/scripts/deploy.sh .env_local dev apply stack-b
```

### Deploy Both Stacks in QA

```bash
./infra/scripts/deploy.sh .env_qa test apply both
```

### Plan Shared-Only Changes in Production

```bash
./infra/scripts/deploy.sh .env_prod prod plan shared-only
```

### Deploy Command Reference

```bash
./infra/scripts/deploy.sh <env-file> <tf-environment> <action> <target>
```

| Argument | Options |
|----------|---------|
| `env-file` | `.env_local`, `.env_qa`, `.env_prod` |
| `tf-environment` | `dev`, `test`, `prod` |
| `action` | `plan`, `apply` |
| `target` | `shared-only`, `stack-a`, `stack-b`, `both` |

## Manual Deployment via GitHub Actions

1. Open the `selective-azure-deploy` workflow.
2. Choose the target environment (`development`, `staging`, `production`).
3. Choose one target: `shared-only`, `stack-a`, `stack-b`, or `both`.
4. Choose the action: `plan` or `apply`.
5. Run the workflow.

Manual target selection overrides changed-path detection.

## Automatic Deployment

Pushes to `develop` default to `staging` and pushes to `main` default to `production`. The workflow resolves which scopes changed, then applies shared infrastructure first when required and deploys only the selected or affected stack artifacts.

### Changed-Path Detection

| Scope | Paths |
|-------|-------|
| Shared | `infra/**`, `.github/workflows/**`, `.env_*.example` |
| Stack A | `src/**`, `server/**`, `package.json`, `vite.config.ts`, `tsconfig*.json`, `tailwind.config.js` |
| Stack B | `dotnet/**` |

## Verify Deployment

```bash
curl https://app-talentmatch-node-dev.azurewebsites.net/api/health
curl https://app-talentmatch-blazor-dev.azurewebsites.net/api/health

terraform -chdir=infra/terraform/live/shared output
terraform -chdir=infra/terraform/live/stack-a output
terraform -chdir=infra/terraform/live/stack-b output
```

## Verify Deployment Build Stamps (Stack A + Stack B)

Each packaged deployment stamps a `build-info.json` file with a unique version and UTC creation timestamp.

1. Open Stack A in the browser and inspect DevTools Console.
2. Open Stack B in the browser and inspect DevTools Console.
3. Confirm each app logs a line in this format:

```text
[TalentMatch Build] version=<unique-version> createdAtUtc=<UTC timestamp>
```

Expected result: every deployment emits a new version value and timestamp so operators can verify the app is serving newly deployed artifacts.

## Optional AWReason API-Key Secret

```bash
KV_NAME="kv-talentmatch-dev"
az keyvault secret set --vault-name "$KV_NAME" --name awr-api-key --value "<your-key>"
```

Azure OpenAI authentication uses managed identity and RBAC; no OpenAI API key
is stored in Key Vault or App Service settings. The AWReason secret is needed
only when `AWR_AUTH_MODE=apikey` and Key Vault secret references are enabled.

Azure SQL auth is handled with Entra managed identity. After shared deployment creates or resolves the app identities, the deployment flow creates contained database users for those identities in Azure SQL. For a reused private-only database that the operator host cannot reach, set `AZ_SQL_BOOTSTRAP_ENABLED=FALSE` and run `infra/scripts/bootstrap-sql-entra-users.mjs` from a VNet-connected host before application use.

## Safe Deprovisioning

### Preview Stack A Destruction

```bash
./infra/scripts/deprovision.sh .env_local dev stack-a --dry-run
```

### Remove Stack B Only

```bash
./infra/scripts/deprovision.sh .env_qa test stack-b
```

### Remove Both Stacks

```bash
./infra/scripts/deprovision.sh .env_local dev both
```

### Remove Shared Infrastructure

```bash
./infra/scripts/deprovision.sh .env_prod prod shared
```

The deprovision wrapper is required so reuse-flagged resources remain protected. Resources with `*_REUSE=TRUE` are marked `[PROTECTED]` and will never be destroyed.

### Deprovision Command Reference

```bash
./infra/scripts/deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]
```

| Argument | Options |
|----------|---------|
| `env-file` | `.env_local`, `.env_qa`, `.env_prod` |
| `tf-environment` | `dev`, `test`, `prod` |
| `target` | `shared`, `stack-a`, `stack-b`, `both` |
| `--dry-run` | Preview without destroying |
| `--force` | Skip confirmation (CI only) |

## Database Schema Isolation (US5)

All 15 application tables live under the `[talentmatch]` schema in Azure SQL. Local SQLite development is unaffected.

### Verify Schema Exists After Deployment

```bash
# Connect to Azure SQL and verify the talentmatch schema
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "SELECT name FROM sys.schemas WHERE name = 'talentmatch'"
```

Expected: One row with `name = talentmatch`.

### Verify Table Count (Must Be 15)

```bash
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "SELECT COUNT(*) AS TableCount FROM sys.tables WHERE schema_id = SCHEMA_ID('talentmatch')"
```

Expected: `TableCount = 15`.

### Verify All Tables Under talentmatch Schema

```bash
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "SELECT s.name AS [Schema], t.name AS [Table]
           FROM sys.tables t
           JOIN sys.schemas s ON t.schema_id = s.schema_id
           WHERE s.name IN ('dbo','talentmatch')
           ORDER BY s.name, t.name"
```

Expected: 15 rows with Schema = `talentmatch`, 0 rows with Schema = `dbo` (for application tables).

### Verify Zero Application Tables in dbo

```bash
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "SELECT COUNT(*) AS DboCount FROM sys.tables
           WHERE schema_id = SCHEMA_ID('dbo')
             AND name IN ('Users','PasswordResetRequests','Jobs','JobConfigVersions',
                          'Applications','ApplicationDocuments','DocumentBlobs',
                          'ExtractionArtifacts','ScoringRuns','AggregatedResults',
                          'ManualReviews','ScoringPrompts','PromptTestRuns',
                          'FailureQueueItems','ProcessingEvents')"
```

Expected: `DboCount = 0`.

### Verify SQLite Is Unaffected

```bash
# Local dev should work exactly as before
npm run dev
# Run tests to confirm SQLite path works
npm test
```

All existing tests must pass with zero regressions — the `T()` helper returns plain table names for SQLite.

### Verify .NET Stack Schema

```bash
# Deploy Stack B and verify EF Core uses talentmatch schema
dotnet test dotnet/TalentMatch.slnx
```

All .NET tests must pass — `HasDefaultSchema("talentmatch")` is only applied when `Database.IsSqlServer()` returns true.

### Migrate Existing Database (dbo → talentmatch)

For databases that already have tables under `dbo`:

```bash
# Run the idempotent migration script
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "$(cat infra/scripts/sql/migrate-schema-dbo-to-talentmatch.sql)"
```

The migration script is idempotent — re-running it on a database that already has tables under `talentmatch` is safe.

**Dry-run check** (before migrating): Verify which tables are currently in `dbo`:

```bash
az sql db query \
  --server sql-talentmatch-dev \
  --name sqldb-talentmatch-dev \
  --resource-group rg-talentmatch-dev \
  --query "SELECT s.name AS [Schema], t.name AS [Table]
           FROM sys.tables t
           JOIN sys.schemas s ON t.schema_id = s.schema_id
           WHERE t.name IN ('Users','PasswordResetRequests','Jobs','JobConfigVersions',
                            'Applications','ApplicationDocuments','DocumentBlobs',
                            'ExtractionArtifacts','ScoringRuns','AggregatedResults',
                            'ManualReviews','ScoringPrompts','PromptTestRuns',
                            'FailureQueueItems','ProcessingEvents')
           ORDER BY s.name, t.name"
```

---

## Private Network Connectivity (US6)

### Configure Networking in Environment Profile

Add the following to your `.env_qa` (or `.env_prod`):

```ini
# --- VNet Integration ---
AZ_VNET_REUSE=TRUE
AZ_VNET_NAME=vnet-awr-platform
AZ_VNET_RG=rg-awr-networking

# Option A: Reuse existing delegated subnet
AZ_INTEGRATION_SUBNET_NAME=snet-appservice-integration

# Option B: Create new delegated subnet (use this OR Option A, not both)
# AZ_INTEGRATION_SUBNET_CIDR=10.0.4.0/26

# --- SQL Private Endpoint ---
AZ_SQL_PRIVATE_ENDPOINT_REUSE=TRUE

# --- IP Access Restrictions (required) ---
AZ_ALLOWED_IPS=203.0.113.10,198.51.100.20,192.0.2.50
```

### Deploy with Networking

```bash
./infra/scripts/deploy.sh .env_qa test apply both
```

The deploy script will:
1. Validate VNet coordinates and IP restrictions
2. Provision shared infrastructure (including networking module)
3. Deploy Stack A and Stack B with VNet Integration and IP restrictions

### Verify VNet Integration

```bash
# Check Stack A VNet Integration
az webapp vnet-integration list \
  --name app-talentmatch-node-test \
  --resource-group rg-talentmatch-test \
  --output table
```

Expected: One row showing the integration subnet.

```bash
# Check Stack B VNet Integration
az webapp vnet-integration list \
  --name app-talentmatch-blazor-test \
  --resource-group rg-talentmatch-test \
  --output table
```

Expected: Same integration subnet as Stack A (FR-028).

### Verify IP Access Restrictions

```bash
# Check Stack A IP restrictions
az webapp config access-restriction show \
  --name app-talentmatch-node-test \
  --resource-group rg-talentmatch-test \
  --output table
```

Expected: Allow rules for each IP in `AZ_ALLOWED_IPS`, with a default Deny action.

```bash
# Test from an allowed IP
curl -s -o /dev/null -w "%{http_code}" https://app-talentmatch-node-test.azurewebsites.net/api/health
# Expected: 200

# Test from a non-allowed IP (e.g., from a different machine)
# Expected: 403
```

### Verify Private SQL Connectivity

```bash
# SSH into the App Service (Kudu console) and verify DNS resolution
az webapp ssh --name app-talentmatch-node-test --resource-group rg-talentmatch-test

# Inside the console:
nslookup sql-talentmatch-test.database.windows.net
# Expected: Resolves to a private IP (10.x.x.x) — NOT a public IP
```

### Verify Application Works Over Private Network

```bash
# After deploying, check the health endpoint
curl https://app-talentmatch-node-test.azurewebsites.net/api/health
# Expected: 200 OK with database connectivity confirmed

# Check Stack B as well
curl https://app-talentmatch-blazor-test.azurewebsites.net/api/health
# Expected: 200 OK
```

### Verify No Public SQL Traffic

The connection string remains unchanged. Private DNS resolution transparently routes all SQL traffic over the VNet. No application code or connection string changes are needed (SC-018).

### Verify Terraform Outputs

```bash
terraform -chdir=infra/terraform/live/shared output integration_subnet_id
# Expected: /subscriptions/.../subnets/snet-appservice-integration
```

### Backward Compatibility (No Networking)

Deployments without `AZ_VNET_REUSE=TRUE` continue to work exactly as before. The `integration_subnet_id` output returns an empty string, and no VNet Integration or IP restrictions are applied. All new variables default to empty/false, so existing `.env_*` files do not require updates unless networking is desired.

### RBAC Requirements

| Scenario | Required Role | Scope |
|----------|--------------|-------|
| Create new subnet | Network Contributor | VNet resource group |
| Reuse existing subnet | Reader | VNet resource group |
| VNet Integration | Website Contributor | App Service resource group |
