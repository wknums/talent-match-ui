# Implementation Plan: Fix Stack B Azure SQL Bootstrap and Preserve Cross-Stack AWR Parity

**Branch**: `007-fix-stackb-sql-endpoint` | **Date**: 2026-06-01 | **Spec**: `spec.md`
**Input**: Feature specification from `/specs/007-fix-stackb-sql-endpoint/spec.md`

## Summary

Fix Stack B Azure deployment blockers and enforce cross-stack parity expectations with minimal, targeted changes:
1. Replace EF Core raw-SQL execution in Azure SQL schema bootstrap with ADO.NET `DbCommand.ExecuteNonQuery()` so SQL batches containing literal curly braces (for example `DEFAULT '{}'`) execute verbatim.
2. Add conditional Terraform wiring for `AWR_SEQ_API_ENDPOINT` in Stack B live infrastructure, mirroring Stack A behavior via `extra_app_settings` only when non-empty.
3. Validate cross-stack parity outcomes for `AWR_SEQ_API_ENDPOINT` app-setting inclusion semantics and `awrApi` health-status semantics (reachable, unreachable, not-configured), without changing Stack A implementation.

This keeps existing schema SQL, retry scaffolding, deployment scripts, and module contracts unchanged while treating Stack A as the parity baseline.

## Technical Context

**Language/Version**:
- C# (.NET Web API host in `dotnet/src/Web.Server/`)
- Terraform (HCL)
- Bash deployment scripts

**Primary Dependencies**:
- EF Core `DatabaseFacade.GetDbConnection()` for connection reuse
- ADO.NET `DbConnection.CreateCommand()` + `ExecuteNonQuery()` for literal DDL execution
- Terraform Stack B live root + pre-existing module `extra_app_settings` merge

**Storage**:
- Azure SQL schema bootstrap from `server/storage/schema.sql` (read-only in this feature)
- Azure App Service app settings populated by Terraform

**Testing**:
- Infrastructure validation: script-driven plan/apply via `infra/scripts/deploy.sh ... stack-b`
- Infrastructure parity validation: compare script-driven plan behavior for Stack A and Stack B using the same `AWR_SEQ_API_ENDPOINT` inputs
- Runtime validation: Stack B health endpoint (`awrApi` dependency), Stack A no-regression checks, and cross-stack health-semantics comparison for reachable/unreachable/not-configured endpoint states
- Data validation: Azure SQL table count checks
- Build sanity: Stack B package/build command from existing runbook

**Target Platform**:
- Stack B on Azure App Service
- Stack A on Azure App Service (validation baseline only)
- Azure SQL Database
- Terraform-managed infrastructure

**Project Type**:
- Existing dual-stack web application; this feature edits Stack B runtime bootstrap and Stack B Terraform live root only, and adds parity validation against Stack A behavior

**Performance Goals**:
- No regression to startup retry behavior
- Full schema creation completion on first successful bootstrap run
- No additional runtime overhead beyond command execution method swap

**Constraints**:
- Preserve `Task.Run` + retry structure in `Program.cs` (FR-004)
- Do not modify `server/storage/schema.sql` content (FR-005)
- Do not modify `.env_qa` or `infra/scripts/lib/common.sh` (FR-009)

- Enforce FR-011 parity for `AWR_SEQ_API_ENDPOINT` app-setting include/omit behavior across Stack A and Stack B
- Enforce FR-012: Stack A runtime behavior remains unchanged
- Enforce FR-013/NFR-004 parity for `awrApi` health semantics across configured-reachable, configured-unreachable, and not-configured states

**Scale/Scope**:
- Code scope: Stack B server bootstrap + Stack B live Terraform root
- Infrastructure scope: one additional conditional app-setting map path
- Verification scope: rebuild/deploy/apply/health + schema completeness checks, plus cross-stack parity verification for SC-007 and SC-008

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. Typed & Auditable by Design | PASS | No entity model change; fix restores full schema creation for existing typed entities and tables. |
| II. Layered Architecture | PASS | Change is in host startup bootstrap and Terraform wiring only; no route/business logic layer boundary violations introduced. |
| III. Storage Abstraction | PASS | No provider-model change; existing storage/provider strategy remains intact. |
| IV. Security Defaults | PASS | Server-side app setting wiring only; no client secret exposure or new credential handling patterns introduced. |
| VII. Simplicity & YAGNI | PASS | Smallest viable fix: swap execution API and mirror existing Stack A Terraform pattern. |
| IX. Clean Architecture (.NET stack) | PASS | No cross-layer dependency inversion introduced; startup host remains orchestration point for bootstrap. Direct ADO.NET is restricted to one documented startup DDL bootstrap path (not routine repository reads/writes), justified as a Principle IX exception for performance-critical startup behavior and deterministic literal SQL execution. |

Gate result: PASS.

## Phase 0: Research Outcomes

Research is captured in `research.md` and resolves all technical uncertainties.

1. **SQL execution decision**: Use ADO.NET `ExecuteNonQuery()` for DDL batches to avoid EF Core format-token interpretation (`{}` issue).
2. **Connection strategy**: Keep EF Core-owned connection via `GetDbConnection()` with explicit open/close guard to preserve existing lifecycle pattern.
3. **Terraform pattern**: Mirror Stack A conditional `extra_app_settings` behavior in Stack B live root.
4. **Deployment variable path**: Keep existing `common.sh` export path and `.env_qa` values unchanged.
5. **Cross-stack parity baseline**: Use Stack A as baseline for conditional app-setting behavior and health semantics; align Stack B to baseline without Stack A code changes.
6. **Verification approach**: Rebuild, deploy, run script-driven plan/apply (`infra/scripts/deploy.sh`), validate schema completeness, and execute cross-stack parity checks for app-setting behavior and `awrApi` semantics.

Raw SQL path documentation note: This feature uses direct ADO.NET only for startup schema bootstrap batch execution, a bounded operational path justified by performance-critical startup behavior and deterministic literal SQL execution where EF Core placeholder parsing causes correctness failures. All regular data access remains in EF Core/repository paths.

All prior `NEEDS CLARIFICATION` placeholders are resolved.

## Project Structure

### Documentation (this feature)

```text
specs/007-fix-stackb-sql-endpoint/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── spec.md
├── checklists/
└── tasks.md
```

### Source Code (repository root)

```text
dotnet/
└── src/
    └── Web.Server/
        └── Program.cs                    # Azure SQL schema bootstrap execution method

infra/
└── terraform/
    ├── live/
    │   ├── stack-a/                     # parity baseline validation only
    │   └── stack-b/
    │       ├── main.tf                   # conditional extra app settings wiring
    │       └── variables.tf              # awr_seq_api_endpoint variable declaration
    └── modules/
        └── stack-b/
            ├── main.tf                   # pre-existing merge(var.extra_app_settings)
            └── variables.tf              # pre-existing extra_app_settings variable

server/
└── storage/
    └── schema.sql                        # read-only input consumed by bootstrap
```

**Structure Decision**:
No structural refactor. Use existing Stack B runtime host and Stack B Terraform live root for code edits, and add parity validation against Stack A Terraform/runtime behavior as a release gate.

## Phase 1 Design Outputs

- **Data model**: `data-model.md` confirms no new entities/relationships; this is an execution and configuration bugfix.
- **Contracts**: No new external API/interface contract artifact required for this feature. Existing health endpoint and deployment interfaces remain unchanged.
- **Quickstart**: `quickstart.md` defines deployment and validation steps for SC-001 through SC-008, including cross-stack app-setting and health-semantics parity checks.
- **Agent context update**: `.specify/scripts/bash/update-agent-context.sh copilot` executed as part of planning workflow.

## Post-Design Constitution Re-Check

| Principle | Status | Post-Design Notes |
|-----------|--------|-------------------|
| I. Typed & Auditable by Design | PASS | Design restores creation of all existing schema-backed entities without adding mutable side paths. |
| II. Layered Architecture | PASS | Design remains confined to startup bootstrap and IaC configuration boundaries. |
| III. Storage Abstraction | PASS | No direct storage-provider coupling added outside current abstractions. |
| IV. Security Defaults | PASS | App setting injection remains server-side and conditional; no client-secret leakage path introduced. |
| VII. Simplicity & YAGNI | PASS | Reuses existing patterns (SQLite bootstrap approach and Stack A Terraform conditional map). |
| IX. Clean Architecture (.NET stack) | PASS | No new layering violations; infrastructure usage remains in expected host/bootstrap boundary. Direct ADO.NET remains limited to the documented startup bootstrap DDL path only, justified as a Principle IX exception for performance-critical startup behavior and deterministic literal SQL execution. |

Post-design gate result: PASS.

## Complexity Tracking

No constitution violations or exception requests.

Documented raw SQL usage: `DbCommand.ExecuteNonQuery()` is approved only for the startup schema bootstrap DDL path in `dotnet/src/Web.Server/Program.cs`, justified as a Principle IX exception for performance-critical startup behavior and deterministic literal SQL execution; all non-bootstrap data access continues to use EF Core/repository abstractions.
