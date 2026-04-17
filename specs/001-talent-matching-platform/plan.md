# Implementation Plan: Talent Matching Platform

**Branch**: `001-talent-matching-platform` | **Date**: 2026-03-14 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-talent-matching-platform/spec.md`

## Summary

Build a dual-stack (React/TypeScript + .NET Blazor WASM) talent matching platform that enables recruiters to create jobs with scoring rubrics, upload candidate applications in bulk, run AI-powered multi-pass scoring via an external reasoning engine (`AWR_SEQ_API_ENDPOINT/assess/passthrough`), and review results through ranked lists and a three-pane manual review interface. The platform enforces a prompt test-and-approve workflow before production scoring, immutable audit trails for all state changes, and role-based access control. Shared API contracts remain the parity target, but Stack B still needs reset-request retrieval completion before feature parity can be re-verified.

Key technical decisions from research: reuse the existing passthrough API pattern for prompt generation and scoring (R3, R10); immutable integer-versioned prompts with single-active-per-job semantics (R4); reuse the existing upload/scoring pipeline for test runs with a `promptVersionId` override to bypass the production gate (R5, R11); auto-trigger scoring on test upload via fire-and-forget (R12); extended `PromptTestRun` status enum for pipeline progress visibility (R13); shared AWR auth helper per `AWR_AUTH_MODE` env var (R6).

## Technical Context

**Language/Version**: TypeScript ~5.7.2 (Stack A), .NET 10.0 (Stack B)
**Primary Dependencies**:
- Stack A: React 19, Vite 7, Express 4, Tailwind CSS 4, shadcn/ui (Radix UI), TanStack Query, Zod, React Hook Form, Recharts, Framer Motion
- Stack B: Blazor WebAssembly, ASP.NET Core Minimal APIs, MediatR 12.4.1, FluentValidation 12.1.1, EF Core (SQLite dev / SQL Server prod), Azure.Identity
**Storage**: Local JSON KV store (Stack A dev), Azure SQL via `mssql` (Stack A prod); SQLite (Stack B dev), Azure SQL Server (Stack B prod) — both behind `StorageProvider` / repository abstractions
**Testing**: Vitest 4 (Stack A), xUnit (Stack B — Application.Tests, Domain.Tests, Infrastructure.Tests, Web.Tests)
**Target Platform**: Web browser (SPA) + Node.js 22+ server (Stack A) + .NET 10 server hosting Blazor WASM (Stack B)
**Project Type**: Web application (dual-stack, frontend + backend)
**Performance Goals**: 20K applications uploadable per job within 90 min (SC-001); each application scored within 15 min (SC-002); dashboard stats refresh ≤30s (SC-003); recruiter end-to-end flow within 10 min of first use (SC-006)
**Constraints**: ≤30s dashboard stat staleness, WCAG ≥4.5:1 contrast ratio, RBAC enforced end-to-end, immutable audit trail for all decisions, no client-side API keys
**Scale/Scope**: 20K applications per job, single-tenant initial deployment, ~10 major UI views, 8 user stories (US1–US8), 60 functional requirements (FR-001–FR-060)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable by Design | ✅ PASS | All 15 entities fully typed in `src/types/index.ts` (312 lines, 27 interfaces). ProcessingEvent provides immutable ledger with correlationId, actor, timestamp. ScoringPrompt and PromptTestRun (new entities) follow the same pattern. |
| II | Layered Architecture | ✅ PASS | Stack A: Express routes → API client → UI components. Stack B: Domain → Application → Infrastructure → Presentation (5 .csproj projects in Clean Architecture). No crosscutting violations. |
| III | Storage Abstraction | ✅ PASS | Stack A: `StorageProvider` interface in `server/storage/types.ts` with local-kv and azure-sql implementations. Stack B: repository interfaces in Domain, EF Core implementations in Infrastructure. |
| IV | Security Defaults | ✅ PASS | SHA-256 hashing (consistency validated by `PasswordHashConsistencyTests`). RBAC via `server/middleware/rbac.ts` and ASP.NET Core policies. File upload validation on both stacks. No `VITE_` secrets. No SAS tokens — RBAC for blob access. |
| V | LLM Integration Discipline | ✅ PASS | All LLM calls proxied through `server/routes/llm.ts` (Stack A) and `LlmProxyService` (Stack B). Frontend calls `/api/llm` only. Prompts constructed server-side. Scoring via `AWR_SEQ_API_ENDPOINT/assess/passthrough` — server-side only. |
| VI | UI Precision & Responsiveness | ✅ PASS | Sonner toast notifications for async ops. 30s polling for dashboard (SC-003). Pagination for large tables. All Radix UI components support loading states. |
| VII | Simplicity & YAGNI | ✅ PASS | No unnecessary abstractions added. Test scoring reuses existing pipeline (R5, R11) rather than creating parallel infrastructure. Fire-and-forget for auto-trigger (R12) rather than message queue for single-tenant. |
| VIII | System Communication Architecture | ✅ PASS | Client → API → Orchestrator → Workers → Results flow preserved. All external calls via APIM/passthrough pattern. SignalR optional for Stack B (polling baseline). |
| IX | Clean Architecture (.NET) | ✅ PASS | Dependencies point inward: Domain (zero dependencies) ← Application (MediatR, FluentValidation) ← Infrastructure (EF Core, Azure.Identity) ← Presentation (Blazor WASM + Web API). Solution structure: `src/Domain/`, `src/Application/`, `src/Infrastructure/`, `src/Web.Server/`, `src/Web.Client/`. |

**Gate Result**: ✅ ALL PASS — No violations. Proceed to Phase 0.

### Post-Phase 1 Re-check

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable | ✅ PASS | ScoringPrompt (14 fields) and PromptTestRun (9 fields) defined in data-model.md. PromptStatus and TestRunStatus enums specified. All lifecycle events logged to ProcessingEvent. |
| II | Layered Architecture | ✅ PASS | New routes/endpoints follow existing patterns. Stack A: `server/routes/prompts.ts`. Stack B: `Application/Prompts/` commands/queries, `Infrastructure/Persistence/Repositories/ScoringPromptRepository.cs`, `Web.Server/Endpoints/PromptEndpoints.cs`. |
| III | Storage Abstraction | ✅ PASS | Prompt data stored via KV (Stack A) and repository pattern (Stack B). No direct storage access from routes/endpoints. |
| IV | Security | ✅ PASS | AWR auth helper (`server/services/awr-auth.ts`, `Infrastructure/Services/AwrAuthHandler.cs`) handles `AWR_AUTH_MODE` modes. No secrets in client bundle. |
| V | LLM Discipline | ✅ PASS | Prompt generation and scoring both go through server-side passthrough. No client-side LLM calls. |
| VI | UI Precision | ✅ PASS | Test run cards show status transitions (pending_scoring → scoring → pending_review). Review Results dialog with summary before full manual review. |
| VII | YAGNI | ✅ PASS | Reuse existing scoring pipeline for test runs. No separate test-only infrastructure. |
| IX | Clean Architecture | ✅ PASS | New commands/queries in Application layer. New entities in Domain. Repository implementations in Infrastructure. Endpoints in Web.Server. |

**Post-Phase 1 Gate Result**: ✅ ALL PASS — No violations.

## Project Structure

### Documentation (this feature)

```text
specs/001-talent-matching-platform/
├── plan.md              # This file
├── research.md          # Phase 0: 13 research decisions (R1–R13)
├── data-model.md        # Phase 1: 15 entities with fields, relationships, state machines
├── quickstart.md        # Phase 1: Developer setup guide for both stacks
├── contracts/
│   ├── api-stack-a.md   # Phase 1: Express API contract (all endpoints)
│   ├── api-stack-b.md   # Phase 1: ASP.NET Core API contract (mirrors Stack A)
│   └── scoring-passthrough.md  # Phase 1: AWR engine passthrough contract
└── tasks.md             # Phase 2: Generated by /speckit.tasks (not created by /speckit.plan)
```

### Source Code (repository root)

```text
# Stack A — React / TypeScript / Express
src/
├── components/          # 20+ feature components (LoginForm, DashboardView, JobDetailView, etc.)
│   └── ui/              # 45+ shadcn/ui component wrappers (button, dialog, table, etc.)
├── hooks/               # Custom React hooks (use-mobile.ts)
├── lib/
│   ├── api.ts           # API client abstraction (50+ functions)
│   ├── api-mock.ts      # Mock API for offline dev
│   ├── api-real.ts      # Real API implementation
│   ├── auth.ts          # Auth utilities
│   ├── spark-client.ts  # KV + LLM proxy client
│   └── utils.ts         # Shared utilities
├── styles/              # CSS assets
└── types/
    └── index.ts         # All domain types (27 interfaces, 312 lines)

server/
├── index.ts             # Express app entry
├── middleware/           # auth.ts, rbac.ts, error-handler.ts, validate.ts
├── routes/              # 10 route files (auth, jobs, applications, prompts, etc.)
├── services/            # 6 services (audit, awr-auth, pipeline, prompt-helpers, etc.)
├── storage/             # StorageProvider interface + local-kv + azure-sql implementations
└── workers/             # scoring.ts, aggregation.ts (extraction.ts deprecated — AWR API handles OCR internally per FR-059)

tests/                   # Vitest test files

# Stack B — .NET 10 / Blazor WASM / Clean Architecture
dotnet/
├── TalentMatch.slnx     # Solution file (5 src + 4 test projects)
├── src/
│   ├── Domain/           # Entities (16), Enums, Interfaces — zero framework deps
│   ├── Application/      # CQRS commands/queries, validators (MediatR + FluentValidation)
│   ├── Infrastructure/   # EF Core DbContext, Repositories (7), Services (AwrAuth, LlmProxy)
│   ├── Web.Server/       # ASP.NET Core host, Endpoints (8), Swagger, cookie auth
│   └── Web.Client/       # Blazor WASM app, Pages, Components, Layout, Services
└── tests/
    ├── Application.Tests/     # Command/query handler tests
    ├── Domain.Tests/          # Entity/value object tests
    ├── Infrastructure.Tests/  # Repository/service tests
    └── Web.Tests/             # Endpoint/integration tests
```

**Structure Decision**: Dual-stack web application. Stack A uses a flat `src/` + `server/` layout with component-per-file organisation. Stack B follows Clean Architecture with 5 separate .csproj projects enforcing dependency boundaries. Both stacks share the same API contract surface and store data in compatible formats.

## Complexity Tracking

> No violations detected. All design decisions align with constitution principles.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| *(none)* | — | — |
