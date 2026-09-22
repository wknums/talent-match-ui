# Implementation Plan: Configurable Rubric Generation and Editing

**Branch**: `001-dynamic-rubric-editor` | **Date**: 2026-09-09 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-dynamic-rubric-editor/spec.md`

## Summary

Replace the duplicated hardcoded job-extraction prompts with a shared, database-backed instruction lifecycle that administrators can draft, test, activate, and roll back. Server-side extraction services in both stacks will combine the active editable instruction body with an application-owned protected JSON contract, call the existing AWReason passthrough endpoint, validate and normalize the result, persist extraction diagnostics, and return a versioned itemized rubric. Rubric configuration moves from a legacy category array with free-text descriptions to a versioned envelope containing ordered categories and individually identified requirement items; both React and Blazor editors will support pointer drag/drop plus explicit move controls for keyboard and touch users.

## Technical Context

**Language/Version**: TypeScript 5.7 on Node.js 20+ with React 19; C# on .NET 10 with Blazor WebAssembly 10
**Primary Dependencies**: Express 4, Zod 3, React 19, Radix/shadcn primitives, Vitest 4, Testing Library, Playwright 1.58; ASP.NET Core 10, MediatR 12, FluentValidation 12, EF Core 10, Blazor WebAssembly, xUnit, bUnit
**Storage**: Shared SQLite for local development and Azure SQL for cloud; Stack A storage-provider repositories and Stack B EF Core mappings/migrations operate on the same schema
**Testing**: Vitest integration/unit tests and Testing Library/Playwright accessibility tests; xUnit application/infrastructure/web tests and bUnit component tests
**Target Platform**: Azure App Service APIs and modern desktop, tablet, and mobile browsers hosting React or Blazor WebAssembly clients
**Project Type**: Dual-stack web application with a shared data model, external AWReason integration, and equivalent API contracts
**Performance Goals**: Prompt administration reads complete within 2 seconds under normal load; drag/drop feedback remains responsive within one animation frame; item moves save within 2 seconds; extraction adds no more than 10% client-side overhead beyond AWReason processing
**Constraints**: LLM calls remain server-side; the protected contract is not administrator-editable; one active instruction version at a time; every mutation is correlated and audited; no silent parser fallback; legacy rubrics remain readable; category weights remain independent from item count; no new drag/drop package is required
**Scale/Scope**: Two API/client stacks, shared SQL schema, one global extraction-instruction lifecycle, versioned extraction records, job create/edit flows, scoring prompt generation/read-only consumers, and legacy rubric conversion

## Constitution Check

*GATE: Passed before Phase 0 research and re-checked after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Typed & Auditable by Design | PASS | Instruction versions, extraction records, rubric envelopes, items, validation findings, concurrency versions, and API DTOs are typed. Draft, validation, activation, rollback, conversion, and config-save mutations emit correlated immutable audit events. |
| II. Layered Architecture | PASS | Stack A routes delegate prompt composition, validation, and persistence to services/repositories; clients use `src/lib/api.ts`. Stack B keeps entities/interfaces in Domain, use cases in Application, persistence/AWReason adapters in Infrastructure, and endpoints/components in Web projects. |
| III. Storage Abstraction | PASS | Shared schema changes are implemented through Stack A `StorageProvider` repositories and Stack B repository interfaces/EF Core mappings. No route or UI imports a concrete provider. |
| IV. Security Defaults | PASS | Administration endpoints require server-side Admin authorization; uploaded sample/spec files retain existing type/size validation; prompt text and raw model output are not logged as audit payloads. |
| V. LLM Integration Discipline | PASS | Browsers never call AWReason. Servers append the protected JSON contract, request structured JSON through the existing passthrough integration, strictly validate the raw response, and fail visibly instead of accepting malformed output. |
| VI. UI Precision & Responsiveness | PASS | Both editors include loading, empty, drag target, keyboard/touch move, validation, conflict, save failure, and reduced-motion states with WCAG-compliant focus and contrast. |
| VII. Simplicity & YAGNI | PASS | The design reuses the existing prompt lifecycle, audit, job-config versioning, and multipart AWReason patterns. Native drag/drop plus explicit move controls avoids a new cross-stack dependency. |
| IX. Clean Architecture (.NET) | PASS | Prompt composition and validation are application use cases behind interfaces; EF Core and AWReason HTTP implementations stay in Infrastructure; Web endpoints only dispatch typed commands/queries. |
| Dual-stack/shared model | PASS | Both stacks use the same tables, JSON schemas, state transitions, authorization rules, and acceptance fixtures. |

Gate result: PASS. No constitution exception is required.

## Phase 0: Research Outcomes

Research is complete in [research.md](research.md). Controlling decisions:

1. Separate the editable instruction body from a versioned protected response contract.
2. Store extraction instructions as immutable global versions with one active version and optimistic concurrency.
3. Persist every actual or validation extraction as an extraction record linked to the exact instruction and contract versions.
4. Replace category descriptions as the requirement container with a schema-versioned rubric envelope containing ordered categories and ordered individual items.
5. Preserve legacy rubric arrays through a read adapter and an explicit, reviewable conversion action; do not silently split legacy prose on load.
6. Use stable item/category identifiers so drag/drop changes assignment/order without changing wording or traceability.
7. Use native pointer drag/drop for desktop and explicit move/reorder controls for keyboard and touch parity.
8. Reuse existing admin authorization, audit events, multipart upload, job-config versioning, and prompt-management UX patterns without reusing scoring-prompt persistence.
9. Keep category-level scoring output unchanged: itemization enriches the rubric definition while downstream scoring still keys category scores by category name.
10. Treat contract validation failures, stale writes, and unassigned requirements as explicit review states; never coerce them into success-shaped responses.

All technical unknowns are resolved.

## Phase 1: Design and Contracts

### Shared Data Design

- Add immutable `ExtractionInstructionVersion` rows with draft/active/retired lifecycle, protected-contract version, validation result, author/activation metadata, and optimistic version.
- Add `JobSpecExtraction` rows for actual and administrator test runs, preserving raw output, normalized output, validation findings, source-file metadata/hash, actor, timing, and optional job/config linkage.
- Extend job configuration metadata with extraction ID and instruction version ID.
- Store new rubrics in a `rubric-v2` JSON envelope with ordered `categories[]` and `items[]`; each item has one category assignment, order, requirement type, source trace, and review status.
- Continue reading legacy category arrays. Conversion creates a new job-config version and preserves the original version unchanged.
- Enforce one active instruction version and unique version numbers transactionally in both database engines.

See [data-model.md](data-model.md) for fields, invariants, transitions, and migration rules.

### API and JSON Contracts

- [contracts/extraction-rubric.schema.json](contracts/extraction-rubric.schema.json) is the canonical protected AWReason output contract.
- [contracts/rubric-config.schema.json](contracts/rubric-config.schema.json) defines the persisted/editable `rubric-v2` envelope.
- [contracts/extraction-instructions.openapi.yaml](contracts/extraction-instructions.openapi.yaml) defines equivalent Stack A/Stack B administration, validation, extraction, conversion, and config-save endpoints.
- Update `INTEGRATION.md` during implementation because API shapes change.

### Stack A Implementation Shape

```text
server/
├── routes/
│   ├── jobs.ts                         # thin extraction/config/conversion endpoints
│   └── extraction-instructions.ts      # Admin-only lifecycle endpoints
├── services/
│   ├── job-spec-extraction.ts          # compose, call, validate, normalize, persist
│   ├── extraction-contract.ts          # protected schema/version and validators
│   └── rubric-conversion.ts            # legacy adapter and explicit conversion
└── storage/
    ├── types.ts                        # provider contracts
    ├── schema.sql
    ├── schema-sqlite.sql
    ├── schema-pre-batch-upgrades.sql
    ├── db.ts                           # guarded local reconciliation
    └── repos/
        ├── extraction-instruction-repo.ts
        ├── job-spec-extraction-repo.ts
        └── job-repo.ts

src/
├── types/index.ts
├── lib/
│   ├── api.ts
│   ├── api-real.ts
│   └── api-mock.ts
└── components/
    ├── CreateJobDialog.tsx
    ├── RubricEditor.tsx
    ├── RubricItemCard.tsx
    └── ExtractionInstructionAdmin.tsx

tests/
├── integration/
│   ├── extraction-instructions.test.ts
│   ├── job-spec-extraction.test.ts
│   └── rubric-schema-migration.test.ts
└── unit/
    ├── extraction-contract.test.ts
    ├── rubric-conversion.test.ts
    └── rubric-editor.test.tsx
```

### Stack B Implementation Shape

```text
dotnet/src/
├── Domain/
│   ├── Entities/
│   │   ├── ExtractionInstructionVersion.cs
│   │   └── JobSpecExtraction.cs
│   └── Interfaces/
│       ├── IExtractionInstructionRepository.cs
│       └── IJobSpecExtractionRepository.cs
├── Application/
│   ├── ExtractionInstructions/
│   │   ├── Commands/
│   │   ├── Queries/
│   │   └── Models/
│   ├── JobExtraction/
│   │   ├── Commands/
│   │   └── Services/
│   └── Rubrics/
│       ├── Models/
│       └── Services/
├── Infrastructure/
│   ├── Persistence/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   └── Services/
│       └── AwrJobSpecExtractionService.cs
├── Web.Server/Endpoints/
│   ├── ExtractionInstructionEndpoints.cs
│   └── JobsEndpoints.cs
└── Web.Client/
    ├── Services/ApiClient.cs
    └── Components/
        ├── CreateJobDialog.razor
        ├── RubricEditor.razor
        ├── RubricItemCard.razor
        └── ExtractionInstructionAdmin.razor

dotnet/tests/
├── Application.Tests/
├── Infrastructure.Tests/
└── Web.Tests/
```

**Structure Decision**: Keep the repository's dual-stack layout and shared database. Add feature-focused services/components rather than continuing to expand the already-large job endpoint and creation-dialog files. The contracts under this feature are the parity source of truth.

### Implementation Sequence

1. Add shared contract fixtures and types, then implement strict parser/normalizer tests in both stacks.
2. Add shared SQL/SQLite schema, Stack A repository interfaces/implementations, and Stack B entities/repositories/EF migration.
3. Seed the current built-in instruction as immutable version 1 only when no instruction versions exist.
4. Implement server-side prompt composition and extraction records; migrate `/extract-spec` consumers to the new response while retaining `/extract-rubric` compatibility.
5. Add Admin lifecycle endpoints and UIs with draft validation, activation, rollback, concurrency, and audit events.
6. Add `rubric-v2` read/write adapters and update scoring-prompt generation, application detail, and manual-review consumers to read both schemas.
7. Implement explicit legacy conversion and the accessible rubric editors in Stack A and Stack B.
8. Run shared contract fixtures, targeted tests, full builds, and parity acceptance tests.

## Complexity Tracking

No constitution violations require exception tracking. Two new persisted entities are required by current audit/versioning requirements and cannot be safely represented by the job-scoring prompt tables because their lifecycle, scope, and payload are different.

## Phase 1 Design Outputs

- Research decisions: [research.md](research.md)
- Data model: [data-model.md](data-model.md)
- Protected extraction contract: [contracts/extraction-rubric.schema.json](contracts/extraction-rubric.schema.json)
- Rubric configuration contract: [contracts/rubric-config.schema.json](contracts/rubric-config.schema.json)
- API contract: [contracts/extraction-instructions.openapi.yaml](contracts/extraction-instructions.openapi.yaml)
- Verification runbook: [quickstart.md](quickstart.md)

## Post-Design Constitution Re-Check

| Principle | Status | Post-Design Notes |
|-----------|--------|-------------------|
| I. Typed & Auditable | PASS | Data model includes stable IDs, immutable versions, correlation-aware events, extraction evidence, and optimistic concurrency. |
| II. Layered Architecture | PASS | Contract parsing, prompt composition, migration, persistence, endpoints, and presentation have explicit layer ownership in both stacks. |
| III. Storage Abstraction | PASS | Equivalent schema and repository boundaries are defined for SQLite and Azure SQL. |
| IV. Security Defaults | PASS | Admin-only mutations and safe upload/logging constraints are present in API and quickstart contracts. |
| V. LLM Integration Discipline | PASS | The protected contract is composed only on the server and every response is validated before it reaches job state. |
| VI. UI Precision & Responsiveness | PASS | Accessible interaction alternatives and failure rollback behavior are contractually required and testable. |
| VII. Simplicity & YAGNI | PASS | No new state framework, drag/drop dependency, or external service is introduced. |
| IX. Clean Architecture | PASS | .NET interfaces and use cases point inward; EF and HTTP remain Infrastructure concerns. |

Post-design gate result: PASS.

## Follow-up Design: Scoring Prompt Governance and Precision (2026-09-17)

The scoring prompt lifecycle is extended without replacing the existing extraction lifecycle:

1. Add immutable `PromptGenerationInstruction` versions scoped either globally or to one job. Generation resolves the active job version first and falls back to the active global version.
2. Treat every manual edit of final scoring prompt text as a new `ScoringPrompt` draft version.
3. Snapshot the configured AWReason model identifier and reasoning level on prompt versions, prompt tests, approvals, and scoring runs.
4. Reject testing, production approval, queue admission, and production scoring when the selected prompt was created for a different profile. A profile-only change therefore requires a new prompt version and new test evidence.
5. Keep schema changes additive so the previously deployed Stack B application can continue operating during a rolling deployment, while only the new application version enforces profile compatibility.
6. Preserve candidate scores to three decimal places through parsing, aggregation, API projection, display, and numeric sorting.
7. Reuse the existing prompt-management and extraction-administration surfaces rather than introduce another administration framework.
