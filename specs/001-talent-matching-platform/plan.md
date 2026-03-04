# Implementation Plan: Talent Matching Platform

**Branch**: `001-talent-matching-platform` | **Date**: 2026-03-03 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-talent-matching-platform/spec.md`

---

## Summary

A production-grade Azure-hosted React/Express application that uses AI reasoning models to score
job applications at scale. The frontend is a React 19 + TypeScript + Tailwind CSS SPA; the backend
is a lightweight Express 4 server acting as a KV store proxy and LLM proxy. All persistence is
abstracted behind a `StorageProvider` interface supporting local JSON (dev) and Azure SQL (prod).
The current codebase has a working auth system, mock API layer, and full UI; the primary remaining
work is replacing the simulated API layer with real backend storage and wiring the AI scoring
pipeline.

---

## Technical Context

**Language/Version**: TypeScript 5.7, Node.js 22+, React 19
**Primary Dependencies**: Express 4, Vite 7, Tailwind CSS 4, shadcn/ui (New York), Framer Motion,
Recharts, Phosphor Icons, React Hook Form + Zod, TanStack Query (available but not yet wired)
**Storage**: Local: `.data/kv-store.json` (file-backed KV). Cloud: Azure SQL via `mssql` (optional dep).
**LLM**: OpenAI API or Azure OpenAI, server-side proxy only via `server/routes/llm.ts`
**Testing**: Not yet configured — to be set up (Vitest recommended, matches Vite ecosystem)
**Target Platform**: Web browser (desktop primary, mobile responsive); backend: Linux/Windows server or Azure App Service
**Project Type**: Full-stack web application (SPA + REST API)
**Performance Goals**: Upload 20 k applications in <5 min; dashboard stats accurate within 30 s; list
rendering smooth at 1 k+ rows (virtual scroll)
**Constraints**: Max item size 2 MB (Cosmos DB compatibility); SHA-256 duplicate detection; no
client-side LLM keys; role-scoped data access
**Scale/Scope**: 20 k+ applications per job; N=3 scoring runs default; multi-department, multi-org

---

## Constitution Check

| Principle | Status | Notes |
|---|---|---|
| I. Typed & Auditable | ✅ | `src/types/index.ts` comprehensive; ledger pattern defined |
| II. Layered Architecture | ✅ | `server/` / `src/lib/api.ts` / `src/components/` clearly separated |
| III. Storage Abstraction | ✅ | `StorageProvider` interface in place; factory pattern used |
| IV. Security Defaults | ✅ | SHA-256 password hash via `crypto.subtle`; no `VITE_` secrets |
| V. LLM Discipline | ✅ | `/api/llm` proxy in `server/routes/llm.ts` |
| VI. UI Precision | ⚠️ | Mock data in use; polling exists but not real-time |
| VII. YAGNI | ✅ | Simulated API layer is acceptable at current stage |

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

### Milestone A: Real persistence (replace mock API)
implement a switch in a configuration file to switch between mock api and real api.
For the real api, replace `generateMockJobs()` and related mock generators in `src/lib/api.ts` with calls to the
real `/api/kv/*` endpoints. Implement proper RESTful routes in `server/routes/` for jobs,
applications, users. The frontend `api.ts` functions map 1:1 to the endpoint contracts above.

Key files to create/modify:
- `server/routes/jobs.ts` — CRUD for jobs + config versioning
- `server/routes/applications.ts` — upload, list, get detail
- `server/routes/users.ts` — user management (currently handled by auth.ts client-side)
- `src/lib/api.ts` — replace all mock generators with real fetch calls

### Milestone B: AI scoring pipeline (backend workers)

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

### Milestone C: Real-time pipeline monitoring

in Real API mode ( not mock api mode), replace polling simulation with actual data using Azure SignalR: stats endpoint aggregates counts across the KV store;
pipeline visualiser reads real application statuses; DLQ shows real entries.

### Milestone D: Testing (Vitest)

Configure Vitest with `jsdom` for frontend components and Node environment for backend. Minimum
coverage targets:
- `src/lib/auth.ts` — unit tests for hash, login, changePassword
- `src/lib/api.ts` — integration tests against mock KV backend
- `server/storage/local-kv.ts` — unit tests for all CRUD ops

---

## Constraints & Risks

| Risk | Mitigation |
|---|---|
| LLM token costs at scale (20 k apps × N runs) | Configurable N; cost tracking via `inputTokens`/`outputTokens` per run |
| Azure SQL cold start in production | Connection pooling in `azure-sql.ts`; health check endpoint |
| Very large items (>2 MB per KV entry) | Paginate `ledger` and `applications` arrays; store documents by reference |
| No background worker support in current Express setup | Extract pipeline workers to separate process or use Azure Functions |
| No auth session token (session stored in KV, no JWT) | Acceptable for demo; plan MSAL/Entra ID migration path documented in AUTHENTICATION.md |

---

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| Dual storage providers (`local-kv.ts` + `azure-sql.ts`) | Azure SQL is required for production deployment on Azure; local JSON KV needed for zero-dependency local dev | Single provider would require devs to run SQL Server locally, blocking onboarding; cloud-only would break offline dev |
