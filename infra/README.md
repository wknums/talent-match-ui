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
├── app-insights/       # Optional shared Application Insights; create or reuse
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

Each non-comment, non-blank line must be a `KEY=VALUE` assignment. Variable names
must start with a letter or underscore and contain only letters, digits, and
underscores. Empty values and whitespace around assignments are supported.
The shared loader stops at the first malformed entry and reports the supplied
file path, physical line number (including comments and blank lines), and reason:

```text
[ERROR] Invalid environment entry at .env.azure:42: invalid variable name; use letters, digits, and underscores, starting with a letter or underscore.
```

Open line 42 in the supplied environment file to correct the entry. Parsing and
export errors, including unmatched quotes and read-only variables, also report
their source line. Diagnostics omit assignment values so secrets are not logged.
All deployment scripts using the shared loader receive the same diagnostics.
Run the loader regression tests with `bash infra/scripts/tests/env-file-validation.sh`.

### App Service naming

The profile can customize the name segment that previously identified the runtime:

```dotenv
APP_NAME_STACK_A_INFIX=customer-api
APP_NAME_STACK_B_INFIX=customer-ui
```

With `PROJECT_NAME=talentmatch` and `ENVIRONMENT=test`, these generate
`app-talentmatch-customer-api-test` and `app-talentmatch-customer-ui-test`.
Unset or empty profile values retain `node` and `blazor`, respectively, so existing
profiles keep their current names. Direct Terraform callers can set
`stack_a_app_name_infix` or `stack_b_app_name_infix` in the corresponding live root;
the composition modules expose `app_name_infix`.

Use lowercase letters, digits and internal hyphens; infixes must start and end
with a letter or digit. Terraform rejects a generated name longer than 60
characters. Names must still be globally available in Azure, and the two stacks
must have distinct names. These settings change only App Service names, not
runtime versions, managed identities, Entra registrations or shared resource names.

Changing an existing app's infix forces App Service replacement. Review the plan
before applying, and update SPA redirect URIs, Microsoft 365 connector callbacks,
CORS origins, E2E base URLs and external integrations to the new URL. Artifact
deployment continues to resolve the name from Terraform outputs. For GitHub
deployments, include the new settings in the `DEPLOYMENT_ENV_FILE` environment secret.

## Bulk Scoring SQL Plan Capture

Run [capture-bulk-scoring-plans.sql](scripts/sql/capture-bulk-scoring-plans.sql)
against the intended Azure SQL application database using an account with
`VIEW DATABASE STATE`. It only reads catalog/Query Store data; it does not execute
queue writes, enable capture, force plans, or add indexes.

The three result sets contain Query Store configuration and the UTC capture window;
classified query text, plan XML, execution-count-weighted runtime statistics, and
regular-execution Query Store wait totals in `waits_json`;
and the relevant live index definitions, row counts, and statistics timestamps.
Save each non-null `query_plan` value as a `.sqlplan` file for inspection in the
SQL execution-plan viewer. Exports can include compiled parameter values, so keep
them in private diagnostic storage outside source control.

The seven-day runtime window includes intersecting boundary intervals. Duration
and CPU are milliseconds; logical I/O is 8 KiB pages. Wait totals are milliseconds
by category, not a live blocking chain. These are stored compiled
plans plus aggregated runtime measurements, **not actual execution plans with
per-operator row counts**. Query Store `AUTO` capture can omit infrequent queries.
Queries sharing a SQL shape are not proof that they ran in the same transaction.

Check coverage against the deployed revision and the current
`SequentialScoringQueueRepository` / `ApplicationRepository` implementations:
claim needs both the candidate read and conditional write; finalization needs the
ownership/lease-fenced application update and dependent result writes; summary
also performs job/configuration and authorization reads. `*-review` classifications
are candidates requiring source verification, not confirmed endpoint attribution.
Missing plans, historical variants, or a finalizer without the current lease fence
are coverage gaps, not evidence that the current implementation is fast.

Before adding an index, inspect seek and residual predicates, joins, lookups,
sorts, warnings, and existing index overlap. Validate a single candidate under a
representative queue/backlog and worker-concurrency workload using actual plans,
`STATISTICS IO/TIME`, and before/after latency, reads, CPU, blocking, and write cost.
Do not replay claim, renewal, or finalization writes on a live queue just to
collect actual plans; use a controlled non-production workload. Record the
baseline and rollback before creating a migration.

### Deadlock follow-up

The live-configurable browser intervals described in
[CONFIGURATION.md](../CONFIGURATION.md#dynamic-browser-refresh-intervals) reduce
polling pressure and prevent a page's slow refreshes from overlapping. They do
not establish that worker deadlocks have been eliminated or explain a reported
percentage increase.

Code-level contention risks, in priority order:

1. [Item transitions](../dotnet/src/Infrastructure/Persistence/Repositories/BulkScoringRepository.cs)
   retain child write locks while recomputing sibling counts and updating the
   parent request. The same repository's serializable `TryFinalizeAsync` reads
   the parent before reading children. This opposing order can form a deadlock
   cycle; sibling scans also increase its lock footprint. Establish one
   parent-before-child locking order across these paths before changing isolation.
   Ordinary read-committed scan behavior also depends on the database's RCSI setting.
2. Serializable claim/recovery transactions can touch several requests, items,
   counters and audit records. Narrow work to per-request transactions where the
   atomicity contract permits, and acquire multiple request/item locks in a
   deterministic order. Preserve lease/owner fences and atomic audit writes.
3. [The worker sweep](../dotnet/src/Application/BulkScoringRequests/Services/BulkScoringCycle.cs)
   attempts finalization/count reconciliation for every active request every five
   seconds, even while items are still running. Evaluate atomic counter deltas
   with bounded reconciliation instead of repeated full sibling scans and
   unchanged-state audit writes, without weakening completion or cleanup guarantees.

These are source-supported risks, not identification of a production deadlock
victim or blocker. Capture deadlock XML with both statements, lock resources and
isolation levels; establish p95/p99 status/lease latency and deadlocks per 1,000
completed items at comparable concurrency. Validate locking changes against a
controlled multi-host Azure SQL workload, including concurrent claims, renewal,
cancellation, finalization and retry. Query Store lock-wait totals alone cannot
reconstruct a deadlock graph. Do not use `NOLOCK`, remove transactional fences,
or add indexes as a substitute for this evidence.

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
./infra/scripts/package-stack-a.sh .env_qa

# 3) Deploy the packaged zip to Stack A App Service
./infra/scripts/deploy-stack-a-app.sh .env_qa artifacts/stack-a.zip
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

Stack B packaging requires the SDK selected by the repository's `global.json`.
It audits all solution dependencies and fails on any known NuGet advisory or
unavailable audit data. The self-contained Linux x64 artifact includes the pinned
.NET and ASP.NET Core 10.0.13 runtime, so it does not depend on App Service receiving
that shared-runtime patch first. Keep the existing `dotnet TalentMatch.Web.Server.dll`
startup command. Runtime security updates require repackaging and redeployment;
they are not inherited automatically from the platform's shared runtime.
See [dependency and runtime security](../CONFIGURATION.md#stack-b-dependency-and-runtime-security).

Both artifact deployment scripts load the environment profile, validate the
Azure context, resolve the app name from Terraform output, and verify Kudu
deployment history if Azure CLI loses its OneDeploy polling connection. They
succeed only when Kudu reports the latest deployment with success status `4`.
The Stack A script also enables the required on-host Oryx build settings before
deploying.

Both deployment entry points require the environment profile as their first
argument. They pass that path unchanged to `load_env_file`; no deployment script
selects a default profile. For QA deployments, always pass `.env_qa` explicitly.

Terraform state is isolated in a workspace named from `AZURE_SUBSCRIPTION_ID`
and `ENVIRONMENT`. This prevents local state from one subscription being reused
by another deployment profile. If the profile's resource group already exists,
the deployment wrapper imports it into a new workspace before planning.

### Generated Deployment Profile Settings

After a successful shared infrastructure `apply`, the wrapper saves `SQL_RG`,
`SQL_SERVER_NAME`, `SQL_DATABASE_NAME`, and `SQL_SERVER_FQDN` from the shared
Terraform outputs into the exact environment file passed to the command.
This happens before database-user bootstrap and downstream stack deployment,
including when runtime SQL bootstrap is disabled. Created and reused SQL are
both supported; the SQL resource group comes from the server resource ID,
which can differ from `RESOURCE_GROUP`. Existing coordinate entries are updated
and missing ones appended, preserving unrelated settings, comments, line endings,
and file permissions. `AZ_SQL_REUSE` and creation/ownership flags are not changed.
Plans do not modify the profile. Missing or
inconsistent SQL outputs stop deployment rather than writing guessed values.
In CI, this updates only the generated local profile, not its GitHub secret.

The same shared apply fills missing or empty **public, generated settings**:

- SQL runtime aliases `AZURE_SQL_SERVER_FQDN` and `AZURE_SQL_DATABASE_NAME`.
- App Service plan, selected managed identities, enabled Key Vault, API Management,
  and Application Insights names/resource groups, resolved from their resource IDs.
- Protected API client/service-principal IDs, scope/role IDs, selected SPA client
  IDs, and configured SPA redirect lists when product Entra authentication is enabled.
- `KEY_VAULT_URI`, `APIM_GATEWAY_URL`, `MICROSOFT365_SOURCE_CLIENT_ID`,
  `BULK_AUTH_KEY_URI`, and existing connector Web redirects when their features are enabled.

After application infrastructure apply, including `stack-b-only`, the wrapper
also fills missing selected `E2E_STACK_A_BASE_URL`/`E2E_STACK_B_BASE_URL` values
from the actual App Service URLs. In `graph` mode, if
`MICROSOFT365_SOURCE_REDIRECT_URIS` is still empty, it composes the source OAuth
and admin-consent callback URLs for the stacks applied by that command, using the
configured callback paths or the runtime defaults. It never guesses App Service
hostnames. Explicit URLs, custom domains, other nonempty settings, credentials,
reuse/creation flags, publisher-verification assertions, consent, CORS, scopes,
and policy limits are left unchanged. The four authoritative SQL coordinates
above remain the deliberate exception: they are refreshed from state.

**Callback registration is separate.** Generated source callback lists emit a
warning to review/register those exact Web URLs in the source connector's Entra
registration before sign-in. Profile persistence does not change Entra
registrations or grant consent. It does not remove first-deployment requirements:
approved product/source redirect URIs, stable role/scope IDs, and all other
required operator inputs must still be supplied before an Entra-enabled first
plan/apply. Later additions to a nonempty callback list, such as adding a second
stack or a custom domain, require an explicit reviewed edit rather than an
automatic merge.

For an already deployed environment, backfill available settings without a
resource plan/apply:

```bash
bash ./infra/scripts/sync-deployment-profile.sh "$ACTIVE_ENV_FILE"
./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" check
```

The sync command validates the active Azure context and reads only the existing
Terraform workspace selected by `AZURE_SUBSCRIPTION_ID` and `ENVIRONMENT`, plus
the stack states selected by `STACK_TARGET`. It changes the selected profile
only, not Azure resources, Terraform state, registrations, or GitHub secrets.
Use `STACK_TARGET=shared-only` only for an actual shared-only deployment; a
selected application stack must have deployed state or the command fails
explicitly. New optional output names absent from older state produce a warning;
review/apply the updated shared configuration to expose those outputs, then sync
again. Do not switch reuse flags to recover generated values.

Run focused regressions with:

```bash
npx vitest run tests/unit/deployment-profile.test.ts
bash infra/scripts/tests/deployment-profile-sync.sh
```

If that import reports `Invalid for_each argument` for
`azuread_app_role_assignment.role_group`, use the updated
[Entra module](terraform/modules/foundation/entra/main.tf). Both groups and
assignments must use the configuration-keyed `local.role_groups` map; only
`principal_object_id` reads the created group's object ID. This keeps instance
keys known during import even before Entra groups exist. Retry the same
deployment command with the same profile and workspace after updating the module.
Do not delete state, regenerate role UUIDs, or use a targeted apply to work around
this error.

The environment file's `STACK_TARGET` (`stack-a`, `stack-b`, `both`, or
`shared-only`) controls the shared provisioning inventory. Command targets can
deploy a subset of that inventory but cannot enable an excluded stack. In
particular, running a `shared-only` command with `STACK_TARGET=stack-b` prepares
Stack B's shared dependencies; it does not change the profile to shared-only.
GitHub target resolution materializes the complete `DEPLOYMENT_ENV_FILE`
environment secret as the mapped untracked profile, filters automatic jobs to
enabled stacks, and rejects manual targets outside that profile. CI never
falls back to `.example` coordinates.

For `APP_AUTH_MODE=entra`, set `ENTRA_API_IDENTIFIER_URI`,
`ENTRA_API_SCOPE_ID`, all four `ENTRA_*_APP_ROLE_ID` UUIDs,
`ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`, and redirect URI lists for selected SPAs only.
Custom scope and role IDs have no built-in defaults. Preserve IDs already
deployed; for new registrations select and record them once in the untracked
profile before the first plan, never generate replacements on each run.
With `ENTRA_REUSE=TRUE`, provide the shared API client/service-principal IDs and
only the selected SPA client ID(s). Otherwise Terraform creates the protected
API plus one SPA for a single-stack profile, or both SPAs for `both`, and
assigns the bootstrap user the API `admin` app role. Unselected SPA outputs are
empty. Stack B requires its own SPA ID; Stack A's ID is optional and is trusted
only when configured. `APP_AUTH_MODE=simple` does not provision product Entra
applications, role groups or the bootstrap API app-role assignment.

`AZ_IDENTITIES_REUSE` is independent of `ENTRA_REUSE`: it reuses or creates only
the identities selected by `STACK_TARGET`. A shared-only profile has no runtime
identities or SPAs. Changing the selected inventory, authentication mode or
create/reuse ownership can remove previously managed objects; review the plan
and coordinate state migration before applying to an existing environment.
See the [manual identity handoff guide](../docs/MANUAL_ENTRA_ID_RBAC_IMPLEMENTATION_GUIDE.md)
for per-stack object counts, consent and configuration examples.

For separate managed-identity provisioning, run setup explicitly before the
normal deployment:

```bash
bash infra/scripts/setup-identity.sh .env_qa
```

Setup checks or creates only the identities selected by `STACK_TARGET` in an
existing resource group. It uses configured names/group when supplied, otherwise
the Terraform naming defaults and `RESOURCE_GROUP`. It verifies the active
Azure tenant/subscription and stops on lookup or permission errors. After
success, record the printed `AZ_IDENTITIES_REUSE=TRUE`, `AZ_IDENTITIES_RG`, and
selected identity-name assignments in the untracked profile before deployment.
No profile, Terraform state, app registration, or grant is changed by setup.
It is never invoked automatically by deployment or CI. `ENTRA_REUSE` continues
to control app registrations independently. Existing Terraform-owned identities
must not be switched to reuse without a reviewed state migration.
See [separate managed-identity setup](../CONFIGURATION.md#separate-managed-identity-setup)
for prerequisites, handoff steps, and ownership precautions.

Browser clients must request the delegated scope as
`<ENTRA_API_IDENTIFIER_URI>/<ENTRA_API_SCOPE>`; do not synthesize it from
`ENTRA_API_APP_CLIENT_ID` when the registration uses a custom identifier URI.
Stack B's explicit **Sign in with Microsoft** action sends
`prompt=select_account` so users choose an account before the tenant-bound
authorization request continues. Silent token refresh does not use this prompt.

When creating Azure SQL, set `SQL_AAD_ADMIN_LOGIN` and
`SQL_AAD_ADMIN_OBJECT_ID`. Leaving `SQL_ADMIN_PASSWORD` empty enables Entra-only
administration. The token-based post-provisioning bootstrap creates the
`talentmatch` schema and grants the selected managed identities their database roles.
It runs by default. For a reused private-only database that the operator host
cannot reach, set `AZ_SQL_BOOTSTRAP_ENABLED=FALSE`; then run
`infra/scripts/bootstrap-sql-entra-users.mjs` with `SQL_SERVER_FQDN`,
`SQL_DATABASE_NAME`, and only the selected `STACK_A_IDENTITY_NAME` and/or
`STACK_B_IDENTITY_NAME` from a VNet-connected host before application use.
Unset the unselected name: this manual helper reads the name variables rather
than `STACK_TARGET`. A shared-only profile skips runtime SQL-user bootstrap.

This helper does **not** create application tables. Package/deploy the
application ZIP next and wait for startup schema initialization before running
`seed-entra-admin.sh`. Stack B performs Azure SQL initialization in the
background and logs `[startup] Background database initialization complete.`
only after the schema and startup data are ready. Successful ZIP deployment or
a SQL connectivity health response alone is insufficient. For
`Invalid object name 'talentmatch.Users'`, verify the seed profile targets the
same database as the app and inspect startup logs; see
[missing application tables](../docs/ENTRA_AUTHORIZATION.md#missing-application-tables).
If startup instead fails with `Invalid object name 'talentmatch.BulkScoringRequests'`
on an empty database, rebuild and redeploy Stack B from the fixed source.
Older builds attempted the bulk upgrade before base schema creation. The fix
requires a new application ZIP, not a Terraform apply or manual empty-table
creation; it preserves existing completed history and the legacy active-work guard.

API Management is optional. Set `AZ_APIM_ENABLED=FALSE` to skip both APIM
creation and lookup. `AZ_APIM_REUSE` is considered only when APIM is enabled.

### Optional Application Insights (Both Stacks)

```env
AZ_APP_INSIGHTS_ENABLED=TRUE
AZ_APP_INSIGHTS_REUSE=TRUE
AZ_APP_INSIGHTS_NAME=<existing-component-name>
AZ_APP_INSIGHTS_RG=<existing-resource-group>
```

The existing component may be in another resource group in the deployment
subscription. With `AZ_APP_INSIGHTS_REUSE=FALSE`, the shared root instead creates
one `appi-<project>-<environment>` component and a 30-day
`law-<project>-<environment>` workspace in `RESOURCE_GROUP`. Both App Services
use the same destination. Leave both flags false to preserve existing behavior.
See [the configuration reference](../CONFIGURATION.md#application-insights) for
permissions, agent support, lifecycle and verification details.

The integration uses Linux App Service autoinstrumentation and the existing
stack managed identities, with component-scoped Monitoring Metrics Publisher
roles. The deployer needs role-assignment permission on the component even in
reuse mode. Node.js autoinstrumentation is public preview; browser monitoring is
disabled. App Service names distinguish the stacks' default cloud roles.

**Lifecycle warning:** Disabling removes monitoring settings and schedules any
component/workspace managed by this deployment for destruction. Reused
components/workspaces are never destroyed. Switching ownership modes requires
an explicit state/data migration if the original resources must be retained.
Review a Terraform plan before applying either change.

For GitHub deployments, put these settings in the complete `DEPLOYMENT_ENV_FILE`
environment secret, not separate GitHub variables. Profile examples keep
monitoring disabled; populate and review an untracked profile to opt in.
Monitoring opt-in settings are never changed automatically; shared apply only
fills generated coordinates and identifiers as described above.
For `AWR_AUTH_MODE=entra`, set `AWR_AAD_AUDIENCE` to the API's `.default`
scope and `AWR_API_CLIENT_ID` to its application client ID. Shared Terraform
assigns `AWR_API_APP_ROLE_VALUE` (default `TalentMatch.Access`) only to selected
stack managed identities. `apikey` and `none` never create these assignments,
even if an old `AWR_API_CLIENT_ID` is populated.
New Azure SQL creation is fail-closed: `AZ_SQL_REUSE=FALSE` also requires the
explicit `ALLOW_CREATE_AZURE_SQL=TRUE` opt-in. Existing-database profiles should
keep `AZ_SQL_REUSE=TRUE` and provide `SQL_SERVER_NAME`, `SQL_DATABASE_NAME`, and
`SQL_RG`; Terraform then treats the server and database as read-only data sources.
For newly created SQL servers, `AZ_ALLOWED_IPS` also creates matching SQL
firewall rules so the operator can run that bootstrap.

### Both Stacks: Full Deploy (One Infra Pass + Both Packages + Both App Deploys)

```bash
# 1) Provision/update shared + both stack infrastructures
./infra/scripts/deploy.sh .env_qa test apply both

# 2) Build both deployable artifacts
./infra/scripts/package-stack-a.sh .env_qa
./infra/scripts/package-stack-b.sh

# 3) Deploy both packaged artifacts
./infra/scripts/deploy-stack-a-app.sh .env_qa artifacts/stack-a.zip
./infra/scripts/deploy-stack-b-app.sh .env_qa artifacts/stack-b.zip
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

`infra/scripts/package-stack-a.sh` creates a self-contained zip artifact with the compiled client/server and only the production dependencies imported by the server.

For repeated local packages, the script reuses `node_modules` when npm's hidden lockfile is newer than both manifests and required build tools are present. Set `STACK_A_INSTALL_MODE=clean` to force a reproducible `npm ci`; CI uses clean mode explicitly. Packaging runs Vite directly because the client TypeScript project is `noEmit`; use `npm run build` separately when you want its TypeScript validation pass.

Deployment implications:
- App Service does not run Oryx during deployment; dependency restore is completed while packaging.
- Required app settings: `SCM_DO_BUILD_DURING_DEPLOYMENT=false` and `ENABLE_ORYX_BUILD=false`.
- These are configured by Terraform in `infra/terraform/modules/stack-a/main.tf`
  and reapplied by `deploy-stack-a-app.sh` before each artifact deployment.

Operational expectations:
- Local packaging is significantly faster.
- First deployment after a code change shifts part of the time to server-side build logs during Zip Deploy.
- Runtime startup still uses the compiled server entrypoint (`node server/index.js`).

## GitHub Actions Workflow

The workflow at `.github/workflows/selective-azure-deploy.yml` supports:

- **Push triggers**: Automatically detects changed scopes and deploys only affected stacks.
- **Manual dispatch**: Choose environment, target, and action explicitly.

Configure each GitHub environment with OIDC secrets `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`, plus `DEPLOYMENT_ENV_FILE`.
The latter is the full multiline contents of the approved untracked profile,
not a filename or an unchanged example. Its `ENVIRONMENT` must match the
selected environment, and its tenant/subscription must match the login secrets.
CI writes it to a permission-restricted, untracked file; never log or upload it.
Target resolution, infrastructure, and Stack A packaging use that profile.

Artifact deployment resolves the App Service `app_name` from the selected
subscription/environment Terraform workspace and uses `RESOURCE_GROUP` from
the same profile. A missing target fails explicitly; names are not inferred
from project/environment naming conventions.

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
  --server "$SQL_SERVER_NAME" \
  --name "$SQL_DATABASE_NAME" \
  --resource-group "$SQL_RG" \
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
SQL_SERVER_NAME=
SQL_DATABASE_NAME=
SQL_RG=
```

The deploying identity needs at least `Reader` access on external resource groups containing reused resources.
Fill the blank coordinates only in the untracked profile using the approved
existing-resource values; these fields deliberately do not prescribe names.

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

When `AZ_SQL_PRIVATE_ENDPOINT_REUSE=FALSE`, it also creates a dedicated private endpoint subnet, an Azure SQL private endpoint, the `privatelink.database.windows.net` private DNS zone, a VNet link, and the endpoint DNS zone group.

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
AZ_VNET_NAME=
AZ_VNET_RG=
AZ_INTEGRATION_SUBNET_NAME=
AZ_SQL_PRIVATE_ENDPOINT_REUSE=FALSE
AZ_SQL_PRIVATE_ENDPOINT_SUBNET_NAME=
AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR=
AZ_ALLOWED_IPS=
```

Populate network names, the approved private-endpoint CIDR, and allowed IPs
only in the untracked profile. Illustrative values remain in the explicitly
labelled deployment templates, not in this runbook.

### Backward Compatibility

Deployments that do not set `AZ_VNET_REUSE=TRUE` work exactly as before — no VNet Integration, no IP restrictions. All new variables default to empty/false.

### Required Azure RBAC Permissions

The deploying identity needs **Network Contributor** on the VNet resource group to create subnets or configure VNet Integration. For reuse-only mode (existing subnet), **Reader** access on the VNet resource group is sufficient.

### Validation Rules

The deploy script validates before any Terraform execution:
- `AZ_VNET_NAME` and `AZ_VNET_RG` must be set when `AZ_VNET_REUSE=TRUE`
- Exactly one of `AZ_INTEGRATION_SUBNET_NAME` or `AZ_INTEGRATION_SUBNET_CIDR` must be set
- CIDR prefix must be ≤26 when creating a new subnet
- `AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR` must be set when Terraform creates SQL Private Link
- `AZ_ALLOWED_IPS` must be non-empty when VNet is configured

## Scripts Reference

| Script | Purpose |
|--------|---------|
| `infra/scripts/deploy.sh` | Main deployment wrapper |
| `infra/scripts/deprovision.sh` | Safe destroy with reuse protection |
| `infra/scripts/setup-identity.sh` | Explicit idempotent managed-identity setup and reuse-coordinate handoff |
| `infra/scripts/resolve-targets.sh` | Workflow target resolution |
| `infra/scripts/package-stack-a.sh` | Build and package Stack A |
| `infra/scripts/package-stack-b.sh` | Publish and package Stack B |
| `infra/scripts/deploy-stack-a-app.sh` | Deploy the packaged Stack A artifact |
| `infra/scripts/deploy-stack-b-app.sh` | Deploy the packaged Stack B artifact |
| `infra/scripts/lib/common.sh` | Shared Bash helpers |
