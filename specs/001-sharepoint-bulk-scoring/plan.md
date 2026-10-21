# Implementation Plan: Dynamic Delegated Microsoft 365 Bulk Scoring

**Branch**: `001-sharepoint-bulk-scoring` | **Date**: 2026-10-13 | **Spec**: [spec.md](./spec.md)  
**Input**: Feature specification from
`C:\code\awr-cv-match-client\specs\001-sharepoint-bulk-scoring\spec.md`  
**Status**: Planned target design until old-runtime removal, provider migrations, both live-stack
gates, parity, and release evidence pass.
**Contract Revision**: OpenAPI 3.1.0; the normative requirement/operation map is the
`x-requirement-coverage` plus `x-endpoint-map` contract metadata.

## Summary

Allow a recruiter to authorize one work/school SharePoint or OneDrive folder through a separate
multi-tenant Microsoft 365 authorization-code connection, discover and confirm bounded files, and
continue import, optional raster conversion, and scoring after the browser closes. Stack B
(.NET 10/Blazor/Clean Architecture) is implemented and proven first; only B-GATE may unlock the
Stack A (React/Express/TypeScript) runtime implementation. SQLite and Azure SQL behavior, public
contracts, source authorization, content handling, scoring outcomes, and security controls remain
identical across stacks.

The final Principle I blocker is closed by extending the existing immutable control-plane audit
contract to every request, item, duplicate, conversion, scoring-handoff, terminal, retry, count,
content-deletion, cleanup, and reconciliation mutation. Each closed event literal is emitted
atomically with its state mutation where a database transaction is available. Durable outbox or
reconciliation decisions are used at external content/queue boundaries. Every event carries a
correlation ID, a user actor for user actions or explicit worker/service actor for background work,
a UTC timestamp, immutable detached retention, and a versioned safe payload containing no token,
raw URL, source/item name, fingerprint, or item content. Stack B live, B-GATE, Stack A live,
A-GATE, PARITY-GATE, and RELEASE-GATE all require the complete matrix on SQLite and Azure SQL.

## Technical Context

**Language/Version**: Stack A TypeScript 5.7 on Node.js 20.19+ with React 19; Stack B C#/.NET 10
with Blazor WebAssembly and ASP.NET Core  
**Primary Dependencies**: Express 4, React 19, Vite 7, Zod, `better-sqlite3`, `mssql`, Azure
Identity/Blob SDKs; ASP.NET Core, EF Core, MSAL confidential client, Microsoft Graph, Azure Key
Vault, Azure Blob Storage  
**Storage**: SQLite and Azure SQL through stack-specific storage abstractions; Azure Blob Storage
or local/database fallback for staged content; append-only audit ledger detached from operational
cascades  
**Testing**: Vitest, Playwright, ESLint, TypeScript build; xUnit/.NET test, .NET Release build;
Terraform validation/policy assertions; shared contract, Graph, fingerprint, raster, disclosure,
audit, provider, live-stack, and parity fixtures  
**Target Platform**: Browser clients plus long-running Linux-hosted API/worker services in Azure;
local deterministic mock mode for development  
**Project Type**: Dual-stack web application with durable background worker and external Microsoft
365 integration  
**Performance Goals**: Confirmation acknowledgement and first authorized query of the returned
request each complete within the same 60-second timer; discovery steps include at most 1,000
eligible files; large review sets use stable server pagination  
**Constraints**: Delegated read-only Microsoft Graph access; no application-only fallback; no SAS;
bounded file/in-flight/request bytes; exact-folder authorization; phase-aware authorization lapse;
atomic state-and-audit persistence; safe immutable audit payloads; four-way Stack/provider parity  
**Scale/Scope**: Up to 100,000 files per request, 50 GiB default aggregate limit, resumable
multi-step discovery, bounded worker concurrency, two runtime stacks, and two relational providers

No `NEEDS CLARIFICATION` remains. Decisions and rejected alternatives are recorded in
[research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research and is re-evaluated after Phase 1 design.*

| Principle | Pre-design and post-design result |
| --- | --- |
| I. Typed & Auditable by Design | **PASS after this refresh.** Canonical TypeScript and .NET contracts use a closed event registry. The expanded matrix covers every feature-owned control-plane, request, item, duplicate, conversion, scoring-handoff, terminal, retry, count, content, cleanup, and reconciliation mutation. State and event commit atomically where applicable; external I/O uses a durable decision/outbox boundary. Required correlation, actor kind/ID, UTC time, immutable retention, safe payload, Stack A/B parity, and SQLite/Azure SQL parity are explicit in the model, OpenAPI, quickstart, and tasks. |
| II. Layered Architecture | **PASS.** Stack A routes remain thin over services/storage; Stack B preserves Domain ← Application ← Infrastructure/Presentation. |
| III. Storage Abstraction | **PASS.** SQLite/Azure SQL and content stores are accessed only through the existing/equivalent provider interfaces. Concrete providers never enter routes or components. |
| IV. Security Defaults | **PASS.** Product and source authorization remain separate; delegated credentials stay server-side and encrypted; source/file validation is server-authoritative; no secret or environment-specific value is committed; Blob access uses RBAC, never SAS. |
| V. LLM Integration Discipline | **PASS / unchanged.** Scoring continues through the existing server-side scoring/LLM boundary; source content never invokes a provider directly from either client. |
| VI. UI Precision & Responsiveness | **PASS.** Async states, paginated review, polling, accessible notifications, and WCAG evidence are planned in both stacks. |
| VII. Simplicity & YAGNI | **PASS.** The existing audit ledger is extended rather than adding a new event store. Durable outbox/reconciliation is used only where an external queue/content operation cannot share the database transaction. |
| VIII. System Communication Architecture | **PASS.** Separate multi-tenant authorization code + PKCE, `/organizations`, exact-folder delegated Graph access, encrypted scoped caches, phase-aware lapse, and no app-only/fallback/allow-list path are preserved. |
| IX. Clean Architecture | **PASS.** Stack B event literals/domain transitions live in Domain, orchestration and audit commands in Application, provider persistence/outbox/content adapters in Infrastructure, and HTTP/UI projections in Web. |

### Principle I atomicity and actor rules

- A user endpoint mutation records `actorType=user` and the authenticated product actor ID.
- A worker, timer, cleanup, or reconciliation mutation records `actorType=service` and a stable
  non-secret service actor ID. `initiatedByActorId` may retain the initiating product user where
  relevant but never replaces the actual background actor.
- A relational state mutation and its audit row commit in the same SQLite/Azure SQL transaction.
  A failed audit insert rolls back the mutation.
- Queue publication commits a durable handoff/outbox state plus audit event before delivery.
- External content deletion/quarantine commits a durable intent/outcome or reconciliation
  correction plus audit event; retries remain idempotent and observable.
- Audit rows are append-only, excluded from job/request/content cascades, and expose no mutation or
  deletion product API.
- Safe facts are versioned enums, opaque subject references, counts/bytes, reason codes, and
  configuration versions only. Tokens, codes, verifiers, caches, keys, raw URLs, source/folder/item
  names, source identities/emails, fingerprints, item content, provider payloads/errors, and
  environment identifiers are prohibited.

### Gate evaluation

There are no justified violations and no unresolved clarification. Any missing event row, actor,
correlation ID, timestamp, atomic rollback, append-only/survival assertion, payload-safety
assertion, or Stack/provider result is a gate failure. The complete expanded matrix is mandatory at:

1. story-level Stack B and Stack A gates for the rows each story owns;
2. distinct `STACK-B-LIVE-GATE` and `STACK-A-LIVE-GATE`;
3. aggregate `B-GATE` and `A-GATE`;
4. four-way `PARITY-GATE`; and
5. `RELEASE-GATE`.

## Phase 0: Research

[research.md](./research.md) retains all earlier decisions and adds the expanded Principle I
decision. The selected approach is:

1. reuse the existing detached immutable ledger and closed typed envelope;
2. add exhaustive event literals for feature-owned lifecycle mutations;
3. use user actors for user actions and explicit service actors for background actions;
4. enforce same-transaction emission for relational state and durable outbox/reconciliation
   emission for external boundaries;
5. prohibit secret, raw source, filename, fingerprint, and content facts structurally;
6. replay the same fixtures through Stack A/Stack B and SQLite/Azure SQL; and
7. block both live gates, both aggregate stack gates, parity, and release on the full matrix.

No technology or integration unknown remains.

## Phase 1: Design and Contracts

### Data model

[data-model.md](./data-model.md) remains authoritative for the seven feature tables and the
existing detached audit ledger. Its expanded audit matrix includes the preserved control-plane
events plus:

- discovery-created item, item claim, import start, and source-metadata recheck outcomes;
- original/converted content staging and fingerprint recording;
- duplicate claim and duplicate skip;
- conversion start, complete, failure, quarantine, and removal;
- application/document creation and scoring handoff queued;
- scoring started only where this feature owns that transition;
- item completed, failed, skipped, and cancelled;
- retry item creation;
- request count reconciliation and terminal outcome finalization;
- original, converted, and transient staged-content deletion; and
- item/request/content cleanup and reconciliation correction outcomes.

If one transaction makes more than one of these typed transitions, every applicable event is
written in that transaction; one broad event may not substitute for the required rows.

### Interface contract

[contracts/openapi.yaml](./contracts/openapi.yaml) remains OpenAPI 3.1.0. Public endpoint shapes
and all existing decisions are preserved. The vendor extension `x-immutable-audit-contract`
normatively freezes event literals, actor semantics, payload restrictions, provider parity,
atomicity, and required gates. Audit mutation/deletion endpoints remain intentionally absent.

### Validation quickstart

[quickstart.md](./quickstart.md) provides deterministic and live procedures. Its Principle I
section must execute each matrix row, prove rollback on audit-write failure, reject mutation or
deletion of ledger rows, verify retention after job/content cleanup, scan payloads for prohibited
data, and compare identical safe facts across all four Stack/provider combinations.

### Agent context

Run from the repository root after the artifacts are updated:

```bash
bash .specify/scripts/bash/update-agent-context.sh copilot
```

Only technology facts from this plan may be added; manual content between managed markers must be
preserved.

## Project Structure

### Documentation

```text
specs/001-sharepoint-bulk-scoring/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── tasks.md
├── contracts/
│   └── openapi.yaml
└── validation/
    ├── contract-freeze-v3.md
    ├── stack-b-*.md
    ├── stack-a-*.md
    ├── audit-contract-parity-v3.md
    ├── parity-gate-v3.md
    └── release-gate-v3.md
```

### Source code

```text
# Stack A
src/
├── components/
├── lib/
└── types/index.ts
server/
├── routes/
├── services/
└── storage/
tests/
├── fixtures/bulk-scoring-requests/
├── integration/
├── security/
├── performance/
└── e2e/

# Stack B
dotnet/src/
├── Domain/
├── Application/
├── Infrastructure/
├── Web.Server/
└── Web.Client/
dotnet/tests/
├── Domain.Tests/
├── Application.Tests/
├── Infrastructure.Tests/
└── Web.Tests/

# Deployment
infra/
├── terraform/
└── scripts/
```

**Structure Decision**: Preserve the existing dual-stack layout. Stack B is the schema and initial
behavior authority and must pass B-GATE before Stack A old-mode removal or runtime implementation.
Shared fixtures and the OpenAPI contract are the cross-stack source of parity truth.

## Phase 2: Task Planning

[tasks.md](./tasks.md) is the executable dependency plan. Existing IDs and implementation order are
preserved. The refresh expands the existing red-first, implementation, live-evidence, stack-gate,
parity, and release tasks rather than invalidating prior task references. A task is not green when
it checks only the former control-plane subset.

Required evidence progression:

```text
contract/schema red
  -> Stack B provider/domain/worker red
  -> Stack B implementation
  -> Stack B complete-matrix automated evidence
  -> STACK-B-LIVE-GATE
  -> B-GATE
  -> Stack A provider/worker red
  -> Stack A implementation
  -> Stack A complete-matrix automated evidence
  -> STACK-A-LIVE-GATE
  -> A-GATE
  -> red-first four-way parity replay
  -> PARITY-GATE
  -> RELEASE-GATE
```

## Post-design Constitution Re-check

**PASS.** The earlier control-plane-only audit matrix was the sole remaining constitutional
blocker. The refreshed research, model, contract, quickstart, and tasks now cover every
feature-owned lifecycle mutation with typed, atomic, correlated, actor-attributed, timestamped,
immutable, non-sensitive, four-way-parity evidence. No other architecture, authorization,
retention, conversion, duplicate, notification, performance, infrastructure, or rollout decision
changed.

## Complexity Tracking

No constitutional violation requires justification.
