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

## Step 3: Apply Stack B Infrastructure via Deployment Script

```bash
bash infra/scripts/deploy.sh .env_qa test plan stack-b
```

**Verify** (SC-004): Script-driven plan output shows `AWR_SEQ_API_ENDPOINT` being added to app settings when `AWR_SEQ_API_ENDPOINT` is non-empty in the selected env file.

```bash
bash infra/scripts/deploy.sh .env_qa test apply stack-b
```

**Expected**: Apply completes. The `AWR_SEQ_API_ENDPOINT` app setting is now configured on the App Service.

### Conditional Verification

Re-run using a temporary env file where `AWR_SEQ_API_ENDPOINT` is intentionally empty to confirm no app-setting change is made:

```bash
bash infra/scripts/deploy.sh <temp-env-with-empty-endpoint> test plan stack-b
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

Also verify no unintended infrastructure drift outside Stack B scope using deployment scripts:

```bash
bash infra/scripts/deploy.sh .env_qa test plan stack-a
```

**Expected**: Stack A health endpoint returns the same results as before the changes. No unintended Terraform changes appear for Stack A or other non-target roots.

## Step 7: Cross-Stack Health Semantics Matrix (NFR-004)

Use the evidence artifacts from T021/T022/T023 to confirm parity of `awrApi` health behavior between Stack A and Stack B.

| Endpoint State | Stack A Expected | Stack B Expected | Evidence |
|---|---|---|---|
| Configured + reachable | `checks.awrApi.status = "ok"` | `checks.awrApi.status = "ok"` | Stack A: `artifacts/logs/007/t013_stack-a_health.json`<br>Stack B: `artifacts/logs/007/t017_idempotent_restart_verification.md` (final recovered payload) |
| Configured + unreachable | `checks.awrApi.status = "failed"` and target points to unreachable endpoint | `checks.awrApi.status = "failed"` and same target semantics | `artifacts/logs/007/t022_unreachable_parity_verification.md`<br>`artifacts/logs/007/t022_stack-a_health_unreachable.json`<br>`artifacts/logs/007/t022_stack-b_health_unreachable.json` |
| Not configured | `checks.awrApi.status = "skipped"` with not-configured detail | `checks.awrApi.status = "skipped"` with same detail | `artifacts/logs/007/t023_not_configured_parity_verification.md`<br>`artifacts/logs/007/t023_stack-a_health_not_configured.json`<br>`artifacts/logs/007/t023_stack-b_health_not_configured.json` |

### Runbook Interpretation

- `awrApi.status = "ok"`: Endpoint is configured and reachable. No action needed for AWR connectivity.
- `awrApi.status = "failed"`: Endpoint is configured but unreachable. Check endpoint value, DNS/network routing, TLS/certificate chain, and upstream service availability.
- `awrApi.status = "skipped"` with `AWR_SEQ_API_ENDPOINT is not configured.`: Configuration-intent state. This is expected only when the endpoint is intentionally unset.

### Operator Notes

- Overall health can return unhealthy (often HTTP 503) when any dependency check fails, even when the app process is alive.
- For parity validation, compare semantic fields first (`checks.awrApi.status`, `checks.awrApi.target`, `checks.awrApi.detail`) rather than exact error wording.
- Always use deployment scripts (`infra/scripts/deploy.sh`) for endpoint-state transitions so both stacks are exercised consistently.
