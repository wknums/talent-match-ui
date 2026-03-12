# Implementation Plan: Recruiter Analytics Dashboard

**Branch**: `005-recruiter-analytics` | **Date**: 2026-03-12 | **Spec**: `specs/005-recruiter-analytics/spec.md`
**Input**: Feature specification from `/specs/005-recruiter-analytics/spec.md`

## Summary

Add a recruiter analytics dashboard accessible to admin and recruiter roles that displays performance metrics (applications in queue, manual reviews, shortlist recommendations, active jobs, avg processing time) at individual-recruiter and department levels. The frontend UI already exists in Stack A with mock data; this plan covers building real server-side analytics endpoints for Stack A, implementing the full feature in Stack B (.NET Blazor), and adding server-side RBAC enforcement. Analytics are computed from existing job and application data in the store — no new data collection is needed.

## Technical Context

**Language/Version**: TypeScript (Node 20+, ES2022) for Stack A; C# / .NET 9+ for Stack B  
**Primary Dependencies**: Express 4, React 19, Vite 7, Shadcn/UI, Phosphor Icons (Stack A); ASP.NET Core, Blazor WASM, MediatR, EF Core (Stack B)  
**Storage**: JSON KV store via `StorageProvider` interface (local dev); Azure SQL (production, both stacks)  
**Testing**: Vitest (Stack A); xUnit + FluentAssertions (Stack B)  
**Target Platform**: Web browser (SPA) + Node.js server (Stack A); Blazor WASM + ASP.NET Core (Stack B)  
**Project Type**: Web application (dual-stack)  
**Performance Goals**: Analytics page load <2s (SC-001); client-side filter instant (SC-002); data freshness ≤30s (SC-003)  
**Constraints**: Server-side RBAC enforcement; recruiter scoped to own department; no new storage schema for analytics (compute from existing data)  
**Scale/Scope**: Admin + recruiter users across multiple departments; 2 new API endpoints per stack; 1 existing frontend component (Stack A); full new Blazor page (Stack B)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | Notes |
|---|-----------|--------|-------|
| I | Typed & Auditable | ✅ PASS | `RecruiterAnalytics` and `DepartmentAnalytics` types already defined in `src/types/index.ts`. Analytics endpoints are read-only queries — no state mutations requiring audit entries. |
| II | Layered Architecture | ✅ PASS | Stack A: routes in `server/routes/`, API client in `src/lib/api.ts`, UI in `src/components/`. Stack B: Domain → Application → Infrastructure → Presentation. |
| III | Storage Abstraction | ✅ PASS | Analytics computed from existing KV data via `StorageProvider.get()`. No direct storage imports. No new storage methods needed — aggregate from `jobs:all` and `jobs:{id}:applications` keys. |
| IV | Security Defaults | ✅ PASS | Plan adds server-side RBAC (`requireRole('admin', 'recruiter')`) to analytics endpoints. Recruiter scoped to own department. No secrets exposed. |
| V | LLM Integration | ✅ N/A | No LLM usage in analytics. |
| VI | UI Precision | ✅ PASS | Spec requires skeleton loaders (FR-008), empty states (FR-009), and ≤30s freshness. Existing component already implements loading/empty states. |
| VII | YAGNI | ✅ PASS | No new abstractions — analytics computed inline from existing data. No time-series, export, or WebSocket features (explicitly out of scope). |
| IX | Clean Architecture (.NET) | ✅ PASS | Stack B plan follows Domain entities → Application queries → Infrastructure data access → Presentation Blazor page. |

**GATE RESULT: ✅ ALL PASS — proceed to Phase 0.**

## Project Structure

### Documentation (this feature)

```text
specs/005-recruiter-analytics/
├── plan.md              # This file
├── research.md          # Phase 0: unknowns resolution
├── data-model.md        # Phase 1: entity definitions
├── quickstart.md        # Phase 1: developer quickstart
├── contracts/           # Phase 1: API contracts
│   ├── get-recruiter-analytics.md
│   └── get-department-analytics.md
└── tasks.md             # Phase 2 output (NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
# Stack A — React / Express / TypeScript
server/
├── routes/
│   └── stats.ts                 # MODIFY: add /recruiters and /departments sub-routes
src/
├── components/
│   └── AnalyticsView.tsx        # EXISTS: update for role-scoped stat cards
├── lib/
│   ├── api.ts                   # EXISTS: no changes (proxy delegates correctly)
│   └── api-real.ts              # MODIFY: implement getRecruiterAnalytics / getDepartmentAnalytics
└── types/
    └── index.ts                 # EXISTS: RecruiterAnalytics, DepartmentAnalytics already defined

tests/
├── unit/
│   └── analytics.test.ts       # NEW: analytics computation unit tests
└── integration/
    └── api.test.ts              # MODIFY: add analytics endpoint tests

# Stack B — .NET Blazor / Clean Architecture
dotnet/src/
├── Domain/
│   └── Entities/
│       ├── RecruiterAnalytics.cs    # NEW: domain entity
│       └── DepartmentAnalytics.cs   # NEW: domain entity
├── Application/
│   └── Analytics/
│       └── Queries/
│           ├── GetRecruiterAnalyticsQuery.cs   # NEW: CQRS query + handler
│           └── GetDepartmentAnalyticsQuery.cs  # NEW: CQRS query + handler
├── Infrastructure/
│   └── Persistence/                  # Data access for analytics aggregation
└── Web.Server/
    └── Endpoints/
        └── AnalyticsEndpoints.cs     # NEW: minimal API endpoints

dotnet/src/Web.Client/
└── Pages/
    └── Analytics.razor               # NEW: Blazor analytics page
```

**Structure Decision**: This feature extends the existing dual-stack web application structure. Stack A changes are minimal (backend endpoints + wiring real API client). Stack B requires full-layer implementation following Clean Architecture. No new projects or structural changes needed.

## Complexity Tracking

> No constitution violations to justify. All design decisions align with existing patterns.
