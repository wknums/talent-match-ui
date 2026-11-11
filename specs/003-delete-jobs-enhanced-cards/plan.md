# Implementation Plan: Delete Jobs and Enhanced Job Cards

**Branch**: `003-delete-jobs-enhanced-cards` | **Date**: 2026-03-12 | **Spec**: `specs/003-delete-jobs-enhanced-cards/spec.md`  
**Input**: Feature specification from `/specs/003-delete-jobs-enhanced-cards/spec.md`

## Summary

Enable admin users to permanently delete jobs (with cascade removal of all associated applications, scoring runs, and config versions) from both the Dashboard and Job Detail page, with confirmation dialogs that warn about dependent data. Enhance Dashboard job cards to display creator name, creation date, and application completion progress. The feature spans both technology stacks: Stack B (.NET Blazor WASM / Clean Architecture) and Stack A (React/Express/TypeScript). The .NET backend already has `DeleteJobCommand`, `IJobRepository.DeleteAsync`, and EF Core cascade delete configured; the Blazor UI implementation is complete. Stack A (Node.js routes + React components) requires new delete endpoint, API client methods, and UI integration.

## Technical Context

**Language/Version**: TypeScript 5.x (Stack A), C# / .NET 9+ (Stack B)  
**Primary Dependencies**:  
- *Stack A*: React 19, Vite 7, Express 4, shadcn/ui, Radix UI, Phosphor Icons, Sonner (toasts)  
- *Stack B*: Blazor WASM (.NET 9+), MediatR (CQRS), Entity Framework Core, ASP.NET Core minimal APIs  
**Storage**: JSON file KV store (`.data/kv-store.json`) for Stack A local dev; Azure SQL via EF Core for Stack B; SQLite for Stack B local dev  
**Testing**: Vitest (Stack A), xUnit + Moq + FluentAssertions (Stack B)  
**Target Platform**: Web browser (Blazor WASM + React SPA)  
**Project Type**: Dual-stack web application (prototype + target)  
**Performance Goals**: Job deletion completes in <10 seconds including confirmation (SC-001)  
**Constraints**: Cascade delete must be atomic — all child entities removed or none. Optimistic UI update with rollback on failure.  
**Scale/Scope**: Dashboard with tens of jobs; cascade delete may affect hundreds of applications per job

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable by Design | ✅ PASS | `JobSummaryDto` defined in `src/types/index.ts` (Stack A) and `Application/Jobs/Queries/` (Stack B). Delete is a state-changing operation — audit event recorded. |
| II | Layered Architecture | ✅ PASS | Stack A: route handler in `server/routes/jobs.ts`, API client in `src/lib/api.ts`, UI in `src/components/`. Stack B: command handler in Application layer, endpoint in Web.Server, UI in Web.Client. No layer violations. |
| III | Storage Abstraction | ✅ PASS | Stack A: all persistence via `StorageProvider` interface. Stack B: all persistence via `IJobRepository` (EF Core). No direct DB access. |
| IV | Security Defaults | ✅ PASS | Delete restricted to admin role: `requireRole('admin')` middleware (Stack A), `RequireAuthorization("AdminOnly")` + `ICurrentUserService.IsAdmin` check (Stack B). Non-admin users never see delete controls. |
| V | LLM Integration | N/A | No LLM calls in this feature. |
| VI | UI Precision & Responsiveness | ✅ PASS | Optimistic delete with rollback on failure. Error handling with Sonner toasts (Stack A) and inline error messages (Stack B). Confirmation dialogs follow existing modal patterns; draggable/resizable behavior is not a constitution requirement. |
| VII | Simplicity & YAGNI | ✅ PASS | No unnecessary abstractions. Reuses existing `StorageProvider`, `IJobRepository`, and `requireRole` patterns. Single-purpose `ConfirmDeleteJobDialog` component (not over-abstracted). |
| IX | Clean Architecture (.NET) | ✅ PASS | `DeleteJobCommand` in Application layer with handler. Endpoint in Presentation layer dispatches via MediatR. Repository in Infrastructure layer. Domain entities unchanged. Dependencies flow inward only. |

**Constitution gate: PASSED** — no violations detected.

### Post-Design Re-check

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable | ✅ PASS | `JobSummaryDto` fully typed. Delete audit event via `audit.appendEvent` (Stack A) and handler pattern (Stack B). |
| II | Layered Architecture | ✅ PASS | No cross-layer references in design. |
| III | Storage Abstraction | ✅ PASS | KV keys for cascade delete (`jobApplicationsKey`, `jobVersionsKey`) accessed through `StorageProvider`. |
| VI | UI Precision | ✅ PASS | Loading states and error toasts are specified. Dialog behavior is treated as a local UI choice rather than a constitution rule. |
| VII | YAGNI | ✅ PASS | No soft-delete, no undo, no recycle bin — only what's specified. |
| IX | Clean Architecture | ✅ PASS | `GetJobSummariesQuery` follows same CQRS pattern as existing queries. |

**Post-design gate: PASSED.**

## Project Structure

### Documentation (this feature)

```text
specs/003-delete-jobs-enhanced-cards/
├── plan.md              # This file
├── research.md          # Phase 0 output — research decisions R1–R10
├── data-model.md        # Phase 1 output — existing entities, new DTOs
├── quickstart.md        # Phase 1 output — verification steps
├── contracts/           # Phase 1 output — API contracts
│   ├── delete-job.md    # DELETE /api/jobs/{jobId}
│   └── get-jobs.md      # GET /api/jobs (enhanced response)
└── tasks.md             # Phase 2 output — implementation tasks
```

### Source Code (repository root)

```text
# Stack A — React / Express / TypeScript
server/
├── routes/
│   └── jobs.ts              # ADD: DELETE /:jobId route, MODIFY: GET / to include createdByName
├── middleware/
│   ├── auth.ts              # Existing: AuthenticatedRequest, User interface
│   └── rbac.ts              # Existing: requireRole('admin')
└── storage/
    ├── types.ts             # Existing: StorageProvider (get/set/delete/keys)
    ├── kv-keys.ts           # Existing: JOBS, jobApplicationsKey, jobVersionsKey
    └── kv-helpers.ts        # Existing: getArray, setArray

src/
├── types/
│   └── index.ts             # MODIFY: add createdByName to Job interface
├── lib/
│   ├── api.ts               # MODIFY: add deleteJob proxy
│   ├── api-real.ts          # MODIFY: add deleteJob implementation
│   └── api-mock.ts          # Existing: mock API (may need deleteJob stub)
└── components/
    ├── JobCard.tsx           # MODIFY: add onDelete prop, delete button, creator name, creation date
    ├── DashboardView.tsx     # MODIFY: add delete confirmation handler, admin check
    └── JobDetailView.tsx     # MODIFY: add delete button for admin, confirmation, redirect

tests/
└── unit/                    # Existing test patterns

# Stack B — .NET Blazor WASM / Clean Architecture
dotnet/
├── src/
│   ├── Domain/
│   │   ├── Entities/
│   │   │   └── Job.cs                         # Existing: unchanged
│   │   └── Interfaces/
│   │       └── IJobRepository.cs              # Existing: DeleteAsync already defined
│   ├── Application/
│   │   └── Jobs/
│   │       ├── Commands/
│   │       │   └── DeleteJobCommand.cs        # DONE: command + handler
│   │       └── Queries/
│   │           └── GetJobSummariesQuery.cs    # DONE: query + handler + DTO
│   ├── Infrastructure/
│   │   └── Persistence/
│   │       └── Repositories/
│   │           └── JobRepository.cs           # Existing: DeleteAsync implemented
│   ├── Web.Server/
│   │   └── Endpoints/
│   │       └── JobsEndpoints.cs               # DONE: DELETE /{jobId} route
│   └── Web.Client/
│       ├── Pages/
│       │   ├── Dashboard.razor                # DONE: delete button, creator name, date, progress
│       │   └── JobDetail.razor                # DONE: delete button, dialog integration
│       ├── Components/
│       │   └── ConfirmDeleteJobDialog.razor   # DONE: confirmation dialog
│       └── Services/
│           └── ApiClient.cs                   # DONE: DeleteJobAsync, JobSummaryDto
└── tests/
    └── Application.Tests/
        └── DeleteJobCommandTests.cs           # DONE: 3 tests (admin, not found, non-admin)
```

**Structure Decision**: Dual-stack web application. Stack B (.NET) implementation is **complete** — all commands, queries, endpoints, UI components, and tests are implemented. Stack A (React/Express) requires new backend route, API client methods, and UI component updates. No new projects or structural changes needed; all modifications fit within existing directories.

## Complexity Tracking

> No constitution violations detected. No complexity justification required.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| — | — | — |
