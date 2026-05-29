# Quickstart — 007-fix-stackb-sql-endpoint

> Verification runbook for the two fixes. Execute after implementation is complete.

## Prerequisites

- Azure subscription with Stack B resources deployed
- Azure CLI authenticated (`az login`)
- Terraform CLI available
- `.env_qa` sourced (provides `AWR_SEQ_API_ENDPOINT` and other deployment vars)
- `common.sh` sourced (exports `TF_VAR_*` variables)

## Step 1: Rebuild Stack B

```bash
cd <repo-root>
bash infra/scripts/package-stack-b.sh
```

**Expected**: Build succeeds, artifact produced at `artifacts/stack-b/`.

## Step 2: Deploy Stack B Artifact

```bash
# Replace <app-name> and <resource-group> with your Stack B App Service values
az webapp deploy \
  --name <app-name> \
  --resource-group <resource-group> \
  --src-path artifacts/stack-b/stack-b.zip \
  --type zip
```

**Expected**: Deployment completes without errors.

## Step 3: Apply Terraform for Stack B

```bash
cd infra/terraform/live/stack-b
terraform plan -out=tfplan
```

**Verify** (SC-004): The plan output shows `AWR_SEQ_API_ENDPOINT` being added to app settings when `TF_VAR_awr_seq_api_endpoint` is set to a non-empty value.

```bash
terraform apply tfplan
```

**Expected**: Apply completes. The `AWR_SEQ_API_ENDPOINT` app setting is now configured on the App Service.

### Conditional Verification

Re-run with an empty endpoint to confirm no change is made:

```bash
TF_VAR_awr_seq_api_endpoint="" terraform plan
```

**Expected**: No changes to `extra_app_settings` (the conditional local produces `{}`).

## Step 4: Verify Schema Bootstrap (SC-001, SC-002)

After the app restarts with the new code, verify all tables were created:

Feature baseline expected table count: **17** `CREATE TABLE` statements in `server/storage/schema.sql`.

Optional schema-derived count check from repo source:

```bash
grep -Eic '^CREATE TABLE' server/storage/schema.sql
```

Expected output: `17`

```sql
-- Run against the Azure SQL database
SELECT COUNT(*) AS table_count
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
  AND TABLE_SCHEMA = 'dbo';
```

**Expected**: `table_count = 17` and it matches the schema-derived `CREATE TABLE` count from source. Check application logs for absence of `FormatException` or format-related errors during startup.

### Step 4b: Verify Missing-Schema Diagnostic (NFR-002)

Run a controlled validation using a temporary artifact variant where `server/storage/schema.sql` is intentionally excluded from deployment payload.

**Expected**: Startup logs include a clear bootstrap error that the schema file was not found (actionable diagnostic, not a generic failure).

### Step 4c: Verify SQL-Execution Diagnostic (NFR-002)

Run a controlled validation using a temporary artifact variant with an intentionally invalid SQL statement in the bootstrap schema input.

**Expected**: Startup logs include actionable SQL execution failure details (statement/batch context and error), not only a generic startup failure.

## Step 5: Verify AWR API Health Check (SC-003)

```bash
curl -s https://<stack-b-hostname>/api/health | jq '.dependencies[] | select(.name == "awr-api")'
```

**Expected**: The `awrApi` dependency reports a status of `"ok"` or `"failed"` (with a connectivity error detail) — NOT `"skipped"`. The status `"skipped"` indicates the environment variable is not configured.

## Step 6: Verify No Regression on Stack A (SC-005)

```bash
# Stack A should be completely unaffected
curl -s https://<stack-a-hostname>/api/health | jq .
```

Also verify no unintended Terraform drift outside Stack B root:

```bash
cd infra/terraform/live/stack-a && terraform plan
```

**Expected**: Stack A health endpoint returns the same results as before the changes. No unintended Terraform changes appear for Stack A or other non-target roots.
