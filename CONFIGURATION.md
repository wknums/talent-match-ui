# Configuration reference

This is the operator reference for the Talent Matching Platform. It covers both
implementations (**Stack A**: React/Node.js and **Stack B**: Blazor/.NET), local and
Azure deployment profiles, browser build settings, tests, and deployment tooling.

> [!IMPORTANT]
> Never copy values from an ignored `.env`, `.env_local`, `.env_qa`, or `.env_prod`
> file into source control. The checked-in `*.example` files retain explicitly
> labelled illustrative settings, not verified deployment coordinates or production
> approval. Review and replace environment-specific examples in an untracked copy.
> Client IDs, object IDs, resource names, and URLs are usually not
> secrets, but they are still environment-specific. Values marked **secret** must
> be stored in GitHub environment secrets, Azure Key Vault, or another approved
> secret store.

## Contents

- [How configuration is loaded](#how-configuration-is-loaded)
- [Safe examples](#safe-examples)
- [Application, storage, and browser](#application-storage-and-browser)
- [Dynamic browser refresh intervals](#dynamic-browser-refresh-intervals)
- [Microsoft Entra product authentication](#microsoft-entra-product-authentication)
- [AWReason and scoring](#awreason-and-scoring)
- [Delegated Microsoft 365 connector](#delegated-microsoft-365-connector)
- [Bulk scoring limits](#bulk-scoring-limits)
- [Azure deployment profiles](#azure-deployment-profiles)
- [Tests, packaging, and administration](#tests-packaging-and-administration)
- [Platform-managed and generated variables](#platform-managed-and-generated-variables)
- [Removed variables](#removed-variables)
- [Validation and operational changes](#validation-and-operational-changes)
- [Known implementation mismatches](#known-implementation-mismatches)

## How configuration is loaded

### Profiles and precedence

| Context | Selection and precedence |
| --- | --- |
| Stack A local runtime | Existing process environment wins. `server/index.ts` then loads missing keys from root `.env`. Defaults apply last. Vite independently loads its normal `.env*` files at **build time**. |
| Stack B local runtime | ASP.NET Core configuration precedence applies: command line, environment variables, then `appsettings*.json`. Environment keys are case-insensitive; `DatabaseProvider` and `DATABASE_PROVIDER` are equivalent. |
| CLI Azure deployment | The profile is explicit: `deploy.sh <env-file> <dev\|test\|prod> ...`. `load_env_file` exports every profile entry and `common.sh` maps operator keys to Terraform inputs. Shell variables already present can therefore be overwritten by the selected file. |
| GitHub deployment | The GitHub environment secret `DEPLOYMENT_ENV_FILE` supplies the complete profile for target resolution, infrastructure, and Stack A packaging. CI writes an untracked, permission-restricted file and never falls back to an example. Separate GitHub variables do not override profile values. OIDC login still uses its three dedicated Azure secrets. |
| Azure runtime | Terraform app settings override application defaults. Key Vault references are resolved by App Service. Changing an app setting restarts the affected App Service. |

`.env_local`, `.env_qa`, and `.env_prod` are untracked deployment inputs. Their
checked-in templates map to Terraform `dev`, `test`, and `prod` respectively.
`STACK_TARGET` in a profile defaults to `both`, while the explicit fourth argument
to `deploy.sh` selects the operation actually run. The profile defines the shared
provisioning inventory: a command target may narrow the run but cannot enable
an excluded stack. A `shared-only` command still prepares dependencies for the
profile's selected stacks; a `STACK_TARGET=shared-only` profile has no runtime
identities or SPAs. Changing an existing inventory can plan deletion of managed
objects, so review the Terraform plan before applying.

Do **not** set `TF_VAR_*` directly in a profile. They are transient adapters created
by `infra/scripts/lib/common.sh`, or downstream values populated from Terraform
outputs. Set the documented profile source variable instead (for example,
`AZURE_LOCATION`, not `TF_VAR_location`). The same rule applies to generated
`ARM_*`, `STACK_A_NAME`, `STACK_B_NAME`, `SQL_TOKEN_FILE`, identity-name, build
metadata, and GitHub runner variables.

Do not source `infra/scripts/lib/common.sh` directly into a long-lived interactive
shell: the helper enables `set -euo pipefail`, so a later failing command can close
that shell. Let `deploy.sh` load the profile, or run helper-based administrative
commands inside a subshell (`( ... )`) so strict-mode effects cannot escape.

### Legend

- **Required** means startup/deployment fails without it.
- **Conditional** means required only in the stated mode.
- **Optional** means omission uses the listed default or disables the feature.
- **Secret** values must not reach a browser or a committed file.
- **Browser-public** `VITE_*` values are compiled into JavaScript and are readable
  by every user. Never put credentials in them.

## Safe examples

Local Stack A/Stack B development:

```env
APP_AUTH_MODE=simple
STORAGE_PROVIDER=local
DATABASE_PROVIDER=sqlite
AWR_SEQ_API_ENDPOINT=http://127.0.0.1:8080
AWR_AUTH_MODE=none
MICROSOFT365_SOURCE_MODE=mock
BULK_AUTH_DEV_KEY_PATH=artifacts/local/bulk-auth.key
```

Azure profile fragments use placeholders, never live values:

```env
AZURE_SUBSCRIPTION_ID=<subscription-guid>
AZURE_TENANT_ID=<tenant-guid>
RESOURCE_GROUP=<resource-group-name>
APP_AUTH_MODE=entra
ENTRA_API_IDENTIFIER_URI=api://<protected-api-app-id>
AWR_SEQ_API_ENDPOINT=https://<awreason-host>
```

## Dynamic browser refresh intervals

In Stack B, a global administrator can change these database-backed values on
`/settings`, under **Dashboard and status refresh**, using the existing settings
save and optimistic-concurrency controls. They are not environment variables and
do not require an application restart.

| JSON setting | Default | Applies to |
| --- | --- | --- |
| `throughputRefreshIntervalSeconds` | `60` | Dashboard throughput graph |
| `statusPollingIntervalSeconds` | `60` | Job/application status, bulk-request status and dashboard status/upload activity |

Both accept positive whole seconds up to the browser timer's supported range
(4,294,967 seconds). Short intervals can substantially increase database load.
The initial data request is immediate. Subsequent automatic refreshes wait the
configured interval **after the previous refresh finishes**, never overlap within
the same loop, and are cancelled when the component is disposed. The former
independent 5-second and 15-second job pollers are replaced by one status loop.
Manual actions still refresh immediately.
Bulk status authorization and detail reads also use the existing lightweight
job/config lookup instead of materializing the job's full application collection.

Authenticated browsers read only the two intervals from
`GET /api/system-configuration/polling` (not upload limits or administrator
metadata). Each open layout checks configuration every 60 seconds. Saving on
the same page updates its configuration immediately; other browsers pick it up
on their next configuration check. Changes reschedule a waiting refresh without
cancelling an in-flight data refresh. Failed configuration loads are shown
explicitly while preserving the last known intervals, or the 60-second defaults
before a first successful load.

The settings use the existing `UploadSettings` singleton. New and upgraded
SQLite/Azure SQL schemas default both columns to 60; existing upload limits and
configured intervals are retained. Older clients that omit the new fields when
saving upload limits do not reset configured intervals. Apply the
`AddUiPollingIntervals` migration when using EF migrations; the normal application
startup schema path also upgrades existing installations.

Less frequent, non-overlapping browser reads reduce load and contention; they
are not evidence that a particular SQL deadlock cycle has been eliminated.
Compare deadlock graphs and rates under the same completed-workload denominator
before changing worker transaction isolation or ownership fences.

## Application, storage, and browser

### Core runtime and persistence

| Variable | Status; values/default | Purpose, dependencies, scope, and source |
| --- | --- | --- |
| `PORT` | Optional integer; `3001` | Stack A HTTP port. A malformed value fails when Express listens. Local/container setting. |
| `API_MODE` | Optional `mock` or `real`; `mock` | Returned by the public Stack A `/api/config` endpoint and supplied to both Azure stacks. It does not itself select the database. |
| `APP_AUTH_MODE` | Optional locally, required for shared environments; `simple` or `entra`; `simple` | Product sign-in mode, both stacks. Stack B rejects `simple` outside Development/Testing. `entra` activates the Entra dependencies below. |
| `CORS_ALLOWED_ORIGINS` | Optional comma-separated origins; empty | Credentialed browser origins for both APIs. Empty is intended for same-origin hosting. Use exact origins (`scheme://host[:port]`), without paths. |
| `STORAGE_PROVIDER` | Optional; Stack A `local`/`sqlite` or `azuresql`; local runtime falls back to SQLite, deploy script defaults `azuresql` | `azuresql` only takes effect with `AZURE_SQL_AUTH_MODE=entra` and SQL coordinates, unless the legacy connection string is supplied. |
| `DATABASE_PROVIDER` | Optional; Stack B `sqlite` or `sqlserver`; `sqlite` | Selects EF Core provider. Azure Terraform forces `sqlserver`. |
| `SQLITE_DB_PATH` | Optional path; Stack A `shared-data/talentmatch.db`; Stack B resolves `../../../shared-data/talentmatch.db` from its content root | Overrides the local SQLite file. Restart required. |
| `AZURE_SQL_AUTH_MODE` | Conditional; `entra`; unset | Enables managed-identity SQL construction in both stacks. Requires the two coordinates below. Azure Terraform manages this for Stack A; Stack B chooses managed identity when no connection string is supplied. |
| `AZURE_SQL_SERVER_FQDN` | Conditional non-secret host | Required with Entra SQL auth. Obtain from Azure SQL server **Overview** or `az sql server show --query fullyQualifiedDomainName`. |
| `AZURE_SQL_DATABASE_NAME` | Conditional non-secret name | Required with Entra SQL auth. Obtain from the SQL database resource or Terraform output. |
| `AZURE_CLIENT_ID` | Conditional non-secret GUID; no default | User-assigned managed-identity client ID used by SQL, Blob, Key Vault, and `DefaultAzureCredential`. Terraform/App Service normally manages it. |
| `AZURE_SQL_CONNECTION_STRING` | Optional **secret**, compatibility path; unset | Stack A connection string takes precedence over provider/managed-identity settings. Prefer Entra settings. Rotation/restart required. |
| `ConnectionStrings__DefaultConnection` | Optional **secret**; unset | Stack B connection string takes precedence for SQL Server or SQLite. Prefer managed identity in Azure. ASP.NET double-underscore form maps to `ConnectionStrings:DefaultConnection`. |
| `AZURE_SQL_WAKEUP_MAX_ATTEMPTS` | Optional positive integer; `8` | Stack A Azure SQL cold-start retry budget. Invalid numeric values are not fail-fast validated. |
| `AZURE_SQL_WAKEUP_INITIAL_DELAY_MS` | Optional positive milliseconds; `2000` | Stack A initial exponential retry delay. |
| `AZURE_SQL_WAKEUP_MAX_DELAY_MS` | Optional positive milliseconds; `15000` | Stack A retry-delay cap. Keep it at least the initial delay. |
| `AZURE_OPENAI_ENDPOINT` | Optional HTTPS URI; unset | Enables Stack A's local `/api/llm` proxy. Obtain from Azure OpenAI/Foundry resource **Keys and Endpoint**; auth uses `DefaultAzureCredential`, not an API key. |

### Browser-exposed Stack A build values

All values in this table are **browser-public** and are fixed when Stack A is
packaged. Rebuild and redeploy to change them.

| Variable | Status; values/default | Purpose and source |
| --- | --- | --- |
| `VITE_APP_AUTH_MODE` | Optional `simple`/`entra`; falls back to `APP_AUTH_MODE`, then `simple` | Chooses the React authentication provider. |
| `VITE_ENTRA_TENANT_ID` | Required when Vite auth is `entra`; falls back to `AZURE_TENANT_ID` | Tenant GUID from Entra **Overview**. |
| `VITE_ENTRA_STACK_A_CLIENT_ID` | Required when `entra`; falls back to `ENTRA_STACK_A_CLIENT_ID` | Public SPA application client ID. |
| `VITE_ENTRA_API_APP_CLIENT_ID` | Packaging requires it in `entra` mode but current client code does not read it | Public protected-API app ID. Keep aligned until the packaging contract is simplified. |
| `VITE_ENTRA_API_IDENTIFIER_URI` | Required when `entra`; falls back to `ENTRA_API_IDENTIFIER_URI` | Builds the delegated API scope. |
| `VITE_ENTRA_API_SCOPE` | Required when `entra`; falls back to `ENTRA_API_SCOPE`, then `access_as_user` | Scope value only, not a secret. |
| `VITE_TALENTMATCH_API_ORIGIN` | Optional origin; same origin | Overrides where the React client sends API calls; useful for split local hosts. |

## Microsoft Entra product authentication

These settings configure the product API and its selected public client(s). They are
separate from the delegated Microsoft 365 source connector.

| Variable | Status; values/default | Purpose, dependencies, scope, and source |
| --- | --- | --- |
| `AZURE_TENANT_ID` | Required for Entra auth and deployment; GUID | Product tenant from Entra **Overview** or `az account show --query tenantId`. |
| `ENTRA_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | With product Entra authentication, reuse the shared API and selected SPA(s). Requires the two API IDs plus one client ID per selected stack: three IDs for a single stack, four for both. False creates only that inventory. |
| `ENTRA_API_APP_CLIENT_ID` | Required at runtime in `entra`; GUID | Protected API application (client) ID. Terraform output or App registrations **Overview**. |
| `ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID` | Required only when `ENTRA_REUSE=TRUE`; GUID | Enterprise application object ID, not app object ID. Terraform output or `az ad sp show --id <client-id>`. |
| `ENTRA_API_IDENTIFIER_URI` | Required for Entra deployment and Stack B auth metadata | Protected API Application ID URI, usually `api://<id>`. App registration **Expose an API**. |
| `ENTRA_API_SCOPE` | Required by Stack A Entra runtime; Stack B default `access_as_user`; deployment default `access_as_user` | Delegated scope value, not the full URI. |
| `ENTRA_API_SCOPE_ID` | Required for deployment when product auth is `entra`; explicit UUID, no default | Delegated scope UUID from the API manifest, distinct from the scope value. Preserve existing IDs; for a new registration select and persist once before the first plan. Deployment-only, mapped to `TF_VAR_entra_api_scope_id`. |
| `ENTRA_STACK_A_CLIENT_ID` | Conditional non-secret GUID | Required for reuse when Stack A is selected. Stack A runtime accepts configured SPA clients; Stack B can additionally trust this ID when present but does not require it in a B-only deployment. |
| `ENTRA_STACK_B_CLIENT_ID` | Conditional non-secret GUID | Required for reuse when Stack B is selected and for Stack B runtime Entra authentication. Omit for Stack A-only deployments. |
| `ENTRA_STACK_A_REDIRECT_URIS` | Required by deployment when product auth is `entra` and Stack A is selected; CSV | Exact Stack A SPA redirects registered by Terraform. Include scheme and trailing path required by the app. |
| `ENTRA_STACK_B_REDIRECT_URIS` | Required by deployment when product auth is `entra` and Stack B is selected; CSV | Exact Stack B SPA redirects. |
| `ENTRA_ADMIN_APP_ROLE_ID` | Required for Entra deployment, together with all four role IDs; explicit distinct UUIDs, no defaults | Stable API role ID from the manifest. Preserve all existing deployed role IDs for both creation and reuse. |
| `ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID` | Same as above | Stable organization-admin role ID. |
| `ENTRA_RECRUITER_APP_ROLE_ID` | Same as above | Stable recruiter role ID. |
| `ENTRA_BUSINESS_PANEL_APP_ROLE_ID` | Same as above | Stable business-panel role ID. |
| `ENTRA_CREATE_ROLE_GROUPS` | Optional `TRUE`/`FALSE`; `FALSE` | Creates shared baseline Entra security groups and role assignments only when product auth is `entra`. Deployment-only. |
| `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID` | Required by Entra deployment/runtime; user object GUID | First administrator's Entra **user object ID**. Get with `az ad user show --id <upn> --query id`. |
| `ENTRA_BOOTSTRAP_ORGANIZATION_NAME` | Conditional non-secret text | Required by the post-deployment admin data seed. Organization to create/adopt. |
| `ENTRA_BOOTSTRAP_DEPARTMENT_NAME` | Conditional non-secret text | Required by the post-deployment admin data seed. Department to create/adopt. |

No SPA client secret is supported or required.

## AWReason and scoring

| Variable | Status; values/default | Purpose, dependencies, scope, and source |
| --- | --- | --- |
| `AWR_SEQ_API_ENDPOINT` | Required by deployments that include a stack; no runtime default | Sequential AWReason base URL for extraction, prompt operations, tests, and sequential scoring. Obtain from the AWReason deployment/API provider. Missing values fail deployment; individual runtime operations also fail. |
| `AWR_PLATFORM_API_ENDPOINT` | Optional URI; unset | A different non-empty URL enables asynchronous platform scoring. Unset or equal to `AWR_SEQ_API_ENDPOINT` means sequential mode. |
| `AWR_AUTH_MODE` | Optional `none`/`apikey`/`entra`; `none` | Outbound AWReason authentication. Startup rejects unknown modes and missing mode-specific values. Use `none` only for trusted local development. |
| `AWR_API_KEY` | Conditional **secret**; unset | Required for `apikey`. Store in Key Vault/GitHub secret or obtain from the AWReason API provider. Never expose as `VITE_*`. |
| `AWR_AAD_AUDIENCE` | Conditional non-secret URI/scope; unset | Required for `entra`; passed to `DefaultAzureCredential.GetToken`. Obtain from the AWReason app registration/API owner. |
| `AWR_AAD_ISSUER` | Template compatibility setting; unset | **Currently not consumed by either stack.** Token acquisition uses `AWR_AAD_AUDIENCE` and `DefaultAzureCredential`; do not rely on this value for issuer validation. |
| `AWR_API_CLIENT_ID` | Conditional non-secret GUID; unset | Deployment requires it with `AWR_AUTH_MODE=entra` so Terraform can assign managed identities the AWReason role. App registration/API provider. |
| `AWR_API_APP_ROLE_VALUE` | Optional text; `TalentMatch.Access` | AWReason application-role value assigned by Terraform; must exist on `AWR_API_CLIENT_ID`. |
| `AWR_API_TIMEOUT_MS` | Optional positive integer; `360000` | Stack A outbound sequential request timeout. Non-positive/invalid input falls back to the default. |
| `AWR_MODEL_ID` | Recommended explicit model/deployment ID; scoring-profile default `passthrough-llm` | Forwarded to AWReason and snapshotted into prompts. Obtain from the AWReason provider/deployment. Changing it blocks production scoring until a matching prompt version is tested and approved. |
| `AWR_REASONING_LEVEL` | Optional provider-supported reasoning value; scoring-profile default `medium` | Snapshotted with the model; changing it has the same approval implications. |
| `AWR_MODEL`, `AWR_REASONING` | Compatibility aliases; primary names above win | Supported by both scoring-profile readers but should not be used for new configuration. |
| `AWR_MAX_PARALLEL` | Optional positive integer; `1` | Per-instance sequential production document slots. Stack B fails startup on invalid values; Stack A clamps/falls back to at least one. Prompt tests have a separate override. |
| `AWR_SCORING_LEASE_SECONDS` | Optional whole number of seconds from `60` to `1800`; `240` (4 minutes) | Stack B only. How long a document being scored stays reserved to its host before a sweep fails it with "Sequential scoring lease expired". The host renews it every 20 s and retries a failed renewal (5 attempts, 2 s doubling to 30 s), then keeps trying at each heartbeat while the lease is still valid. Stack B fails startup on an invalid value. Stack A has no equivalent lease and ignores it. |
| `AWR_PLATFORM_BATCH_SIZE` | Optional positive integer; `2` | Applications per platform submission. Platform mode only. |
| `AWR_PLATFORM_RECONCILE_INTERVAL_MS` | Optional integer; `15000`, minimum `2000` | Platform reconciler tick, both stacks. |
| `AWR_PLATFORM_MAX_INFLIGHT_PER_TICK` | Optional integer; `50`, minimum `1` | Maximum due batches considered per tick, per replica. |
| `AWR_PLATFORM_LEASE_SECONDS` | Optional integer; `60`; minimum Stack A `30`, Stack B `10` | SQL row lease for reconciler ownership. See mismatch note before tuning near the minimum. |
| `AWR_PLATFORM_SUBMIT_TIMEOUT_S` | Optional integer seconds; `60` | Per-submit timeout, platform mode. Use a positive value. |
| `AWR_PLATFORM_POLL_TIMEOUT_S` | Optional integer seconds; `30` | Per-poll timeout, platform mode. Use a positive value. |
| `AWR_PLATFORM_DEFAULT_POLL_DELAY_MS` | Optional integer; `10000`, minimum `5000` | Poll delay when the platform does not return one. |
| `AWR_BLOB_STORAGE_ACCOUNT` | Required in platform mode; account name | Enables managed-identity Blob storage. Obtain from the Storage account or Terraform output. Sequential mode keeps documents inline. |
| `AWR_BLOB_CONTAINER` | Optional container name; `cv-uploads` | Platform exchange container. Both identities need data-plane RBAC; SAS is not supported. |
| `PROMPT_TESTRUN_RUN_COUNT` | Optional positive integer; `1` | Stack B scoring runs per prompt test. |
| `PROMPT_TESTRUN_MAX_PARALLEL` | Optional positive integer; falls back to `AWR_MAX_PARALLEL`, then `4` | Stack B prompt-test parallelism; it does not consume the production slot cap. |
| `PROMPT_TESTRUN_SCORING_STALE_MINUTES` | Optional positive integer; `10` | Stack B age used to reconcile apparently stuck test scoring. |

### Stack B sequential scoring finalization

Aggregation is computed before opening a database transaction. Existing aggregate lookup and
result staging also run outside the transaction. A single conditional application update checks
the scoring owner and unexpired lease, publishes the terminal status, score and candidate name,
and releases ownership. All scoring runs and the aggregate are then persisted with one batched
save in the same transaction, so a failed write cannot expose a partial terminal result.

SQL Server deadlocks (error `1205`) replay the complete database attempt, not the AI scoring call.
There are at most six retries after the initial attempt. Exponential backoff uses equal jitter:
50-100 ms initially, doubling to a 1,000 ms ceiling per wait, with less than 3.5 seconds of total
backoff. Each attempt reloads the aggregate and rechecks current ownership and lease validity.
Cancellation interrupts the wait; exhausted and permanent failures propagate to the existing
worker failure handling. Other transient SQL errors retain the existing six-retry/15-second
maximum-delay policy. These retry limits are code-level defaults, not environment settings.

## Delegated Microsoft 365 connector

The connector exists in Stack B only. It uses a separate multi-tenant
confidential Web application, authorization code + PKCE, delegated scopes,
managed-identity client assertion, and request-bound encrypted authorization.
Do not configure a client secret, certificate, application permission, site
grant, tenant allow-list, host allow-list, or fallback identity.

| Variable | Status; values/default | Purpose, dependencies, scope, and source |
| --- | --- | --- |
| `MICROSOFT365_SOURCE_MODE` | Optional `off`/`mock`/`graph`; default `mock` in ASP.NET Development, otherwise `off` | Activates connector services and workers. Invalid values fail startup. `mock` is local deterministic testing only. |
| `MICROSOFT365_SOURCE_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Deployment-only: adopt an existing connector registration. Requires its client ID. |
| `MICROSOFT365_SOURCE_CLIENT_ID` | Required in `graph`; GUID | Separate connector app client ID. Obtain from Terraform `source_connector_client_id` output or the connector app registration. |
| `MICROSOFT365_SOURCE_REDIRECT_URIS` | Conditional deployment CSV; empty | Exact Web callback URLs used when Terraform creates/adopts the connector. Include Stack B callback and any deliberately enabled future Stack A callback. |
| `MICROSOFT365_SOURCE_CALLBACK_PATH` | Optional local absolute path; `/api/source-connections/callback` | Runtime OAuth callback. Must start with `/` and must not be an absolute URI. Must match a registered redirect URI after host composition. |
| `MICROSOFT365_SOURCE_ADMIN_CONSENT_CALLBACK_PATH` | Optional local absolute path; `/api/source-connections/admin-consent-callback` | Runtime admin-consent callback with the same path restrictions. |
| `MICROSOFT365_SOURCE_AUTHORITY` | Required in `graph`; HTTPS URI ending `/organizations` | Normally `https://login.microsoftonline.com/organizations`; commercial multi-tenant organizational authority. |
| `MICROSOFT365_SOURCE_SCOPES` | Optional but exact; `openid profile offline_access Files.Read.All Sites.Read.All` | Space/comma/semicolon input is accepted, but startup requires exactly this canonical order and no extra scope. |
| `MICROSOFT365_SOURCE_PUBLISHER_VERIFIED` | Conditional boolean | Production activation requires explicit `true`; Development/QA do not. The gate uses ASP.NET environment, not `PRODUCT_ENVIRONMENT`. Verify publisher status in the connector app registration. |
| `MICROSOFT365_SOURCE_MAX_CONCURRENCY` | Optional integer `1..64`; `4` | Maximum concurrent Graph source calls per app instance. |
| `MICROSOFT_GRAPH_BASE_URI` | Profile/Terraform input; empty | **Currently not consumed by runtime**; Graph is hard-coded to commercial `https://graph.microsoft.com/v1.0/`. See mismatch note. |
| `PRODUCT_ENVIRONMENT` | Conditional `Development`/`QA`/`Production`; falls back to ASP.NET runtime environment | Controls connector rollout/publisher activation independently of the App Service runtime environment. Azure App Service normally runs with `ASPNETCORE_ENVIRONMENT=Production`; set `PRODUCT_ENVIRONMENT=QA` for controlled QA so publisher verification may remain false. `PRODUCT_ENVIRONMENT=Production` requires explicit publisher verification. |
| `BULK_AUTH_KEY_URI` | Required in `graph` unless a non-production dev key path is used; non-secret Key Vault key URI | Versionless Key Vault key URI used to wrap per-request authorization data keys. Terraform creates the RSA key through the ARM control plane, so the deployment runner does not need Key Vault data-plane network access. When the delegated connector is enabled, shared infrastructure requires VNet integration and creates a Key Vault private endpoint plus `privatelink.vaultcore.azure.net` DNS link in the existing private-endpoint subnet. Runtime managed identities receive key-scoped `Key Vault Crypto User`. |
| `BULK_AUTH_DEV_KEY_PATH` | Optional path to **secret key material**; unset; forbidden in Production | Local persistent 32-byte base64 AES key file. The runtime creates it if absent. Keep the file ignored and access-restricted. Without a persistent path, restarts invalidate stored mock/development authorizations. |
| `BULK_AUTH_KEY_NAME` | Optional deployment-only Key Vault key name; `bulk-auth` | Used when provisioning the wrapping key. Set the profile source variable, not `TF_VAR_bulk_auth_key_name`. |

## Bulk scoring limits

All variables here are Stack B runtime settings. Omission uses the exact
`BulkScoringOptions` default. Invalid parsed values fail startup. Limits are per
app instance/request as described, so scale-out multiplies worker concurrency.

### Database transaction boundaries

Bulk import serializes short SQL mutations per request by acquiring the request
row's update lock before touching items, content, audit rows, or counts.
Multi-request operations acquire these locks in request-ID order. SQL Server uses
read-committed isolation instead of serializable audit scans. Network downloads,
rasterization, and the sequential scoring API call are outside these transactions.
The scoring lease is persisted ownership metadata, not a database lock held while
the API responds. Heartbeats commit separate short lease updates.

Item and audit writes remain atomic. Request counts are reconciled in that same
transaction when status or size changes, not for audit-only writes. Deadlock
retries reload committed handoff state and track existing content parents without
re-inserting them. Repeated committed handoffs do not requeue an application.

Deploy the `20261020150000_AddBulkAuditLookupIndex` EF migration with the updated
Stack B code. It adds the nonunique `(SubjectId, EventType)` audit lookup index on
both providers. The targeted Azure SQL script
[`add-bulk-audit-lookup-index.sql`](infra/scripts/sql/add-bulk-audit-lookup-index.sql)
and the SQL Server migration both check for an existing index, so applying the
script before the migration is safe. Repeated lease/count audit events
remain allowed. Do not use increased lease timeouts or SQL `NOLOCK` as deadlock fixes.

To run the opt-in SQL Server persistence tests, set
`BULK_SCORING_TEST_SQLSERVER` to a connection string whose login may create/drop
isolated test databases. The suite creates uniquely named `TalentMatchDeadlock_*`
databases and removes only those databases. Never point it at a production service.
The SQL fixture uses the shared SQL schema and delegated bulk upgrade scripts.

### What a discovery step means

A discovery **step** is one bounded, resumable unit of recursive folder
traversal. It is **not** one folder, one Microsoft Graph page, one file, or the
whole bulk request. A step starts or resumes from the server-held continuation
cursor and stops at the first of:

1. the eligible-file cap (`BULK_DISCOVERY_STEP_FILES`, default `1000`);
2. the scanned-entry cap (`BULK_DISCOVERY_STEP_MAX_ENTRIES`, default `10000`);
3. the elapsed-time cap (`BULK_DISCOVERY_STEP_MAX_SECONDS`, default `45`);
4. source exhaustion; or
5. the cumulative request file or byte limit.

At the boundary, the server atomically persists newly eligible items,
excluded/unreadable tallies, and the continuation cursor. **Discover more**
starts the next step from that cursor; it does not rescan the completed portion.

An **eligible file** passed type, size, and source-policy checks and can become a
bulk item. A **scanned entry** is any provider item inspected during traversal,
including folders and files later excluded or found unreadable. Consequently, a
folder tree with few usable CVs can reach the entry/time cap long before the
eligible-file cap.

- Increasing `BULK_DISCOVERY_STEP_FILES` commits more eligible items per step
  and reduces user round trips, but increases transaction size, response work,
  and the amount presented after one action. Decreasing it creates smaller,
  more frequent checkpoints.
- Increasing `BULK_DISCOVERY_STEP_MAX_ENTRIES` makes sparse/deep trees progress
  farther per action, at the cost of more Graph calls and server work before a
  checkpoint. Decreasing it bounds provider scanning more tightly and may
  require more **Discover more** actions.
- Increasing `BULK_DISCOVERY_STEP_MAX_SECONDS` gives slow or throttled sources
  more time per step, but lengthens request latency and resource occupancy.
  Decreasing it improves latency bounds while producing more resumptions.

| Variable | Status; valid range; default | Impact and dependencies |
| --- | --- | --- |
| `BULK_AUTH_MAX_AGE_HOURS` | Optional integer `1..2147483647`; `72` | Maximum age of stored delegated authorization before the request lapses. |
| `BULK_SOURCE_IDLE_TTL_MINUTES` | Optional integer `1..120`; `30` | Pending source connection idle expiry. Must be less than the absolute TTL. |
| `BULK_SOURCE_ABSOLUTE_TTL_MINUTES` | Optional integer `1..1440`; `120` | Absolute pending-connection expiry; must exceed idle TTL. |
| `BULK_PENDING_CONNECTIONS_PER_ACTOR` | Optional integer `1..100`; `10` | Pending source connections allowed per product actor. |
| `BULK_CONNECTION_STARTS_PER_ACTOR_PER_MINUTE` | Optional integer `1..1000`; `10` | Per-actor connection-start admission rate. |
| `BULK_CONNECTION_STARTS_PER_IP_PER_MINUTE` | Optional integer `1..1000`; `30` | Per HMAC-derived client-address subject start rate. |
| `BULK_CALLBACKS_PER_IP_PER_MINUTE` | Optional integer `1..5000`; `60` | Per client-address callback admission rate. |
| `BULK_DISCOVERY_STEP_FILES` | Optional integer `1..1000`; `1000` | Eligible files committed per resumable discovery step. |
| `BULK_DISCOVERY_STEP_MAX_ENTRIES` | Optional integer `1..2147483647`; `10000` | Provider entries inspected per discovery step. |
| `BULK_DISCOVERY_STEP_MAX_SECONDS` | Optional integer `1..2147483647`; `45` | Wall-clock target for one discovery step. |
| `BULK_MAX_TOTAL_BYTES` | Optional integer bytes `1048576..1099511627776`; `53687091200` | Aggregate request limit (default 50 GiB). Per-file limit is fixed at 8 MiB and is not environment-configurable. |
| `BULK_IMPORT_CONCURRENCY` | Optional integer `1..64`; `4` | Concurrent import workers. |
| `BULK_CONVERT_CONCURRENCY` | Optional integer `1..64`; `2` | Concurrent document conversions. |
| `BULK_MAX_ATTEMPTS` | Optional integer `1..20`; `5` | Retry exhaustion threshold per bulk item. |
| `BULK_RASTER_DPI` | Optional integer `72..600`; `150` | Initial PDF raster resolution. |
| `BULK_RASTER_JPEG_QUALITY` | Optional integer `1..100`; `80` | Raster JPEG quality. |
| `BULK_RASTER_MAX_PAGES` | Optional integer `1..1000`; `30` | Maximum pages rasterized per document. |
| `BULK_ADMISSION_HMAC_KEY_URI` | Optional URI; unset | **Parsed and passed through Terraform, but not consumed.** Admission currently generates a random 32-byte key at each process start. Do not claim this setting provides stable admission subjects; see mismatch note. |

Raster timeout is fixed at 120 seconds; it has no environment variable.

## Azure deployment profiles

### Core and resource ownership

| Variable | Status; values/default | Purpose, dependencies, and source |
| --- | --- | --- |
| `AZURE_SUBSCRIPTION_ID` | Required GUID | Deployment subscription; Azure portal **Subscriptions** or `az account show --query id`. Active CLI context must match. |
| `AZURE_LOCATION` | Required by script; `australiaeast` in templates/Terraform | Azure region name, for example `australiaeast`. Check service availability before changing. |
| `ENVIRONMENT` | Required `dev`/`test`/`prod` | Resource suffix and Terraform workspace component. Must match the explicit deploy environment argument. |
| `RESOURCE_GROUP` | Required name | Managed/reused resource group. Azure portal/CLI or naming policy. |
| `PROJECT_NAME` | Optional Azure-safe prefix; `talentmatch` | Resource naming prefix. Changing it may create replacements. |
| `STACK_TARGET` | Optional `shared-only`/`stack-a`/`stack-b`/`both`; `both` | Defines the shared provisioning inventory, including selected SPAs, identities and runtime grants. Command targets must be a subset of the profile. |
| `APP_SERVICE_PLAN_SKU` | Optional valid App Service SKU; `B1` | Shared plan SKU (`B1` local template, `B2` QA, `S1` production). Validate region quota/pricing. |
| `AZ_APP_SERVICE_PLAN_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Reuse plan. True requires `AZ_APP_SERVICE_PLAN_NAME` and `AZ_APP_SERVICE_PLAN_RG`. |
| `AZ_APP_SERVICE_PLAN_NAME`, `AZ_APP_SERVICE_PLAN_RG` | Conditional text | Existing plan coordinates from portal/CLI. |
| `AZ_SQL_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Reuse SQL. True requires `SQL_SERVER_NAME`, `SQL_DATABASE_NAME`, and `SQL_RG`. False requires explicit creation approval and Entra admin. |
| `ALLOW_CREATE_AZURE_SQL` | Conditional `TRUE`; `FALSE` | Destructive-cost guard required whenever `AZ_SQL_REUSE` is not true. |
| `AZ_SQL_BOOTSTRAP_ENABLED` | Optional `TRUE`/`FALSE`; `TRUE` | Runs post-provision Entra database-user/schema bootstrap. Disable only when the operator host cannot reach reused private SQL, then run bootstrap from a connected host. |
| `SQL_SERVER_NAME`, `SQL_DATABASE_NAME`, `SQL_RG` | Conditional text | Existing SQL coordinates from portal/CLI. These are deployment names; runtime uses FQDN/name outputs. |
| `SQL_ADMIN_LOGIN` | Optional text; `sqladmin` | SQL administrator login used only when applicable. |
| `SQL_ADMIN_PASSWORD` | Optional **secret**; empty | Password admin secret. Empty supports Entra-only administration. Use Key Vault/GitHub secret if required. |
| `SQL_AAD_ADMIN_LOGIN`, `SQL_AAD_ADMIN_OBJECT_ID` | Required when creating SQL | Entra SQL administrator display/login and object GUID; get from Entra user/group and verify with Azure CLI. |
| `AZ_KEY_VAULT_ENABLED` | Optional `TRUE`/`FALSE`; `FALSE` | Include Key Vault and secret references. |
| `AZ_KEY_VAULT_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Requires Key Vault enabled plus `AZ_KEY_VAULT_NAME` and `AZ_KEY_VAULT_RG`. |
| `AZ_KEY_VAULT_NAME`, `AZ_KEY_VAULT_RG` | Required when reuse is true; optional create-time overrides when reuse is false | With `AZ_KEY_VAULT_REUSE=TRUE`, identify the existing vault from portal/CLI. With reuse false, nonblank values set the name and existing target resource group for the newly managed vault; blank values create `kv-<project>-<environment>` in `RESOURCE_GROUP`. A custom target resource group must already exist. Changing either value after creation forces vault replacement, so review the plan and migrate any existing keys/secrets before applying. |
| `USE_KEY_VAULT_SECRET_REFS` | Optional `TRUE`/`FALSE`; when vault enabled defaults `TRUE` in prod, `FALSE` otherwise; forced false when disabled | Chooses App Service Key Vault references rather than direct `AWR_API_KEY`. |
| `AZ_APIM_ENABLED` | Optional `TRUE`/`FALSE`; `FALSE` | Provision/use API Management. |
| `AZ_APIM_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | If APIM is enabled, true requires `AZ_APIM_NAME` and `AZ_APIM_RG`. |
| `AZ_APIM_NAME`, `AZ_APIM_RG` | Conditional text | Existing APIM coordinates. |
| `AZ_APP_INSIGHTS_ENABLED` | Optional `TRUE`/`FALSE`; `FALSE` | Enable backend App Service autoinstrumentation for the selected stacks. False skips lookup and removes monitoring settings; it also schedules any monitoring resources owned by this deployment for destruction. |
| `AZ_APP_INSIGHTS_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | When enabled, true looks up an existing component; false creates one workspace-backed component shared by both stacks. Ignored when disabled. |
| `AZ_APP_INSIGHTS_NAME`, `AZ_APP_INSIGHTS_RG` | Required when enabled and reused | Existing Application Insights coordinates in the deployment subscription; its resource group may differ from `RESOURCE_GROUP`. Ignored in create mode. |
| `APIM_SKU` | Optional Terraform SKU; `Consumption_0` | Templates use `Consumption_0` local, `Developer_1` QA, `Basic_1` production. |
| `APIM_PUBLISHER_NAME` | Optional text; `TalentMatch` | APIM publisher display name. |
| `APIM_PUBLISHER_EMAIL` | Optional email; `admin@example.com` | APIM operational contact; replace placeholder for real deployments. |
| `AZ_IDENTITIES_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Reuse managed identities for the stacks selected by `STACK_TARGET`. A single-stack deployment requires only that stack's identity. |
| `AZ_IDENTITY_STACK_A_NAME`, `AZ_IDENTITY_STACK_B_NAME`, `AZ_IDENTITIES_RG` | Conditional text | When identity reuse is enabled, provide `AZ_IDENTITIES_RG` plus `AZ_IDENTITY_STACK_A_NAME` only if `STACK_TARGET` includes `stack-a`, and `AZ_IDENTITY_STACK_B_NAME` only if it includes `stack-b`. `shared-only` requires neither stack identity. |

### Application Insights

Reuse an existing shared component:

```env
STACK_TARGET=both
AZ_APP_INSIGHTS_ENABLED=TRUE
AZ_APP_INSIGHTS_REUSE=TRUE
AZ_APP_INSIGHTS_NAME=<existing-component-name>
AZ_APP_INSIGHTS_RG=<existing-monitoring-resource-group>
```

To create monitoring instead, set `AZ_APP_INSIGHTS_REUSE=FALSE` and leave the
coordinates blank. The shared root creates `appi-<project>-<environment>` and a
`law-<project>-<environment>` Log Analytics workspace in `RESOURCE_GROUP`, with
30-day workspace retention and Entra-only telemetry ingestion. The Application
Insights module requires AzureRM 4.81+ within the existing 4.x provider line.

Both stacks receive the same connection string and use their own user-assigned
managed identities. The shared root manages component-scoped **Monitoring Metrics
Publisher** assignments for the identities selected by `STACK_TARGET`. The
deployment identity needs permission to create these role assignments, including
on a reused component in another resource group; read access alone is insufficient.
Reused components and their workspaces are read-only lookups and are never
created, imported, reconfigured, or deleted by this module.

The Linux App Service agent is enabled without adding application SDKs. Each
stack has its own App Service name/default cloud role and `OTEL_SERVICE_NAME`.
Browser instrumentation is explicitly disabled. Node.js App Service
autoinstrumentation is currently public preview; confirm that this is acceptable
for production. Network/firewall/private-link rules must allow ingestion.
This does not collect all application `console`/file logs or instrument arbitrary
business operations. Do not add CV contents, prompt bodies, tokens, or other
sensitive recruiting data as telemetry.

Changing either ownership flag is not a migration: disabling schedules destruction
of a component/workspace managed by this deployment, and switching from create
to reuse can also schedule the formerly managed resources for destruction.
Review the plan and explicitly migrate/detach state before changing ownership of
a resource that must be retained. Reused components remain untouched; only this
deployment's publisher role assignments are removed when disabled.

For CI deployments, include these settings in the complete `DEPLOYMENT_ENV_FILE`
GitHub **environment secret**. Separate GitHub variables no longer override the
profile. Never supply an unchanged example template as that secret.
Do not supply connection strings manually in profiles. A `stack-b-only` run
requires the enabled shared component and its output to have been applied first.
Use a normal `both` deployment to change monitoring for both existing hosts.

After deployment/restart, send one API request to each stack and verify new
requests/dependencies in the chosen resource under both distinct cloud roles.
Verify disabling in a reviewed plan before applying it; existing shared
components must not appear as resource deletions.

References: [App Service monitoring](https://learn.microsoft.com/azure/app-service/monitor-app-service#application-insights),
[managed-identity ingestion](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication).

### Network controls

| Variable | Status; values/default | Purpose and dependencies |
| --- | --- | --- |
| `AZ_VNET_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Enables App Service VNet integration with an existing VNet; VNet creation is not supported. |
| `AZ_VNET_NAME`, `AZ_VNET_RG` | Required when VNet reuse is true | Existing VNet coordinates. |
| `AZ_INTEGRATION_SUBNET_NAME` | Conditional subnet name | Reuse an existing delegated subnet. Mutually exclusive with CIDR. |
| `AZ_INTEGRATION_SUBNET_CIDR` | Conditional CIDR, prefix `/26` or larger network | Create a delegated subnet in the reused VNet. Mutually exclusive with subnet name. |
| `AZ_SQL_PRIVATE_ENDPOINT_REUSE` | Optional `TRUE`/`FALSE`; `FALSE` | Asserts that SQL private endpoint/DNS already exists. If false while VNet is enabled, private-endpoint subnet CIDR is required. |
| `AZ_SQL_PRIVATE_ENDPOINT_SUBNET_NAME` | Optional name; `snet-sql-private-endpoints` | Dedicated private endpoint subnet name. |
| `AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR` | Conditional CIDR; empty | Required when VNet is enabled and an existing SQL private endpoint is not reused. |
| `AZ_ALLOWED_IPS` | Required with VNet; comma-separated IPs; empty | App Service public allow-list. Use operator/public egress addresses; all other traffic is denied. |

## Tests, packaging, and administration

### Stack B dependency and runtime security

`global.json` selects the stable .NET SDK 10.0.402 feature band, with patch
roll-forward only. Local builds and the deployment workflow use that same SDK
selection. `dotnet/Directory.Build.props` aligns Microsoft .NET 10 package
references to 10.0.13 and audits direct and transitive dependencies at every
severity. `NuGet.Config` explicitly selects the public NuGet vulnerability
database without changing package download sources. Advisories and unavailable
audit data fail restore/build rather than being suppressed.

The Stack B packager forces an audited restore of the entire solution before
publishing. CI also runs the solution tests before uploading the artifact.
For a local audit, run:

```powershell
dotnet restore dotnet/TalentMatch.slnx --force
dotnet list dotnet/TalentMatch.slnx package --vulnerable --include-transitive --no-restore
```

Stack B remains framework-dependent. Its published runtime configuration requires
.NET and ASP.NET Core 10.0.13 or a later 10.0 patch; it must not run on an older
runtime. App Service's `10.0` stack setting does not select a particular patch.
Verify the selected runtime on the target host before republishing. If the host
has not received a patched runtime, do not lower the application's minimum to
make deployment succeed. A clean NuGet audit covers known package advisories,
not unknown vulnerabilities or the host operating system.

| Variable | Status; values/default | Purpose and handling |
| --- | --- | --- |
| `E2E_STACK_A_BASE_URL` | Profile metadata; local template `http://127.0.0.1:5173` | **Currently no checked-in test reads it.** Reserved Stack A browser target. |
| `E2E_STACK_B_BASE_URL` | Optional generally; required by Stack B E2E/capture; `http://127.0.0.1:5000` | Running Stack B URL. Playwright config uses it as base URL. |
| `E2E_STACK_B_AUTH_STATE` | Conditional **secret-bearing file path**; unset | Required for Entra Stack B E2E. Points to ignored Playwright/MSAL state generated by `npm run test:e2e:auth`. |
| `E2E_STACK_B_USERNAME` | Conditional non-secret account name | Required by the simple-mode capture helper only. |
| `E2E_STACK_B_PASSWORD` | Conditional **secret** | Required by the simple-mode capture helper only. |
| `E2E_JOB_SPEC_FILE` | Conditional path | Existing document required by the Stack B onboarding E2E. |
| `E2E_ENTRA_ACCESS_USER_OBJECT_ID` | Profile metadata; empty | Currently not consumed by checked-in runtime/tests; intended tenant-verified fixture ID. |
| `E2E_ENTRA_ORGANIZATION_ID` | Profile metadata; empty | Currently not consumed. |
| `E2E_ENTRA_DEPARTMENT_IDS` | Profile metadata CSV; empty | Currently not consumed. |
| `STACK_A_INSTALL_MODE` | Optional `auto`/`clean`; `auto` | Stack A packager reuses current `node_modules` when valid or forces `npm ci`. CI sets `clean`. |
| `PROJECT_ROOT` | Optional path; Vite config directory | Advanced Vite root override. Normally leave unset. |
| `TALENTMATCH_ENV_FILE` | Optional test path; `.env_qa` | Selects the profile used by the Entra admin seed unit test. |
| `ENTRA_ROLE_OPERATOR_OBJECT_ID` | Optional audit actor ID; `operator-cli` in wrapper | Actor recorded by the role-management helper. |

The admin seed helper derives `ENTRA_BOOTSTRAP_ADMIN_USERNAME`,
`ENTRA_BOOTSTRAP_ADMIN_FULL_NAME`, and `ENTRA_BOOTSTRAP_ADMIN_EMAIL` from
Microsoft Graph. Role-management helpers similarly derive/pass
`ENTRA_TARGET_USERNAME`, `ENTRA_TARGET_FULL_NAME`, and `ENTRA_TARGET_EMAIL`.
They are process-local adapters, not profile inputs.

## Platform-managed and generated variables

Do not add these to operator profiles unless diagnosing the owning platform:

| Variable/group | Owner and behavior |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT` | ASP.NET host (`Development`, `Testing`, `QA`, `Production`). Controls Stack B safety checks and connector publisher gate. Azure normally supplies Production semantics. |
| `AZURE_CLIENT_ID` | Terraform supplies the user-assigned identity client ID in Azure; local developers may set it only to select a particular credential. |
| `WEBSITE_NODE_DEFAULT_VERSION` | Terraform sets `~22` for Stack A App Service. |
| `SCM_DO_BUILD_DURING_DEPLOYMENT`, `ENABLE_ORYX_BUILD` | Terraform/deploy script force `false`; artifacts are prebuilt. |
| `KEY_VAULT_URI`, `APIM_GATEWAY_URL` | Terraform output app settings. Currently injected for platform integration; application code does not directly read them. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING`, `APPLICATIONINSIGHTS_AUTHENTICATION_STRING` | Derived shared connection string and stack-specific managed-identity authentication. Present only when monitoring is enabled; consumed by the App Service agent. |
| `ApplicationInsightsAgent_EXTENSION_VERSION`, `APPINSIGHTS_JAVASCRIPT_ENABLED`, `OTEL_SERVICE_NAME`, `XDT_MicrosoftApplicationInsights_*` | Terraform-managed Linux agent, browser opt-out, service name and ASP.NET Core agent settings. Do not override them independently in the portal or initialize a duplicate SDK. |
| `ARM_CLIENT_ID`, `ARM_TENANT_ID`, `ARM_SUBSCRIPTION_ID`, `ARM_USE_OIDC` | GitHub/Terraform provider authentication; GitHub OIDC workflow-managed. |
| `TF_VAR_*` | Generated from profile keys or Terraform outputs. Never the authoritative operator input. |
| `STACK_A_IDENTITY_NAME`, `STACK_B_IDENTITY_NAME`, `SQL_SERVER_FQDN`, `SQL_TOKEN_FILE` | Short-lived SQL bootstrap adapters generated/derived by deployment scripts. |
| `BUILD_VERSION`, `BUILD_CREATED_AT_UTC`, `GIT_SHA` | Packagers generate artifact diagnostics; external values are not honored as configuration. |
| `CI`, `GITHUB_*`, `INPUT_*`, `CHANGED_*`, `GITHUB_OUTPUT` | GitHub Actions/Playwright process internals. |

GitHub deployments require environment secrets `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` for OIDC login, plus
`DEPLOYMENT_ENV_FILE` containing the full approved profile, including any
Stack A packaging inputs. Its tenant/subscription and `ENVIRONMENT` must match
the selected deployment. A missing or empty profile secret fails closed.

## Removed variables

The old application-only/site-allow-list connector has been removed. **Do not
set or reintroduce any `SHAREPOINT_*`, `ONEDRIVE_ALLOWED_*`, or `BULK_LIVE*`
variable.** This includes former mode, reader credential, allowed host/root,
site-grant, and live-test switches. They cannot activate the delegated connector
and may indicate an obsolete profile or runbook. Migrate to
`MICROSOFT365_SOURCE_*` plus `BULK_*`.

`AWR_MODEL` and `AWR_REASONING` remain compatibility aliases, but the `_ID` and
`_LEVEL` names are authoritative. `AZURE_SQL_CONNECTION_STRING` remains a legacy
Stack A escape hatch; managed identity is preferred.

## Validation and operational changes

Startup and deployment fail fast in these important cases:

- deployment profile tenant/subscription differs from active Azure CLI context;
- reuse flags lack required coordinates, SQL creation lacks explicit approval,
  or VNet choices conflict;
- `APP_AUTH_MODE=entra` lacks product API/client/bootstrap inputs;
- AWReason auth mode is invalid or lacks its API key/audience;
- Stack B uses simple auth outside Development/Testing;
- Microsoft 365 graph mode lacks client/authority/wrapping key, uses extra scopes,
  uses a non-organizations authority, or supplies a dev key in Production;
- any parsed bulk value is outside its range or idle TTL is not below absolute TTL.

Most server values are captured at process startup (many are static fields or
module constants). Restart every affected local server after changing `.env`.
Azure App Service restarts automatically after app-setting changes. Rebuild
Stack A for every `VITE_*` change. Terraform/deployment-only changes require a
new plan/apply; identity/RBAC, Key Vault key, and connector app-registration
changes may also require consent or role-propagation time. Changes to model or
reasoning profile require a new tested/approved prompt version before production
scoring resumes. Changing the local bulk wrapping key invalidates outstanding
authorizations.

## Known implementation mismatches

These settings need code or template follow-up; this reference describes current
behavior rather than intended behavior:

1. `BULK_ADMISSION_HMAC_KEY_URI` is parsed and deployed but never dereferenced.
   Each Stack B process generates an ephemeral admission HMAC key, so subjects
   change on restart and differ across replicas.
2. `MICROSOFT_GRAPH_BASE_URI` is passed through deployment but Graph calls and
   token scopes are hard-coded to commercial Microsoft Graph.
3. The four checked-in templates expose only a subset of active bulk tunables:
   `BULK_AUTH_MAX_AGE_HOURS`, `BULK_MAX_ATTEMPTS`, `BULK_RASTER_JPEG_QUALITY`, and
   `BULK_RASTER_MAX_PAGES` are runtime-supported but are not wired through the
   current Terraform Stack B root.
4. Platform-mode, blob, model/reasoning, timeout, prompt-test, and Stack A SQL
   retry tunables are runtime-supported but mostly absent from deployment
   profile-to-Terraform wiring; set them directly as App Service settings until
   infrastructure inputs are added.
5. `E2E_STACK_A_BASE_URL` and the three `E2E_ENTRA_*` fixture IDs are declared
   by templates but have no checked-in consumer.
6. Stack A and Stack B enforce different minimums for
   `AWR_PLATFORM_LEASE_SECONDS` (30 versus 10).
7. Some Stack B extraction call paths fall back to model `o3`/reasoning `high`
   while the shared scoring-profile provider falls back to
   `passthrough-llm`/`medium`; set `AWR_MODEL_ID` and `AWR_REASONING_LEVEL`
   explicitly to avoid drift.
8. `AWR_AAD_ISSUER` is advertised in the general/production templates but has
   no runtime reader.
