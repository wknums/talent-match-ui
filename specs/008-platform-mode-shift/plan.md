# Implementation Plan: 008 Platform-Mode Orchestration Shift

**Branch**: `008-platform-mode-shift` | **Date**: 2026-05-29 | **Spec**: `spec.md`
**Input**: Feature specification from `/specs/008-platform-mode-shift/spec.md`

## Summary

Revise platform-mode architecture so the client is a submit/reconcile layer over platform orchestration while preserving SQL as the structured system of record. Keep `STORAGE_PROVIDER` semantics (`local|azuresql`), enforce blob-by-reference in platform mode, and guarantee cross-mode read compatibility via per-row canonical byte source (`BlobUri` first, DB fallback).

## Technical Context

**Language/Version**:
- Stack A: TypeScript 5.x, Node.js 20+
- Stack B: C# on .NET 10
- Infrastructure: Terraform HCL

**Primary Dependencies**:
- Stack A: Express, `mssql`, SQLite, `@azure/identity`, `@azure/storage-blob`
- Stack B: ASP.NET Core, MediatR, EF Core, `Azure.Identity`, `Azure.Storage.Blobs`

**Storage**:
- Shared SQL schema (`talentmatch`) in Azure SQL (cloud) or shared SQLite (local)
- Azure Blob container `cv-uploads` for platform-mode document bytes by reference

**Testing**:
- TypeScript: Vitest
- .NET: `dotnet test`
- Integration: deployment and runtime verification against health, batch status, and reconciliation outcomes

**Target Platform**:
- Azure App Service (Linux) for Stack A and Stack B
- Local developer environment with SQLite and optional Azure resources

**Project Type**:
- Dual-stack web application with shared data model and infrastructure-as-code

**Performance Goals**:
- Reconciler tick and polling cadence remain bounded (`AWR_PLATFORM_RECONCILE_INTERVAL_MS` default 15000)
- Avoid request explosion by batching platform submissions (`AWR_PLATFORM_BATCH_SIZE`, default 2)
- Preserve resilient progress on long-running jobs through lease-based reconciliation

**Constraints**:
- `STORAGE_PROVIDER` must remain `local|azuresql` only (constitution + spec)
- No SAS tokens; managed identity + RBAC only
- New writes store document bytes in exactly one canonical location per row
- Sequential mode behavior and API compatibility must remain intact

**Scale/Scope**:
- Cross-stack feature parity touching Stack A (`server/`), Stack B (`dotnet/src/`), and Terraform/app settings
- SQL schema additions for batches/progress and document metadata (`BlobUri`, `ContentSha256`)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Typed & Auditable by Design | PASS | Batch/progress entities and transitions remain typed; submission/poll/cancel events are auditable. |
| II. Layered Architecture | PASS | Route handlers remain thin; orchestration belongs in services/workers (Stack A) and hosted services/application handlers (Stack B). |
| III. Storage Abstraction | PASS | SQL access continues via repository/provider abstractions; storage provider semantics unchanged. |
| IV. Security Defaults | PASS | Blob access uses RBAC with managed identity; no SAS tokens introduced. |
| V. LLM Integration Discipline | PASS | No direct frontend LLM calls introduced; platform mode only changes scoring transport/orchestration. |
| VII. Simplicity & YAGNI | PASS | Reuses existing mode switch and extends current schema/services rather than adding new provider modes. |
| IX. Clean Architecture (.NET) | PASS | Blob and platform clients implemented in Infrastructure; use cases and contracts stay in Application/Domain layers. |

Gate result: PASS.

## Phase 0: Research Outcomes

See `research.md` for full detail. All technical-context clarifications are resolved:

1. Canonical document byte source is determined per row by `BlobUri` presence.
2. Platform mode preconditions are explicit (`STORAGE_PROVIDER=azuresql` + blob account).
3. Cross-mode reads are Blob-first with DB fallback.
4. Batch lifecycle and reconciliation contracts are polling-based for MVP.
5. No additional storage provider value is introduced.

### Clarification Decisions (2026-05-29)

1. Mixed terminal semantics: batch transitions to `completed` when all CV entries are terminal, with per-CV outcomes persisted.
2. Retry model: failed CV entries may be moved into a new retry batch using a new `batchId`/`Idempotency-Key`; source batch remains immutable.
3. Idempotency retention: dedupe window remains active through batch terminal state plus 30 days.
4. Observability scope: structured logs + metrics/alerts + per-CV decision trace retention for audit period.

## Project Structure

### Documentation (this feature)

```text
specs/008-platform-mode-shift/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── platform-batch-api.md
├── platform-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
server/
├── services/
│   ├── pipeline.ts
│   ├── platform-submitter.ts
│   ├── blob-store.ts
│   └── awr-timeout.ts
├── workers/
│   └── reconciler.ts
├── routes/
│   └── jobs.ts
└── storage/
    ├── schema.sql
    ├── schema-sqlite.sql
    └── repos/
        ├── application-repo.ts
        └── scoring-batch-repo.ts

dotnet/src/
├── Application/
│   ├── Common/Interfaces/
│   └── Jobs/Commands/
├── Domain/
│   ├── Entities/
│   └── Interfaces/
├── Infrastructure/
│   ├── Services/
│   ├── HostedServices/
│   └── Persistence/
└── Web.Server/Endpoints/

infra/
└── terraform/
```

**Structure Decision**:
Use the existing dual-stack architecture with targeted additions in services, repositories, hosted reconciliation, and schema mappings. Keep contracts centralized under this feature and reference `platform-contract.md` as the canonical wire contract source.

## Complexity Tracking

No constitution violations requiring exception tracking.

## Phase 1 Design Outputs

- Data model: `data-model.md`
- Interface contract artifact: `contracts/platform-batch-api.md`
- Verification runbook: `quickstart.md`

## Post-Design Constitution Re-Check

| Principle | Status | Post-Design Notes |
|-----------|--------|-------------------|
| I. Typed & Auditable | PASS | Entity fields and state transitions are explicitly documented in data model. |
| II. Layered Architecture | PASS | Contract and quickstart keep API, service, and worker boundaries intact. |
| III. Storage Abstraction | PASS | Byte-source policy is per-row semantics, not provider-model expansion. |
| IV. Security Defaults | PASS | RBAC-only blob access remains mandatory in design and quickstart checks. |
| IX. Clean Architecture | PASS | Stack B contract maps to Application interfaces and Infrastructure implementations only. |

Post-design gate result: PASS.
