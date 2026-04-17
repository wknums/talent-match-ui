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

Start from the tracked examples:

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

## Manual Deployment via GitHub Actions

1. Open the `selective-azure-deploy` workflow.
2. Choose the target environment.
3. Choose one target: `shared-only`, `stack-a`, `stack-b`, or `both`.
4. Run the workflow.

Manual target selection overrides changed-path detection.

## Automatic Deployment

Pushes to `develop` default to `staging` and pushes to `main` default to `production`. The workflow resolves which scopes changed, then applies shared infrastructure first when required and deploys only the selected or affected stack artifacts.

## Verify Deployment

```bash
curl https://app-talentmatch-node-dev.azurewebsites.net/api/health
curl https://app-talentmatch-blazor-dev.azurewebsites.net/api/health

terraform -chdir=infra/terraform/live/shared output
terraform -chdir=infra/terraform/live/stack-a output
terraform -chdir=infra/terraform/live/stack-b output
```

## Seed Key Vault Secrets

```bash
KV_NAME="kv-talentmatch-dev"
az keyvault secret set --vault-name "$KV_NAME" --name openai-api-key --value "<your-key>"
az keyvault secret set --vault-name "$KV_NAME" --name awr-api-key --value "<your-key>"
```

## Safe Deprovisioning

### Preview Stack A Destruction

```bash
./infra/scripts/deprovision.sh .env_local dev stack-a --dry-run
```

### Remove Stack B Only

```bash
./infra/scripts/deprovision.sh .env_qa test stack-b
```

### Remove Shared Infrastructure

```bash
./infra/scripts/deprovision.sh .env_prod prod shared
```

The deprovision wrapper is required so reuse-flagged resources remain protected.