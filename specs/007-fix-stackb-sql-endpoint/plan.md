# Implementation Plan: Fix Stack B Azure SQL Bootstrap and Wire AWR Endpoint

**Branch**: `007-fix-stackb-sql-endpoint` | **Date**: 2025-07-15 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/007-fix-stackb-sql-endpoint/spec.md`

## Summary

Two co-P1 runtime bugs block Stack B Azure deployments:

1. **SQL schema bootstrap failure** — `EnsureSharedAzureSqlSchemaIfNeeded` in `Program.cs` uses EF Core's `Database.ExecuteSqlRaw()`, which interprets `{}`/`{0}` tokens as parameter placeholders. The schema SQL contains `DEFAULT '{}'` for JSON columns (lines 166, 167, 191, 210, 287 of `schema.sql`), causing a `FormatException` that halts table creation mid-way. **Fix**: Replace `ExecuteSqlRaw` with ADO.NET `DbCommand.ExecuteNonQuery()`, which treats the SQL as a literal string with no parameter interpretation — matching the pattern already used by the SQLite bootstrap (`EnsureSharedSqliteSchemaIfNeeded`, lines 287–322).

2. **Missing `AWR_SEQ_API_ENDPOINT` in Stack B Terraform** — The variable is declared in Stack A's live root and passed via `extra_app_settings` with conditional inclusion, but Stack B's live root omits both the variable declaration and the conditional local. **Fix**: Mirror Stack A's `variables.tf` declaration and `main.tf` conditional local into Stack B's live root — the module already accepts `extra_app_settings`.

## Technical Context

**Language/Version**: C# / .NET 10 (Stack B server), HCL / Terraform (infrastructure)
**Primary Dependencies**: Microsoft.EntityFrameworkCore, Microsoft.Data.SqlClient, ASP.NET Core
**Storage**: Azure SQL (production), SQLite (local dev) — shared schema via `server/storage/schema.sql`
**Testing**: Manual verification — `terraform plan` for infra, health endpoint for runtime, table count for schema
**Target Platform**: Azure App Service (Linux), .NET 10 runtime
**Project Type**: Web service (Blazor WASM hosted) + infrastructure-as-code
**Performance Goals**: N/A — bugfix, no new performance targets
**Constraints**: Scoped to SQL execution method only; no schema SQL changes; no changes to `.env_qa` or `common.sh`; no changes to Stack A config or shared modules
**Scale/Scope**: 4 files changed across 2 stacks (1 C#, 2 Terraform HCL in live root); zero new files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| **II. Layered Architecture** | ✅ PASS | Change is in `Program.cs` (startup/presentation layer); no business logic in bootstrap. |
| **III. Storage Abstraction** | ✅ PASS | The schema bootstrap is a one-time DDL initializer, not data-access code. It does not bypass `StorageProvider`. |
| **IV. Security Defaults** | ✅ PASS | No secrets exposed; `AWR_SEQ_API_ENDPOINT` is a URL, not a credential. |
| **VII. Simplicity & YAGNI** | ✅ PASS | Minimal change; mirrors an existing proven pattern (SQLite bootstrap + Stack A Terraform). |
| **IX. Clean Architecture** | ⚠️ JUSTIFIED | Using ADO.NET `DbCommand` directly in `Program.cs` instead of EF Core. Constitution IX allows direct ADO.NET for *"documented performance-critical paths"*. This is a startup DDL bootstrap, not data-access logic. The SQLite bootstrap at lines 287–322 already uses this exact pattern (`connection.CreateCommand()` + `ExecuteNonQuery()`), establishing project precedent. Documented in Complexity Tracking below. |
| **FR-005: Schema SQL unchanged** | ✅ PASS | Only the execution mechanism changes; `schema.sql` is untouched. |
| **FR-009: No .env_qa / common.sh changes** | ✅ PASS | Both already have the correct values. |

**Gate result**: ✅ PASS — all principles satisfied; one justified deviation documented.

## Project Structure

### Documentation (this feature)

```text
specs/007-fix-stackb-sql-endpoint/
├── plan.md              # This file
├── research.md          # Phase 0: research findings
├── data-model.md        # Phase 1: affected entities & schema
├── quickstart.md        # Phase 1: verification runbook
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (files affected)

```text
# Fix 1: SQL bootstrap (C#)
dotnet/src/Web.Server/Program.cs                          # EnsureSharedAzureSqlSchemaIfNeeded method (~lines 324-342)

# Fix 2: Terraform (Stack B live root only)
infra/terraform/live/stack-b/variables.tf                 # Add awr_seq_api_endpoint variable
infra/terraform/live/stack-b/main.tf                      # Add conditional local + pass extra_app_settings

# Verification touchpoints (read-only — no changes)
infra/terraform/modules/stack-b/variables.tf              # Confirm extra_app_settings exists (line 77)
infra/terraform/modules/stack-b/main.tf                   # Confirm merge(... var.extra_app_settings) (line 35)
infra/terraform/live/stack-a/main.tf                      # Reference pattern for conditional local
infra/terraform/live/stack-a/variables.tf                  # Reference pattern for variable declaration
infra/scripts/lib/common.sh                               # Confirm TF_VAR_awr_seq_api_endpoint export (line 132)
server/storage/schema.sql                                  # Confirm curly-brace DEFAULT values
infra/scripts/package-stack-b.sh                           # Build script for deployment artifact
dotnet/src/Web.Server/Endpoints/HealthEndpoints.cs         # Health check reads AWR_SEQ_API_ENDPOINT
```

**Structure Decision**: No new files or directories. Changes are isolated to 3 existing files:
1 in the .NET server project, 2 in the Stack B Terraform live root.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Direct ADO.NET in `Program.cs` (Principle IX permits EF Core as default ORM) | `ExecuteSqlRaw` interprets `{}` as parameter placeholders; there is no EF Core API that executes raw DDL without placeholder interpretation | The SQLite bootstrap already uses `DbCommand.ExecuteNonQuery()` at lines 313-315 of Program.cs — this is established project precedent, not a new pattern |

## Post-Design Constitution Re-Check

*Re-evaluated after Phase 1 design artifacts are complete.*

| Principle | Status | Post-Design Notes |
|-----------|--------|-------------------|
| **II. Layered Architecture** | ✅ PASS | No change — `Program.cs` is presentation layer startup code. |
| **III. Storage Abstraction** | ✅ PASS | No change — DDL bootstrap is infrastructure plumbing, not data access. |
| **IV. Security Defaults** | ✅ PASS | No change — no secrets in code; endpoint URL is non-sensitive. |
| **VII. Simplicity & YAGNI** | ✅ PASS | Design mirrors existing patterns exactly; zero new abstractions introduced. |
| **IX. Clean Architecture** | ⚠️ JUSTIFIED (unchanged) | ADO.NET usage is minimal (3 lines replacing 1 line), follows SQLite precedent, and is documented above. |

**Post-design gate result**: ✅ PASS — design is consistent with pre-research assessment. No new violations introduced.
