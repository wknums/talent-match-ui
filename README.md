# Talent Matching Platform

An AI-assisted recruiting accelerator that demonstrates how reasoning models can help recruiters assess candidates more consistently, explain recommendations, and complete high-volume review work in less time.

> [!IMPORTANT]
> This repository is **not a full Applicant Tracking System (ATS)** and is not intended to replace one. It is an accelerator and reference implementation focused on the candidate-assessment stage of recruiting. Capabilities such as candidate sourcing, career sites, interview scheduling, offer management, onboarding, and end-to-end hiring compliance workflows remain the responsibility of an ATS or other integrated systems.

The solution keeps people in control of hiring decisions. AI-generated scores, evidence, and recommendations are decision-support signals for recruiters and business reviewers, not autonomous hiring decisions.

## What the Solution Demonstrates

- **Job and rubric configuration**: Create jobs, extract structured requirements from job specifications, define weighted rubric categories, identify must-have and desired criteria, set thresholds, and version scoring configuration.
- **Reasoning-based candidate assessment**: Evaluate CVs against job-specific criteria and return category scores, must-have checks, evidence citations, rationale, improvement recommendations, and model/run metadata.
- **Multi-run scoring and aggregation**: Run configurable repeated assessments, compare variance, and aggregate results using median, mean, or weighted strategies to reduce reliance on a single model response.
- **Recruiter review workflows**: Rank and filter longlisted, shortlisted, excluded, and manual-review candidates; inspect source documents and individual scoring runs; and capture human review decisions and notes.
- **Prompt lifecycle management**: Generate, edit, rate, activate, test, approve, and promote job-specific prompts, including test-run comparison before a prompt is used for production scoring.
- **Extraction-instruction lifecycle**: Administrators can draft, validate, activate, and roll back the default job-specification extraction instructions while the application keeps the structured output contract protected.
- **Itemized rubric editing**: New and converted `rubric-v2` job configurations preserve ordered rubric categories plus individually traceable requirements that can be moved, reordered, and reviewed.
- **Operational controls**: Monitor extraction, scoring, and aggregation progress; cancel processing; retry or rescore failures; reaggregate completed work; and manage dead-letter queue items.
- **Recruiting analytics**: Explore recruiter and department-level throughput and outcome metrics with access scoped to the signed-in user.
- **Organization-aware authorization**: Support global administrators, organization administrators, recruiters, and business-panel reviewers with organization and department boundaries.
- **Two implementation stacks**: Compare equivalent TypeScript and .NET approaches against shared behavioral, data, authentication, and API contracts.

## Key Strengths

- **Explainable by design**: Assessments retain criterion-level scores, cited evidence, rationales, prompt versions, and run metadata so reviewers can understand how a recommendation was formed.
- **Human-centered**: Manual review, business-panel access, variance flags, and visible source documents keep professional judgment at the center of the workflow.
- **Configurable and repeatable**: Versioned rubrics, prompts, thresholds, run counts, and aggregation strategies make experiments explicit and results easier to reproduce.
- **Enterprise-ready patterns**: Microsoft Entra ID, persisted role assignments, organization/department scoping, managed identity, Azure SQL, Blob Storage, health checks, and audit events demonstrate practical integration patterns.
- **Resilient processing options**: The solution supports direct sequential scoring and asynchronous platform scoring with batching, reconciliation, retries, and failure recovery.
- **Technology choice without contract drift**: Stack A and Stack B implement the same core experience using different ecosystems, making the repository useful for architecture evaluation and modernization workshops.

## Solution Stacks

| Area | Stack A | Stack B |
| --- | --- | --- |
| Web client | React 19 + TypeScript + Vite | Blazor WebAssembly |
| API | Node.js + Express | ASP.NET Core 10 minimal APIs |
| Application architecture | Route/service/repository layers | Clean Architecture + MediatR |
| Data access | SQLite or Azure SQL through repositories | EF Core with SQLite or Azure SQL |
| Browser authentication | MSAL React in Entra mode | Blazor MSAL in Entra mode |
| Shared contracts | API routes, authorization outcomes, database model, scoring platform contract | API routes, authorization outcomes, database model, scoring platform contract |

## Prerequisites

For local application development:

- **Node.js** 20.19+ or 22.12+ (22.12+ is recommended)
- **npm** 10+
- **.NET SDK** 10.0.x for Stack B

## Local Azure Deployment Prerequisites

The deployment scripts are Bash scripts and can run from a developer or
operator workstation without GitHub Actions. On Windows, run them from **Git
Bash**, not from a PowerShell prompt. The versions below match the repository's
runtime and Terraform constraints.

| Tool | Required or supported version | Used for |
| --- | --- | --- |
| Git for Windows and Git Bash | Git 2.x; no narrower repository minimum | Running the Bash deployment scripts and stamping artifacts with a commit SHA |
| Azure CLI | Azure CLI 2.x; no narrower repository minimum | Authentication, Azure resource queries, ZIP deployment, Microsoft Graph calls, and SQL administrator checks |
| Terraform | 1.9.0 or later; remain on 1.x | Provisioning shared resources and both App Service stacks |
| Node.js | 22.12+ recommended; 20.19+ also supported | Packaging Stack A and running Azure SQL/Entra bootstrap helpers |
| npm | 10+ | Restoring and packaging Stack A |
| .NET SDK | 10.0.x | Publishing Stack B |
| Windows PowerShell | 5.1+ | ZIP creation fallback when the `zip` command is unavailable |

The Terraform Azure providers are restored by `terraform init`. The shared root
uses `hashicorp/azurerm ~> 4.0` and `hashicorp/azuread ~> 3.0`; the stack roots
use `hashicorp/azurerm ~> 4.0`. Do not install provider binaries manually.

### Install on Windows

Run these commands from an elevated PowerShell prompt. WinGet installs the
current stable release of each tool; the version constraints in the table above
determine whether that release is suitable for this repository.

```powershell
# Git for Windows, including Git Bash
winget install --exact --id Git.Git --source winget

# Azure CLI
winget install --exact --id Microsoft.AzureCLI --source winget

# Terraform
winget install --exact --id Hashicorp.Terraform --source winget

# .NET 10 SDK
winget install --exact --id Microsoft.DotNet.SDK.10 --source winget

# NVM for Windows, used to install the repository's Node.js version
winget install --exact --id CoreyButler.NVMforWindows --source winget
```

Close and reopen the terminal after installing NVM, then install the recommended
Node.js line:

```powershell
nvm install 22.12.0
nvm use 22.12.0
```

Node.js 22.12.0 includes npm 10. If a later Node.js 22 release supplies a
different npm major, select a compatible npm version explicitly:

```powershell
npm install --global npm@10
```

### Verify the Workstation

Open Git Bash in the repository root and verify every command before attempting
a deployment:

```bash
git --version
bash --version
az version
terraform version
node --version
npm --version
dotnet --version
powershell.exe -NoProfile -Command '$PSVersionTable.PSVersion'
```

Expected major/minimum versions are Terraform 1.9+, Node.js 20.19+/22.12+,
npm 10+, and .NET SDK 10. The deployment package scripts use Windows PowerShell
as their ZIP fallback, so installing a separate `zip` package is optional.

Authenticate to the tenant and subscription declared in the target environment
profile. The scripts validate both IDs and stop rather than silently switching
context:

```bash
az login --tenant "<tenant-id>"
az account set --subscription "<subscription-id>"
az account show --query "{tenantId:tenantId, subscriptionId:id, name:name}" --output table
```

Copy the appropriate `.env_*.example` file to a concrete profile and populate
it through the organization's approved secret-handling process. Concrete
environment profiles can contain sensitive deployment coordinates and are
intentionally excluded from Git.

The complete local infrastructure, packaging, and ZIP deployment commands are
documented in [infra/README.md](infra/README.md). Vendor installation references
are available for [Git for Windows](https://git-scm.com/install/windows),
[Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-windows),
[Terraform](https://developer.hashicorp.com/terraform/install),
[Node.js](https://nodejs.org/en/download), and
[.NET 10](https://learn.microsoft.com/dotnet/core/install/windows).

## Quick Start

```bash
# Install dependencies and create local configuration
npm install
cp .env.example .env

# Start both backend server and frontend dev server
npm run dev
```

This runs two processes concurrently:

- **Backend API** on `http://localhost:3001`
- **Vite frontend** on `http://localhost:5173`

Open `http://localhost:5173` in your browser. In Development, the default login is `admin` / `adm1n99`.
The .NET server rejects simple authentication outside Development and Testing; shared and production environments must use Entra authentication.

The default configuration uses simple authentication, SQLite, and mock API data. Set `API_MODE=real` and configure an AWReason endpoint to exercise the real extraction and scoring workflows.

## Available Scripts

| Script | Description |
| --- | --- |
| `npm run dev` | Start backend + frontend together |
| `npm run dev:client` | Start only the Vite frontend |
| `npm run dev:server` | Start only the Express backend |
| `npm run build` | Build frontend for production |
| `npm run build:server` | Compile backend TypeScript |
| `npm start` | Run compiled backend (after `build:server`) |
| `npm run lint` | Run ESLint |
| `npm test` | Run the Stack A Vitest suite |
| `npm run test:e2e` | Run Playwright end-to-end tests |

## Scoring Architecture

The application delegates document extraction, prompt generation, and reasoning-based assessment to an AWReason API. Production scoring can use either of two modes:

| Mode | Configuration | Behavior |
| --- | --- | --- |
| Sequential | `AWR_SEQ_API_ENDPOINT` only | Sends assessments directly and waits for each response. Suitable for local development, prompt tests, and smaller workloads. |
| Platform | `AWR_PLATFORM_API_ENDPOINT` set to a different endpoint | Submits CV batches asynchronously and reconciles their status. The external platform owns queueing, fan-out, retries, and durable orchestration. |

Prompt generation, extraction, and prompt test runs always use the sequential endpoint. Platform mode additionally requires Azure Blob Storage so the client and scoring platform can exchange documents using managed identity rather than shared access signatures.

## Dynamic Rubric Editor

Feature `001-dynamic-rubric-editor` adds a shared protected extraction contract, persisted extraction diagnostics, and a versioned `rubric-v2` envelope.

- **Protected extraction contract**: both stacks append an application-owned output contract before calling AWReason and reject invalid responses instead of silently coercing them.
- **Persisted provenance**: `ExtractionInstructionVersions` and `JobSpecExtractions` preserve the instruction version, findings, source metadata, and correlation IDs used during extraction.
- **Legacy compatibility**: existing rubric arrays remain readable; users can preview and confirm conversion into `rubric-v2` without mutating historical versions, and Stack A blocks generic save while a legacy preview still needs confirmation.
- **Reviewer safety**: generated rubric warnings and `needs_review` items stay visible during React editing so recruiters can reassign ambiguous requirements before approval.
- **Dual-read scoring compatibility**: scoring, prompt generation, application detail, and manual review continue to operate on category weights while richer item-level traces remain available to reviewers.

See [specs/001-dynamic-rubric-editor/quickstart.md](specs/001-dynamic-rubric-editor/quickstart.md) for the feature runbook and [INTEGRATION.md](INTEGRATION.md) for endpoint details.

The platform integration contract is defined in [specs/008-platform-mode-shift/platform-contract.md](specs/008-platform-mode-shift/platform-contract.md).

## Azure App Service Startup (Stack A)

For Linux App Service deployments of Stack A, set the **Startup Command** to:

```bash
npm start
```

Do not set the startup command to `npm run` without a script name. That causes startup failure.

The Stack A packaging script (`infra/scripts/package-stack-a.sh`) prepares the deployment artifact so `npm start` resolves to the correct production entry point.

## Storage Configuration

The backend supports two database drivers:

### SQLite (default — local development)

No configuration needed. Data is stored in `shared-data/talentmatch.db`. Tables are created without schema qualification.

### Azure SQL

```env
STORAGE_PROVIDER=azuresql
AZURE_SQL_AUTH_MODE=entra
AZURE_SQL_SERVER_FQDN=your-server.database.windows.net
AZURE_SQL_DATABASE_NAME=your-db
AZURE_CLIENT_ID=<user-assigned-managed-identity-client-id>
```

Requires `mssql` package (included as optional dependency). In Azure, Stack A uses the assigned managed identity for Azure SQL; the corresponding database user must exist before startup. The server will run `schema.sql` automatically on first connection.

Azure SQL pay-as-you-go databases can take 30-90 seconds to wake after idle periods. Both stacks now include startup resilience for that behavior:

- **Node.js (Stack A)** retries initial `mssql` connection attempts with bounded exponential backoff.
- **.NET (Stack B)** applies SQL Server provider transient retries, a minimum 90 second connect timeout, and retries startup migration/seed work.
- **Operations guidance**: do not lower the Azure SQL connect timeout below 90 seconds in Azure-hosted environments unless you are prepared to absorb cold-start failures.

Stack A retry tuning can be overridden with environment variables:

```env
AZURE_SQL_WAKEUP_MAX_ATTEMPTS=8
AZURE_SQL_WAKEUP_INITIAL_DELAY_MS=2000
AZURE_SQL_WAKEUP_MAX_DELAY_MS=15000
```

When cold-start retries occur, the service logs retry scheduling, eventual success-after-retry, and retry-budget exhaustion to help diagnose database wake-up delays.

**Schema isolation**: Azure SQL deployments use the `talentmatch` schema — all 15 application tables are created under `[talentmatch].[TableName]` instead of the default `dbo` schema. This provides namespace isolation in shared database environments.

- **Node.js (Stack A)**: Uses the `T()` helper from `server/storage/table-names.ts` to qualify table names. When adding a new table, use `T('NewTable')` in your SQL queries — schema qualification is automatic.
- **\.NET (Stack B)**: Uses `HasDefaultSchema("talentmatch")` in `AppDbContext.cs`, applied only on SQL Server.
- **Local SQLite**: Completely unaffected. The `T()` helper returns plain table names, and `HasDefaultSchema` is skipped.

## AI Service Configuration

Set the AWReason endpoint used for extraction, prompt generation, and candidate scoring:

```env
API_MODE=real
AWR_SEQ_API_ENDPOINT=http://127.0.0.1:8080
AWR_AUTH_MODE=none
```

`AWR_AUTH_MODE` supports `none` for local development, `apikey` for shared test environments, and `entra` for Microsoft Entra workload authentication. Optional `AWR_PLATFORM_API_ENDPOINT` settings enable asynchronous platform scoring.

Azure OpenAI can also be configured for the local LLM proxy:

```env
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com
```

The application identity must have the `Cognitive Services OpenAI User` role on the Azure OpenAI resource. Local development uses the developer identity selected by `DefaultAzureCredential`.

## Environment Variables

Copy `.env.example` to `.env` and configure:

| Variable | Default | Description |
| --- | --- | --- |
| `PORT` | `3001` | Backend server port |
| `API_MODE` | `mock` | Use `mock` data or the `real` application API |
| `APP_AUTH_MODE` | `simple` | Use local credentials in Development/Testing or `entra` authentication elsewhere |
| `STORAGE_PROVIDER` | `local` | Set to `azuresql` to use Azure SQL in Stack A |
| `DATABASE_PROVIDER` | `sqlite` | Stack B database provider: `sqlite` or `sqlserver` |
| `SQLITE_DB_PATH` | `shared-data/talentmatch.db` | Optional shared local database path |
| `AZURE_SQL_AUTH_MODE` | - | Set to `entra` for managed-identity Azure SQL auth |
| `AZURE_SQL_SERVER_FQDN` | - | Azure SQL server FQDN for Entra auth |
| `AZURE_SQL_DATABASE_NAME` | - | Azure SQL database name for Entra auth |
| `AZURE_CLIENT_ID` | - | User-assigned managed identity client ID used for Azure SQL and other Azure SDK auth |
| `AWR_SEQ_API_ENDPOINT` | - | AWReason endpoint for extraction, prompt work, and sequential scoring |
| `AWR_PLATFORM_API_ENDPOINT` | - | Optional asynchronous scoring platform endpoint |
| `AWR_AUTH_MODE` | `none` | AWReason authentication mode: `none`, `apikey`, or `entra` |
| `AWR_AAD_AUDIENCE` | - | AWReason API scope used for Entra client-credential tokens |
| `AWR_BLOB_STORAGE_ACCOUNT` | - | Blob account required by platform mode |
| `AWR_BLOB_CONTAINER` | `cv-uploads` | Blob container used to exchange CV documents |
| `AZURE_SQL_WAKEUP_MAX_ATTEMPTS` | `8` | Stack A Azure SQL startup retry budget |
| `AZURE_SQL_WAKEUP_INITIAL_DELAY_MS` | `2000` | Stack A initial Azure SQL retry delay |
| `AZURE_SQL_WAKEUP_MAX_DELAY_MS` | `15000` | Stack A cap for Azure SQL retry backoff |
| `AZURE_OPENAI_ENDPOINT` | - | Azure OpenAI endpoint URL; authentication uses Entra RBAC |

See [.env.example](.env.example) for the complete configuration surface and platform-mode tuning settings.

## Project Structure

```text
src/                         # Stack A React client and UI workflows
server/                      # Stack A Express API, services, workers, and repositories
dotnet/src/Domain/           # Stack B domain entities and interfaces
dotnet/src/Application/      # Stack B use cases, validation, and authorization
dotnet/src/Infrastructure/   # Stack B EF Core persistence and external services
dotnet/src/Web.Server/       # Stack B ASP.NET Core API
dotnet/src/Web.Client/       # Stack B Blazor WebAssembly client
shared-data/                 # Shared local SQLite database
infra/                       # Terraform and deployment scripts
specs/                       # Feature specifications and integration contracts
tests/                       # Stack A unit, integration, and end-to-end tests
dotnet/tests/                # Stack B test projects
```

---

## Stack B — .NET / Blazor WASM

The project includes a parallel .NET implementation using Clean Architecture in `dotnet/`.

### Running Stack B

```bash
cd dotnet
dotnet restore TalentMatch.slnx
dotnet build TalentMatch.slnx
dotnet run --project src/Web.Server/TalentMatch.Web.Server.csproj
```

The server prints its local URL at startup and exposes Swagger UI at `/swagger` in development.

### Running Tests

```bash
# Stack A tests
npm test

# Stack B tests
cd dotnet
dotnet test TalentMatch.slnx
```

### Configuration

Stack B uses `appsettings.json` for configuration:

- `DatabaseProvider`: `sqlite` (default) or `sqlserver`
- `AZURE_SQL_AUTH_MODE`: `entra` for Azure-hosted managed-identity SQL auth
- `AZURE_SQL_SERVER_FQDN`: Azure SQL server FQDN
- `AZURE_SQL_DATABASE_NAME`: Azure SQL database name
- `AZURE_CLIENT_ID`: user-assigned managed identity client ID
- `AzureOpenAI:Endpoint`: Azure OpenAI endpoint (for LLM features)

When `DatabaseProvider=sqlserver`, Stack B enforces Azure SQL resilience defaults in code:

- SQL connect timeout is raised to at least 90 seconds.
- EF Core enables transient retries with up to 6 retries and a 15 second max retry delay.
- Startup migration and seeding are retried with bounded exponential backoff for transient wake-up failures.

In local development, both stacks default to the same shared SQLite file at `shared-data/talentmatch.db` under the repo root.
You can override that location for either stack with `SQLITE_DB_PATH`.

## Authentication and Authorization

Both stacks support two mutually exclusive modes:

- `APP_AUTH_MODE=simple` provides local username/password authentication for demos, automated tests, and offline development. The .NET server rejects this mode outside Development and Testing.
- `APP_AUTH_MODE=entra` uses single-tenant Microsoft Entra ID authorization-code flow with PKCE. API access is resolved from persisted application assignments and organization/department memberships rather than relying on token roles alone.

Entra mode supports `admin`, `organization_admin`, `recruiter`, and `business_panel` roles. Authorization changes use optimistic concurrency, revocation is reflected through authorization-version changes, and mutations produce correlated audit events.

See [AUTHENTICATION.md](AUTHENTICATION.md) for behavior and [docs/ENTRA_AUTHORIZATION.md](docs/ENTRA_AUTHORIZATION.md) for setup and operator guidance.

## Microsoft Entra Deployment and Administrative Handoff

This section describes the complete identity inventory and the handoffs required
when directory administration, Azure infrastructure deployment, SQL
administration, application deployment, and day-to-day access administration
belong to different people.

### Identity Inventory

For an environment whose `PROJECT_NAME` is `talentmatch` and whose
`ENVIRONMENT` is `test`, Terraform uses the `talentmatch-test-*` display names.
Display names are for people; automation and handoff records must use the
immutable IDs.

| Item | Purpose | Important configuration |
| --- | --- | --- |
| Protected API app registration (`<project>-<environment>-api`) | Represents the TalentMatch API and defines what browser clients may request | Single-tenant; access token version 2; identifier URI from `ENTRA_API_IDENTIFIER_URI`; delegated `ENTRA_API_SCOPE` (normally `access_as_user`) |
| Protected API enterprise application | Tenant-local service principal for the protected API | Receives user/group app-role assignments; hand off its **object ID** as `ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID` |
| Stack A SPA app registration (`<project>-<environment>-stack-a`) | Public browser client for React/TypeScript Stack A | Single-tenant SPA; redirect URIs from `ENTRA_STACK_A_REDIRECT_URIS`; delegated access to the protected API scope; no client secret |
| Stack A enterprise application | Tenant-local service principal for the Stack A registration | Created with the app registration; used for consent and tenant administration |
| Stack B SPA app registration (`<project>-<environment>-stack-b`) | Public browser client for Blazor WebAssembly Stack B | Single-tenant SPA; redirect URIs from `ENTRA_STACK_B_REDIRECT_URIS`; delegated access to the protected API scope; no client secret |
| Stack B enterprise application | Tenant-local service principal for the Stack B registration | Created with the app registration; used for consent and tenant administration |
| Four protected-API app roles | Coarse directory role signal and administrative assignment target | Values must be exactly `admin`, `organization_admin`, `recruiter`, and `business_panel`; allowed member type is Users/Groups |
| Optional role security groups | Group users before assigning a protected-API app role | Use direct user membership. Group-based enterprise-app assignment requires the applicable Microsoft Entra license |
| Bootstrap administrator user | Establishes the first usable application administrator and initial organization/department | Must be an enabled tenant **Member** user, not a guest; identified by `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID` |
| Stack A user-assigned managed identity | Runtime identity for Stack A | Becomes an Azure SQL contained user; optionally receives the AWReason API application role |
| Stack B user-assigned managed identity | Runtime identity for Stack B | Becomes an Azure SQL contained user; optionally receives the AWReason API application role |
| Azure SQL Microsoft Entra administrator | Control-plane and data-plane bootstrap identity for the SQL logical server | Selected with `SQL_AAD_ADMIN_LOGIN` and `SQL_AAD_ADMIN_OBJECT_ID` when this repository creates SQL |
| AWReason API enterprise application, when enabled | External protected API called by the application workloads | Both managed identities receive `AWR_API_APP_ROLE_VALUE` (normally `TalentMatch.Access`) when `AWR_AUTH_MODE=entra` |

The SPA registrations are public clients. **Do not create, request, store, or
hand off client secrets for either SPA.** Client IDs, object IDs, scope IDs,
tenant IDs, redirect URIs, and application-role IDs are identifiers rather than
credentials, but they should still be kept in the controlled deployment record.

### Separation of Duties

Use time-bound/PIM activation where available and scope ownership to these
specific applications, groups, resource groups, or SQL resources.

| Person or team | Responsibility | Practical minimum access |
| --- | --- | --- |
| Entra application administrator | Create or approve the three app registrations and enterprise applications, expose the API scope, create app roles, set owners, and grant delegated admin consent | Owner of the specific application/service-principal objects where sufficient; otherwise time-bound Cloud Application Administrator or Application Administrator |
| Entra group administrator | Create and maintain app-specific security groups | Owner of the specific groups; use Groups Administrator only when object ownership is insufficient |
| Azure infrastructure operator | Run Terraform for the resource group, App Service plan/apps, managed identities, SQL, Key Vault, APIM, and networking selected by the profile | Contributor on the deployment resource scope; Network Contributor when creating/configuring network resources, or Reader for supported existing-subnet reuse |
| SQL control-plane administrator | Select or change the SQL logical server's Microsoft Entra administrator | A role containing `Microsoft.Sql/servers/administrators/write` on that SQL server |
| SQL data-plane bootstrap operator | Create the `talentmatch` schema and managed-identity database users | The configured SQL Microsoft Entra administrator, or a deliberately delegated database principal with equivalent schema/user/role-management rights |
| Application bootstrap operator | Create the first TalentMatch Admin assignment and initial organization/department | API enterprise-application owner or application administrator for the Graph assignment, plus SQL bootstrap access |
| Release operator | Build ZIP artifacts and deploy them to the existing App Services | Website Contributor on the target apps, or an organization-approved custom equivalent; no directory role is required for code-only ZIP deployment |
| TalentMatch application administrator | Manage routine organization, department, recruiter, and panel access after bootstrap | The in-application `admin` or scoped `organization_admin` role; no Azure RBAC, SQL permission, or directory role |

The current `seed-entra-admin.sh` safety contract requires the bootstrap
administrator user to be the same object as the Azure SQL Microsoft Entra
administrator. In a strictly separated organization, activate the necessary
rights for that named bootstrap operator only for the bootstrap window, run the
checks below, and remove the temporary directory/Azure privileges afterward.
Routine application administration does not require those privileges.

### Choose Who Owns Entra Provisioning

There are two supported operating models.

#### Model A: Terraform Creates the Entra Objects

Set `ENTRA_REUSE=FALSE`. The identity running the shared Terraform apply must be
permitted to create applications, service principals, app roles, optional
groups, and app-role assignments. Terraform creates the protected API and both
SPA registrations. The deploying identity becomes an owner of the application
registrations it creates.

This is convenient for a small team, but it combines Azure deployment and Entra
application administration during the shared-infrastructure step. Grant the
directory role only for that change window.

#### Model B: An Entra Administrator Pre-Creates the Objects

Use this model when directory and Azure duties are separated:

1. The Entra administrator creates and validates the protected API and both SPA
   registrations using the checklist below.
2. The Entra administrator records the immutable IDs and consent status in the
   approved handoff system.
3. The infrastructure operator sets `ENTRA_REUSE=TRUE` and supplies all existing
   IDs in the environment profile.
4. Terraform reads and wires the existing registrations instead of creating or
   modifying them. The shared apply still manages app-role assignments as
   described below.
5. The Entra/SQL bootstrap operators complete their post-provisioning steps
   after the Azure managed identities and SQL server exist.

When `ENTRA_REUSE=TRUE`, all four of these values are mandatory:

```ini
ENTRA_API_APP_CLIENT_ID=<protected-api-application-client-id>
ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=<protected-api-enterprise-app-object-id>
ENTRA_STACK_A_CLIENT_ID=<stack-a-application-client-id>
ENTRA_STACK_B_CLIENT_ID=<stack-b-application-client-id>
```

The app-role IDs in the environment profile must match the IDs actually defined
on the reused protected API. Never generate replacement role IDs during a
redeployment:

```ini
ENTRA_ADMIN_APP_ROLE_ID=<admin-role-id>
ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=<organization-admin-role-id>
ENTRA_RECRUITER_APP_ROLE_ID=<recruiter-role-id>
ENTRA_BUSINESS_PANEL_APP_ROLE_ID=<business-panel-role-id>
```

`ENTRA_REUSE=TRUE` is not a completely directory-read-only deployment mode.
The shared apply still creates the bootstrap user's app-role assignment on the
existing protected API enterprise application. If `AWR_AUTH_MODE=entra`, it
also assigns both Azure managed identities to the configured AWReason API app
role. If optional Terraform-managed role groups are configured directly, it
creates and assigns those as well. The shared deployment identity must therefore
own the affected enterprise applications/groups or receive a time-bound
application/group administration role. Registration reuse removes broad
registration lifecycle ownership; it does not remove these explicit assignment
writes.

### Entra Administrator Portal Checklist

Perform these steps in the target tenant in the
[Microsoft Entra admin center](https://entra.microsoft.com/).

#### 1. Create or Verify the Protected API

1. Go to **Entra ID > App registrations > New registration**.
2. Use `<project>-<environment>-api` as the display name and select
   **Accounts in this organizational directory only**.
3. Under **Expose an API**, set the Application ID URI to the agreed
   `ENTRA_API_IDENTIFIER_URI`.
4. Add the delegated scope whose value is `ENTRA_API_SCOPE`, normally
   `access_as_user`. This repository defines it as an administrator-consent
   scope.
5. Under **App roles**, create enabled roles for Users/Groups with the exact
   values `admin`, `organization_admin`, `recruiter`, and `business_panel`.
   Preserve the resulting role IDs.
6. Record the app registration's **Application (client) ID**.
7. Open the corresponding item under **Enterprise applications** and record its
   **Object ID**. Do not confuse this with the application client ID.
8. Assign at least two approved owners to both the registration and enterprise
   application so ownership does not depend on the original deployer.

#### 2. Create or Verify Each SPA

Repeat for Stack A and Stack B:

1. Create a single-tenant app registration.
2. Under **Authentication**, add a **Single-page application** platform and
   enter the exact HTTPS redirect URI or URIs from the environment profile.
3. Under **API permissions**, select **My APIs**, select the protected API, and
   add its delegated `access_as_user` scope.
4. Do not add a web platform, client secret, or certificate for the browser
   client.
5. Record the **Application (client) ID** and assign durable owners.

Redirect URIs are exact-match security boundaries. Record scheme, hostname,
path, port, and trailing slash exactly as configured. Update the registration
before changing an application's public URL.

#### 3. Grant and Verify Consent

Adding `required_resource_access` declares the requested permission; it does not
itself create a tenant-wide OAuth consent grant. An authorized consent
administrator must therefore:

1. Open each SPA under **App registrations > API permissions**.
2. Confirm that only the intended TalentMatch delegated scope is requested.
3. Select **Grant admin consent for \<tenant\>**.
4. Confirm that the permission status shows granted for both SPAs.

Cloud Application Administrator or Application Administrator can grant this
delegated consent. Use Privileged Role Administrator only if tenant policy or a
different, more privileged requested permission requires it. Consent should be
reviewed again whenever requested permissions change.

#### 4. Assign Application Roles

The bootstrap user receives a direct `admin` assignment. Other users should
normally receive access through app-specific security groups assigned to one of
the protected API's roles:

1. Open **Enterprise applications > \<protected API\> > Users and groups**.
2. Add the approved user or security group.
3. Select exactly one intended application role.
4. Record the group object ID and app-role ID for the application authorization
   mapping.

The directory assignment alone does not authorize TalentMatch. The application
also requires an active organization/department assignment in its shared SQL
authorization model. This fail-closed design prevents an Azure or directory
administrator from gaining application access accidentally.

### Cross-Team Handoff Record

The Entra administrator should provide the infrastructure/bootstrap operators
with a reviewed record containing:

- Tenant ID and environment name.
- Protected API display name, application client ID, enterprise-application
  object ID, identifier URI, scope value, and scope ID.
- Stack A and Stack B display names, application client IDs, and exact redirect
  URIs.
- All four app-role values and immutable role IDs.
- Bootstrap administrator user object ID and user principal name.
- Optional role-group names and object IDs.
- Owner names for every app registration, enterprise application, and group.
- Admin-consent status and approval date for each SPA.
- If AWReason Entra authentication is enabled, its API client ID, enterprise
  application owner, required app-role value, and assignment status for both
  managed identities.

Do not put access tokens, refresh tokens, passwords, certificates, client
secrets, or concrete environment files in tickets, email, or deployment logs.

### Ordered Handoff and Verification

Use this order so each administrator can finish without receiving unrelated
standing privileges:

1. **Entra administrator:** create or approve the app registrations, roles,
   owners, SPA permissions, and consent; provide the handoff record.
2. **Infrastructure operator:** populate the environment profile, authenticate
   to its exact tenant/subscription, and run a Terraform plan for review:

   ```bash
   ./infra/scripts/deploy.sh <env-file> <dev|test|prod> plan shared-only
   ```

3. **Infrastructure operator:** after approval, apply shared infrastructure and
   then the required stack roots. If this operator cannot bootstrap a
   private/reused SQL database, set `AZ_SQL_BOOTSTRAP_ENABLED=FALSE`.
4. **Entra administrator:** when `AWR_AUTH_MODE=entra`, verify that both new
   managed identities are assigned `AWR_API_APP_ROLE_VALUE` on the AWReason API
   enterprise application. The shared Terraform apply creates these
   assignments only when its executing identity has permission on that
   enterprise application.
5. **SQL bootstrap operator:** from a host that can reach SQL, create the
   managed-identity database users. The supplied helper grants each identity
   `db_datareader`, `db_datawriter`, and `db_ddladmin`:

   ```bash
   export SQL_SERVER_FQDN="<server>.database.windows.net"
   export SQL_DATABASE_NAME="<database-name>"
   export STACK_A_IDENTITY_NAME="<stack-a-managed-identity-name>"
   export STACK_B_IDENTITY_NAME="<stack-b-managed-identity-name>"

   node infra/scripts/bootstrap-sql-entra-users.mjs
   ```

   The helper requires `SQL_SERVER_FQDN`, `SQL_DATABASE_NAME`,
   `STACK_A_IDENTITY_NAME`, and `STACK_B_IDENTITY_NAME` in its environment.
6. **Application bootstrap operator:** in Git Bash, validate and then converge
   the first administrator assignment:

   ```bash
   export ACTIVE_ENV_FILE="<env-file>"
   export MSYS_NO_PATHCONV=1

   ./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" check
   ./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" apply
   ./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" apply
   ./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" check
   ```

   The repeated `apply` is intentional and proves that the operation is
   idempotent.
7. **Release operator:** package and deploy the application ZIP. Code-only ZIP
   deployment does not require Entra application-administrator rights.
8. **Application administrator:** sign in and perform all subsequent
   organization and department access changes through the application.

Every operator must verify the active context before mutation:

```bash
az account show --query "{tenantId:tenantId, subscriptionId:id, name:name}" --output json
```

The deployment and authorization scripts also perform this check and fail on a
tenant or subscription mismatch. For group mapping, assignment, revocation,
failure recovery, and exit codes, follow
[docs/ENTRA_AUTHORIZATION.md](docs/ENTRA_AUTHORIZATION.md).

## Additional Documentation

- [INTEGRATION.md](INTEGRATION.md): API mappings and cross-stack integration details
- [SECURITY.md](SECURITY.md): security model and reporting guidance
- [infra/README.md](infra/README.md): Azure infrastructure and deployment workflow
- [PARITY.md](PARITY.md): Stack A and Stack B parity notes
- [EDIT_JOB_SPECIFICATION.md](EDIT_JOB_SPECIFICATION.md): job-editing behavior and configuration details
