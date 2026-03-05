# Implementation Plan: Talent Matching Platform

**Branch**: `001-talent-matching-platform` | **Date**: 2026-03-03 | **Updated**: 2026-03-04 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-talent-matching-platform/spec.md`
**Constitution**: v1.1.0

---

## Summary

A production-grade Azure-hosted talent matching platform supporting **two technology stacks** (see
constitution §Technology Stacks):

- **Stack A** (React 19 / Express 4 / TypeScript) — the current prototype with a working auth
  system, mock API layer, and full UI. Primary remaining work: replace simulated API with real
  backend storage and wire the AI scoring pipeline.
- **Stack B** (Blazor WebAssembly / ASP.NET Core / Clean Architecture / EF Core) — the target
  implementation for a specific customer. Must implement the same feature set using Clean
  Architecture (constitution Principle IX) with EF Core for persistence and Entra ID for auth.

Both stacks share the same AI pipeline backend (awr-platform) via the communication architecture
defined in constitution §VIII (Client → OpenAPI client → APIM → Platform API → Orchestrator →
Service Bus → Engine Workers → Azure SQL / Blob / SignalR). The frontend+API layers differ per
stack; the scoring engine and data contracts are shared.

---

## Technical Context

### Stack A — React / TypeScript (prototype)

**Language/Version**: TypeScript 5.7, Node.js 22+, React 19
**Primary Dependencies**: Express 4, Vite 7, Tailwind CSS 4, shadcn/ui (New York), Framer Motion,
Recharts, Phosphor Icons, React Hook Form + Zod, TanStack Query (available but not yet wired)
**Storage**: Local: `.data/kv-store.json` (file-backed KV). Cloud: Azure SQL via `mssql` (optional dep).
**LLM**: OpenAI API or Azure OpenAI, server-side proxy only via `server/routes/llm.ts`
**Testing**: Not yet configured — to be set up (Vitest recommended, matches Vite ecosystem)
**Auth**: KV-backed SHA-256 password hashing (demo); MSAL/Entra ID migration path planned

### Stack B — .NET Blazor WASM / Clean Architecture (target)

**Language/Version**: C# / .NET 9+
**Frontend**: Blazor WebAssembly (hosted model), MudBlazor or Fluent UI Blazor (TBD)
**API**: ASP.NET Core Web API (minimal APIs preferred)
**Application Layer**: MediatR (CQRS), FluentValidation
**ORM / Migrations**: Entity Framework Core (code-first migrations)
**Storage**: Azure SQL (via EF Core; no local KV — EF Core with SQLite for local dev is acceptable)
**Auth**: Microsoft Identity / Entra ID
**LLM**: Azure OpenAI via APIM (server-side only)
**Testing**: xUnit + bUnit (Blazor component tests), FluentAssertions
**Architecture**: Clean Architecture (constitution Principle IX)

### Shared Context

**Target Platform**: Web browser (desktop primary, mobile responsive); backend: Linux/Windows server or Azure App Service
**Project Type**: Full-stack web application (SPA + REST API)
**Performance Goals**: Upload 20 k applications in <90 min; dashboard stats accurate within 30 s; list
rendering smooth at 1 k+ rows (virtual scroll)
**Constraints**: Max item size 2 MB (Cosmos DB compatibility); SHA-256 duplicate detection; no
client-side LLM keys; role-scoped data access
**Scale/Scope**: 20 k+ applications per job; N=3 scoring runs default; multi-department, multi-org
**Communication**: Both stacks communicate with the awr-platform backend via the architecture defined
in constitution §VIII (OpenAPI client → APIM → Platform API → Orchestrator → Service Bus → Workers)

---

## Constitution Check

| Principle | Status | Notes |
|---|---|---|
| I. Typed & Auditable | ✅ | `src/types/index.ts` comprehensive; ledger pattern defined. Stack B: domain entities in `src/Domain/` |
| II. Layered Architecture | ✅ | Stack A: `server/` / `src/lib/api.ts` / `src/components/` separated. Stack B: Clean Architecture layers (Principle IX) |
| III. Storage Abstraction | ✅ | Stack A: `StorageProvider` interface + factory. Stack B: EF Core repositories behind interface contracts |
| IV. Security Defaults | ✅ | SHA-256 password hash (Stack A); Entra ID (Stack B). No `VITE_` secrets. RBAC enforced server-side. SAS tokens forbidden — RBAC only for Blob Storage |
| V. LLM Discipline | ✅ | Stack A: `/api/llm` proxy. Stack B: ASP.NET Core Web API → APIM → Azure OpenAI |
| VI. UI Precision | ⚠️ | Mock data in use (Stack A); polling exists but not real-time. Stack B: not yet started |
| VII. YAGNI | ✅ | Simulated API layer acceptable at current stage |
| VIII. System Communication | ✅ | Architecture defined in constitution Mermaid diagram. Both stacks conform to: Client → OpenAPI → APIM → Platform API → Orchestrator → Service Bus → Workers |
| IX. Clean Architecture (.NET) | ⚠️ | Principle defined; Stack B solution structure not yet scaffolded. Domain/Application/Infrastructure/Presentation layers planned |

---

## Project Structure

### Documentation (this feature)

```text
specs/001-talent-matching-platform/
├── spec.md              # Feature specification (US1–US8)
├── plan.md              # This file
├── research.md          # Phase 0 research output (not yet created)
├── data-model.md        # Phase 1 data model detail (not yet created)
├── quickstart.md        # Developer quickstart (not yet created)
├── contracts/           # Detailed API contracts (not yet created)
└── tasks.md             # Phase-by-phase task list (created by /speckit.tasks)
```

### Repository Layout (existing)

```text
server/                          # Express backend
  index.ts                       # Server entry; loads .env, mounts routes
  routes/
    kv.ts                        # GET/PUT/DELETE /api/kv/:key
    llm.ts                       # POST /api/llm  — LLM proxy
  storage/
    types.ts                     # StorageProvider interface
    factory.ts                   # Reads STORAGE_PROVIDER, returns provider
    local-kv.ts                  # JSON file store (.data/kv-store.json)
    azure-sql.ts                 # Azure SQL KV store (mssql)

src/
  App.tsx                        # Top-level routing/view state
  components/
    LoginForm.tsx                # US1
    UserManagementDialog.tsx     # US2
    ChangePasswordDialog.tsx     # US2
    UserMenu.tsx                 # US2
    CreateJobDialog.tsx          # US3
    UploadApplicationsDialog.tsx # US4
    UploadRubricDialog.tsx       # US3 (rubric upload)
    DashboardView.tsx            # US8
    JobCard.tsx                  # US8
    JobDetailView.tsx            # US6
    ApplicationsTable.tsx        # US6
    ApplicationDetail.tsx        # US6, US7
    ManualReviewView.tsx         # US7
    PipelineVisualizer.tsx       # US8
    AnalyticsView.tsx            # US8
    StatCard.tsx                 # US8
    StatusBadge.tsx              # shared
    DraggableResizableDialog.tsx # shared
  lib/
    api.ts                       # API abstraction (currently simulated + KV)
    auth.ts                      # KV-backed auth operations
    spark-client.ts              # KV + LLM API client (calls backend)
    utils.ts
  types/
    index.ts                     # All entity types
  styles/
    theme.css
```

**Structure Decision**: Web application layout selected (Option 2 — separate `server/` backend + `src/` frontend). No monorepo split is needed at current scale. The backend is a single Express process; the frontend is a Vite SPA served alongside it in production. Additional server routes (`jobs.ts`, `applications.ts`, `users.ts`) will be added under `server/routes/` as Milestone A work.

### Stack B — .NET Solution Structure (planned)

```text
src/
├── Domain/                          # Core domain (no external dependencies)
│   ├── Entities/
│   │   ├── User.cs                  # User aggregate root
│   │   ├── Job.cs                   # Job aggregate root
│   │   ├── Application.cs           # Application entity
│   │   ├── ScoringRun.cs            # Scoring run value object
│   │   └── AggregatedResult.cs      # Aggregated result entity
│   ├── ValueObjects/
│   │   ├── JobConfigVersion.cs
│   │   ├── RubricCategory.cs
│   │   └── ExtractionArtifact.cs
│   ├── Events/
│   │   └── ProcessingEvent.cs       # Domain events (audit ledger)
│   ├── Enums/
│   │   ├── ApplicationStatus.cs
│   │   └── UserRole.cs
│   └── Interfaces/
│       ├── IJobRepository.cs
│       ├── IApplicationRepository.cs
│       └── IUserRepository.cs
│
├── Application/                     # Use cases (references Domain only)
│   ├── Jobs/
│   │   ├── Commands/
│   │   │   ├── CreateJobCommand.cs
│   │   │   └── UpdateJobConfigCommand.cs
│   │   └── Queries/
│   │       ├── GetJobsQuery.cs
│   │       └── GetJobDetailQuery.cs
│   ├── Applications/
│   │   ├── Commands/
│   │   │   ├── UploadApplicationsCommand.cs
│   │   │   └── SaveManualReviewCommand.cs
│   │   └── Queries/
│   │       ├── GetApplicationsQuery.cs
│   │       └── GetScoringRunsQuery.cs
│   ├── Users/
│   │   ├── Commands/
│   │   │   └── CreateUserCommand.cs
│   │   └── Queries/
│   │       └── GetUsersQuery.cs
│   ├── Common/
│   │   ├── Behaviours/               # MediatR pipeline behaviours
│   │   │   ├── ValidationBehaviour.cs
│   │   │   └── LoggingBehaviour.cs
│   │   └── Interfaces/
│   │       └── ICurrentUserService.cs
│   └── DependencyInjection.cs
│
├── Infrastructure/                   # External concerns (references Application + Domain)
│   ├── Persistence/
│   │   ├── AppDbContext.cs           # EF Core DbContext
│   │   ├── Configurations/           # EF Core entity type configurations
│   │   ├── Migrations/               # EF Core code-first migrations
│   │   └── Repositories/
│   │       ├── JobRepository.cs
│   │       ├── ApplicationRepository.cs
│   │       └── UserRepository.cs
│   ├── Services/
│   │   ├── LlmProxyService.cs        # Azure OpenAI via APIM
│   │   └── SignalRNotificationService.cs
│   └── DependencyInjection.cs
│
├── Web/                              # Presentation (references Application only)
│   ├── Server/                       # ASP.NET Core Web API host
│   │   ├── Controllers/ or Endpoints/
│   │   │   ├── AuthEndpoints.cs
│   │   │   ├── JobsEndpoints.cs
│   │   │   ├── ApplicationsEndpoints.cs
│   │   │   └── UsersEndpoints.cs
│   │   └── Program.cs
│   └── Client/                       # Blazor WASM
│       ├── Pages/
│       │   ├── Dashboard.razor       # US8
│       │   ├── JobDetail.razor        # US6
│       │   ├── ManualReview.razor     # US7
│       │   └── Login.razor            # US1
│       ├── Components/
│       │   ├── JobCard.razor          # US8
│       │   ├── ApplicationsTable.razor# US6
│       │   └── PipelineVisualizer.razor# US8
│       ├── Services/
│       │   └── ApiClient.cs           # Typed HttpClient (equivalent to src/lib/api.ts)
│       └── Program.cs
│
└── Tests/
    ├── Domain.Tests/                  # xUnit domain entity tests
    ├── Application.Tests/             # Use-case handler tests
    ├── Infrastructure.Tests/          # Repository integration tests
    └── Web.Tests/                     # bUnit Blazor component tests
```

**Stack B Structure Decision**: Clean Architecture with four layers per constitution Principle IX.
Blazor WASM hosted model chosen (citizen-facing, requires client-side execution). Minimal APIs
preferred for the Web API layer. EF Core code-first migrations for all schema changes. The solution
will be scaffolded as Milestone E work (see Phase 2).

---

## Phase 0 — Research (complete)

The codebase has been analysed. Key findings:

1. **Auth system is live** — SHA-256 hashing, KV-backed users, role/department filtering, password
   reset request flow all implemented in `src/lib/auth.ts`.
2. **Mock API layer** — `src/lib/api.ts` contains `generateMockJobs()`, `generateMockApplications()`
   etc. This is the primary gap: it must be replaced with real KV-backed persistence.
3. **Backend KV routes live** — `/api/kv/*` is fully working; the mock data bypass is entirely in
   the frontend `api.ts`, NOT in the server.
4. **LLM proxy live** — `server/routes/llm.ts` exists; supports OpenAI and Azure OpenAI; used by
   `UploadRubricDialog` and `UploadApplicationsDialog` for document extraction.
5. **No test framework** — Vitest needs to be configured.
6. **No real pipeline** — The AI scoring pipeline (extraction → N scoring runs → aggregation) is
   represented by mock data only. The backend workers/queue are not yet implemented in this codebase, but exists in a separate "awr platform" codebase. This codebase requires the api endpoints and needs to conform to the api contracts defined for the awr platform backend.  
7. **Azure SQL storage** — `azure-sql.ts` exists and creates a `kv_store` table; not yet validated
   end-to-end. The same azure sql database is used for the persistance of the front-end data as well as the platform data.

---

## Phase 1 — Data Model & Contracts

### KV Key Schema

All entities are stored as JSON values in the KV store under the following key conventions:

| Key Pattern | Value Type | Description |
|---|---|---|
| `auth:users` | `StoredUser[]` | All user accounts (with passwordHash) |
| `auth:current-user` | `User` | Active session user |
| `auth:reset-requests` | `PasswordResetRequest[]` | Pending reset requests |
| `jobs` | `Job[]` | All jobs |
| `job:{jobId}:versions` | `JobConfigVersion[]` | Config version history |
| `job:{jobId}:applications` | `Application[]` | All applications for a job |
| `app:{applicationId}:documents` | `ApplicationDocument[]` | Uploaded documents |
| `app:{applicationId}:extraction` | `ExtractionArtifact` | Extracted Markdown |
| `app:{applicationId}:runs` | `ScoringRun[]` | All N scoring runs |
| `app:{applicationId}:result` | `AggregatedResult` | Final aggregated result |
| `app:{applicationId}:manual-review` | `ManualReviewData` | Manual review state + audit trail |
| `dlq` | `DLQItem[]` | Dead letter queue items |
| `ledger` | `ProcessingEvent[]` | Immutable audit ledger |
| `system:stats` | `SystemStats` | Cached system-wide stats |

### API Endpoint Contracts

| Method | Path | Description |
|---|---|---|
| POST | `/api/auth/login` | Authenticate user |
| POST | `/api/auth/logout` | Clear session |
| GET | `/api/auth/me` | Get current session user |
| POST | `/api/auth/change-password` | Self-service password change |
| GET | `/api/users` | Admin: list all users |
| POST | `/api/users` | Admin: create user |
| DELETE | `/api/users/:userId` | Admin: delete user |
| POST | `/api/users/:userId/reset-password` | Admin: set new password |
| GET | `/api/users/reset-requests` | Admin: pending reset requests |
| POST | `/api/users/reset-requests` | Recruiter: request password reset |
| PUT | `/api/users/reset-requests/:requestId` | Admin: approve/reject |
| GET | `/api/jobs` | List jobs (filtered by department for recruiter) |
| POST | `/api/jobs` | Create job |
| GET | `/api/jobs/:jobId` | Get job with stats |
| PUT | `/api/jobs/:jobId/config` | Update config (creates new version) |
| GET | `/api/jobs/:jobId/applications` | List applications with filters |
| POST | `/api/jobs/:jobId/applications/upload` | Bulk upload documents |
| GET | `/api/applications/:applicationId` | Get application detail |
| GET | `/api/applications/:applicationId/runs` | Get all scoring runs |
| GET | `/api/applications/:applicationId/result` | Get aggregated result |
| GET | `/api/applications/:applicationId/extraction` | Get extracted Markdown |
| GET | `/api/applications/:applicationId/manual-review` | Get manual review data |
| POST | `/api/applications/:applicationId/manual-review` | Save manual review |
| GET | `/api/dlq` | Get DLQ items |
| POST | `/api/dlq/:itemId/retry` | Retry DLQ item |
| GET | `/api/stats` | System-wide stats |
| GET | `/api/audit` | Query audit ledger |

---

## Phase 2 — Implementation Milestones

> Milestones A–D are **Stack A** work. Milestone E is the **Stack B** scaffold and implementation.
> Both stacks share the same API contracts and data model defined in Phase 1.

### Milestone A: Real persistence — Stack A (replace mock API)
implement a switch in a configuration file to switch between mock api and real api.
For the real api, replace `generateMockJobs()` and related mock generators in `src/lib/api.ts` with calls to the
real `/api/kv/*` endpoints. Implement proper RESTful routes in `server/routes/` for jobs,
applications, users. The frontend `api.ts` functions map 1:1 to the endpoint contracts above.

Key files to create/modify:
- `server/routes/jobs.ts` — CRUD for jobs + config versioning
- `server/routes/applications.ts` — upload, list, get detail
- `server/routes/users.ts` — user management (currently handled by auth.ts client-side)
- `src/lib/api.ts` — replace all mock generators with real fetch calls

### Milestone B: AI scoring pipeline — Stack A (backend workers)

wire up the processing pipeline as background async workers triggered by the job queue:

1. **Extraction worker** — reads documents from KV, calls LLM to convert to Markdown, stores
   `ExtractionArtifact`, updates application status.
2. **Scoring worker** — for each extracted application, runs N LLM scoring calls in sequence,
   stores each `ScoringRun`.
3. **Aggregation worker** — once N runs exist, computes aggregated result using configured strategy,
   writes `AggregatedResult`, updates final status.
4. **DLQ manager** — captures failures with error details; supports retry-from-checkpoint.

Note:
The actual AI scoring is performed in the existing awr-engine codebase which is triggered from the awr-platform codebase using Azure service Bus 

### Milestone C: Real-time pipeline monitoring — Stack A

in Real API mode ( not mock api mode), replace polling simulation with actual data using Azure SignalR: stats endpoint aggregates counts across the KV store;
pipeline visualiser reads real application statuses; DLQ shows real entries.

### Milestone D: Testing — Stack A (Vitest)

Configure Vitest with `jsdom` for frontend components and Node environment for backend. Minimum
coverage targets:
- `src/lib/auth.ts` — unit tests for hash, login, changePassword
- `src/lib/api.ts` — integration tests against mock KV backend
- `server/storage/local-kv.ts` — unit tests for all CRUD ops

### Milestone E: Stack B scaffold and implementation (Blazor WASM / Clean Architecture)

Scaffold and implement the .NET Blazor WASM solution following Clean Architecture (Principle IX) and
the system communication architecture (Principle VIII). This milestone produces a **feature-equivalent**
implementation to Stack A targeting a specific customer.

#### E1 — Solution scaffold
- Create .NET solution with projects: `Domain`, `Application`, `Infrastructure`, `Web.Server`,
  `Web.Client` (Blazor WASM), `Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`,
  `Web.Tests`.
- Configure EF Core DbContext with entity type configurations matching the data model in Phase 1.
- Generate initial code-first migration from domain entities.
- Set up MediatR + FluentValidation pipeline behaviours.
- Configure Entra ID authentication (Microsoft Identity) for the Web API.

#### E2 — Domain and Application layers
- Implement domain entities (`Job`, `Application`, `User`, `ScoringRun`, `AggregatedResult`,
  `ProcessingEvent`) with value objects and domain events.
- Implement MediatR command/query handlers for all use cases (US1–US8).
- Implement FluentValidation validators for all commands.
- Implement repository interfaces in Domain; repository implementations in Infrastructure.

#### E3 — Web API endpoints
- Implement minimal API endpoints matching the API Endpoint Contracts table from Phase 1.
- Wire endpoints to MediatR handlers (no business logic in endpoints).
- Add OpenAPI/Swagger generation for integration with the awr-platform.

#### E4 — Blazor WASM frontend
- Implement Razor pages and components matching US1–US8 acceptance scenarios.
- Create typed `ApiClient` service (equivalent to `src/lib/api.ts`).
- Implement SignalR client for real-time pipeline updates.
- Apply UI precision requirements (loading states, toasts, virtual scrolling, WCAG contrast).

#### E5 — Testing
- xUnit + bUnit for component tests.
- FluentAssertions for readability.
- EF Core in-memory or SQLite provider for repository integration tests.
- Minimum coverage: domain entity invariants, command handler happy paths, API endpoint routing.

---

## Constraints & Risks

| Risk | Mitigation |
|---|---|
| LLM token costs at scale (20 k apps × N runs) | Configurable N; cost tracking via `inputTokens`/`outputTokens` per run |
| Azure SQL cold start in production | Connection pooling in `azure-sql.ts` (Stack A) / EF Core connection resilience (Stack B); health check endpoint |
| Very large items (>2 MB per KV entry) | Paginate `ledger` and `applications` arrays; store documents by reference |
| No background worker support in current Express setup | Extract pipeline workers to separate process or use Azure Functions |
| No auth session token — Stack A (session stored in KV, no JWT) | Acceptable for demo; Stack B uses Entra ID with proper token-based auth |
| Dual-stack feature parity drift | Shared spec.md acceptance scenarios serve as cross-stack contract; shared API endpoint contracts |
| Stack B UI component library undecided (MudBlazor vs Fluent UI) | Decision deferred to Milestone E1 project start; both libraries meet WCAG requirements |
| EF Core migration conflicts across team members | Linear migration chain; squash migrations at release boundaries |
| Clean Architecture overhead for simple CRUD | YAGNI (Principle VII) — start with thin handlers; add complexity only when domain logic justifies it |

---

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| Dual storage providers (`local-kv.ts` + `azure-sql.ts`) — Stack A | Azure SQL is required for production deployment on Azure; local JSON KV needed for zero-dependency local dev | Single provider would require devs to run SQL Server locally, blocking onboarding; cloud-only would break offline dev |
| Dual technology stacks (React/Express + Blazor/Clean Architecture) | Stack B targets a specific customer requiring .NET; Stack A is the existing prototype | Single stack would lose either the existing working prototype or the customer requirement |
| Clean Architecture layers for Stack B (Principle IX) adds structural overhead | Required by constitution for .NET stack; enables testability and long-term maintainability | Simpler layering rejected because the customer deployment requires enterprise-grade separation of concerns |
