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

- **Node.js** 20.19+ or 22.12+
- **npm** 10+

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

Open `http://localhost:5173` in your browser. Default login: `admin` / `adm1n99`

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
| `APP_AUTH_MODE` | `simple` | Use local credentials or `entra` authentication |
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

- `APP_AUTH_MODE=simple` provides local username/password authentication for demos and offline development.
- `APP_AUTH_MODE=entra` uses single-tenant Microsoft Entra ID authorization-code flow with PKCE. API access is resolved from persisted application assignments and organization/department memberships rather than relying on token roles alone.

Entra mode supports `admin`, `organization_admin`, `recruiter`, and `business_panel` roles. Authorization changes use optimistic concurrency, revocation is reflected through authorization-version changes, and mutations produce correlated audit events.

See [AUTHENTICATION.md](AUTHENTICATION.md) for behavior and [docs/ENTRA_AUTHORIZATION.md](docs/ENTRA_AUTHORIZATION.md) for setup and operator guidance.

## Additional Documentation

- [INTEGRATION.md](INTEGRATION.md): API mappings and cross-stack integration details
- [SECURITY.md](SECURITY.md): security model and reporting guidance
- [infra/README.md](infra/README.md): Azure infrastructure and deployment workflow
- [PARITY.md](PARITY.md): Stack A and Stack B parity notes
- [EDIT_JOB_SPECIFICATION.md](EDIT_JOB_SPECIFICATION.md): job-editing behavior and configuration details
