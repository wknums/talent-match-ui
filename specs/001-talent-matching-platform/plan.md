# Implementation Plan: Manual Review AI Evidence Prepopulation

**Branch**: `001-talent-matching-platform` | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/001-talent-matching-platform/spec.md`

## Summary

Restore and spec-harden the manual review AI prepopulation feature. When a recruiter opens manual
review for the first time (no saved review), each rubric category MUST show both the AI-assigned
score (averaged across scoring runs) AND the matching evidence snippets (direct CV quotes) in its
notes field — per FR-014, US7 scenario 6. This was previously working but regressed. The root
cause is in Stack B's evidence extraction pipeline: `ParseSingleRun` does not reliably capture
evidence into `EvidenceCitationsJson`, so `PrePopulateFromAiAsync` has nothing to surface. Stack A
(`buildStackBManualReviewPrepopulation` + `collectEvidenceByRubricCategory`) is the reference
implementation and is functionally correct.

## Technical Context

**Language/Version**: TypeScript 5.x (Stack A — Node.js/React), .NET 9 / C# 13 (Stack B — Blazor WASM)  
**Primary Dependencies**: React 18, Vite, Express (Stack A); ASP.NET Core 9, Blazor WASM, MediatR, EF Core (Stack B)  
**Storage**: Azure SQL via StorageProvider abstraction; SQLite for local dev; `ScoringRun.EvidenceCitationsJson` TEXT column  
**Testing**: Vitest (Stack A unit + integration); xUnit + FluentAssertions (Stack B)  
**Target Platform**: Web browser (both stacks); Stack B is Blazor WASM (client-side execution)  
**Project Type**: Web application — dual-stack parity implementation  
**Performance Goals**: Manual review page load < 3 s including prepopulation; no additional API calls beyond existing scoring run fetch  
**Constraints**: Prepopulation MUST NOT overwrite a previously saved review; evidence matching uses fuzzy category-name matching (≥40% word overlap); no schema migration required  
**Scale/Scope**: Per-application scoped operation; N scoring runs (default 3) per application; M rubric categories per job (typically 4–8)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Status | Notes |
|------|--------|-------|
| **I — Typed & Auditable** | ✅ PASS | `EvidenceCitation` already typed in `src/types/index.ts`; no new audit-trail changes; all changes are within existing typed structures |
| **II — Layered Architecture (Stack A)** | ✅ PASS | Evidence extraction stays in `server/` (backend); `collectEvidenceByRubricCategory` stays in `src/lib/`; components only consume prepopulation result |
| **II — Layered Architecture (Stack B)** | ✅ PASS | `ParseSingleRun` fix is in Application layer (`ScoreApplicationCommand`); UI display in Presentation (`ManualReview.razor`); no cross-layer dependency added |
| **III — Storage Abstraction** | ✅ PASS | No schema change; `EvidenceCitationsJson` column already exists; StorageProvider interface unchanged |
| **IV — Security** | ✅ PASS | Evidence snippets are CV content; no PII change; no new external calls |
| **V — LLM Integration** | ✅ PASS | No LLM call path changed; only parsing of existing LLM response payloads |
| **VI — UI Precision** | ✅ PASS | Banner and mismatch warning are already partially implemented; hardening to spec |
| **VII — Simplicity/YAGNI** | ✅ PASS | Fixing a regression + closing a spec gap; no new abstraction layers |
| **IX — Clean Architecture (.NET)** | ✅ PASS | Fix is in Application layer; Presentation layer reads DTO; no inner→outer dependency introduced |

**Post-Phase 1 Re-check**: ✅ PASS — design artifacts (research, data model, contracts, quickstart)
remain compliant with all gates above.

## Project Structure

### Documentation (this feature)

```text
specs/001-talent-matching-platform/
├── plan.md              # This file
├── research.md          # Phase 0 — root cause analysis and decision log
├── data-model.md        # Phase 1 — evidence citation schema (existing, verify)
├── quickstart.md        # Phase 1 — how to reproduce and verify the regression
├── contracts/
│   ├── scoring-passthrough.md          # existing scoring contract (evidence required)
│   └── manual-review-prepopulation.md  # new behavior contract for US7/FR-014/FR-026
└── tasks.md             # Phase 2 (/speckit.tasks — NOT created here)
```

### Source Code (affected files)

```text
# Stack B — .NET / Blazor WASM
dotnet/src/Application/Scoring/Commands/
└── ScoreApplicationCommand.cs        # ParseSingleRun() — evidence extraction fix

dotnet/src/Web.Client/Pages/
└── ManualReview.razor                # PrePopulateFromAiAsync() — evidence display fix
                                      # aiScoringMismatch warning UI hardening

dotnet/tests/Application.Tests/       # NEW: ParseSingleRun evidence extraction unit tests
dotnet/tests/Domain.Tests/            # existing — no change

# Stack A — React / TypeScript
src/lib/
└── stackb-scoring.ts                 # collectEvidenceByRubricCategory() — verify & harden

src/components/
└── ManualReviewView.tsx              # Banner + mismatch warning — verify against spec

tests/unit/
└── stackb-scoring.test.ts            # NEW or extend: evidence collection + prepopulation tests
```

**Structure Decision**: Both stacks use Option 2 (web application). Affected files are localised —
no new directories. Stack B Application layer test project (`dotnet/tests/Application.Tests/`)
may need to be created if it does not exist; otherwise new test files go there.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

No violations. No entry required.
