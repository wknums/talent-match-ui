# Implementation Plan: Entra Login and Organization Authorization

**Branch**: `001-entra-login-authorization` | **Date**: 2026-07-24 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-entra-login-authorization/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Replace production password authentication in both application stacks with single-tenant Microsoft Entra sign-in and organization-scoped authorization. Stack A React and Stack B Blazor WebAssembly use authorization code with PKCE against one protected API registration and delegated `access_as_user` scope. Each SPA calls its stack's separately deployed API implementation; the Express and ASP.NET Core APIs expose the same contracts, validate tokens for the same audience, and resolve authority from the same shared authorization data. The shared SQL model normalizes organizations, organization-owned departments, user memberships, job scopes, group mappings, multiple scoped assignments, and audit events. Organization Admins manage departments and delegated roles only within assigned organizations without Azure, SQL, or Graph privileges. A Bash/TypeScript seed verifies Azure context and the SQL Entra administrator, creates or reuses the initial organization/department, registers the owner in both, and grants global Admin idempotently. Terraform creates or reuses tenant-local applications, service principals, app roles, and optional security groups.

## Technical Context

**Language/Version**: Node.js 20+, TypeScript 5.7, React 19; C#/.NET 10 and Blazor WebAssembly; Terraform and Bash  
**Primary Dependencies**: Existing Express 4, React, EF Core, and `Microsoft.Identity.Web`; add `@azure/msal-browser`, `@azure/msal-react`, `jose`, and `Microsoft.Authentication.WebAssembly.Msal` with versions aligned to each stack  
**Storage**: Shared SQLite for local development and Azure SQL schema `talentmatch` for cloud; Stack A storage repositories and Stack B EF Core use the same normalized organization, membership, job-scope, and authorization tables  
**Testing**: Vitest 4 and Testing Library for Stack A; xUnit, FluentAssertions, and ASP.NET Core integration tests for Stack B; Terraform validation and Bash check-mode tests for infrastructure scripts  
**Target Platform**: Modern browsers; Linux Azure App Service hosting separate Stack A and Stack B APIs; Blazor WebAssembly client; local Windows/Git Bash development  
**Project Type**: Dual-stack web application with two public SPA clients, two separately deployed API implementations conforming to one shared contract and protected API registration, one shared data model, and shared Azure infrastructure  
**Performance Goals**: Bearer-token validation plus scoped authorization resolution adds less than 100 ms p95 of server-side elapsed time per protected request in each release-mode API against QA Azure SQL. Measure at least 20,000 protected requests after a 60-second warm-up, with JWKS metadata and database connection pools warm, 25 concurrent clients, 10,000 users, 100 organizations, 1,000 departments, and 100,000 active assignments; the exercised identity has 10 organization memberships, 25 department memberships, and 20 active assignments. Report each stack separately and exclude browser/network transit from the authorization-stage timer.  
**Constraints**: Single tenant only; exact tenant/subscription context gate; resource-scoped hierarchy with no cross-organization inheritance; every user has organization and department membership; no local-password fallback in Entra mode; no runtime Microsoft Graph dependency; access tokens older than 15 minutes are rejected; server-side authorization on every protected operation; no client secrets in browser configuration  
**Scale/Scope**: One workforce tenant, two application stacks, four role types, up to the benchmark profile of 100 organizations, 1,000 departments, 10,000 users, and 100,000 active scoped assignments in the existing shared database

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Design Gate

- **Typed and auditable**: PASS. Entra identity, organizations, departments, memberships, scoped assignments, job scope, seed records, and errors are typed in the shared TypeScript model and equivalent .NET contracts. Authentication and authorization changes append immutable processing events with actor, target scope, timestamp, result, and correlation ID.
- **Layered architecture**: PASS. Stack A browser auth stays behind the `src/lib/api.ts` client facade, API authentication and authorization stay in middleware/services, and persistence stays behind the `StorageProvider` interface. Stack B uses Web.Client presentation, Web.Server protocol concerns, Application use cases, Domain entities/interfaces, and Infrastructure persistence.
- **Storage abstraction**: PASS. New SQL access goes through the Stack A `StorageProvider` contract in `server/storage/types.ts` and Stack B Domain repository interfaces implemented by EF Core; routes and components do not access concrete storage.
- **Security defaults**: PASS. APIs validate bearer tokens, memberships, normalized job scope, and role hierarchy server-side. Organization Admin operations are constrained to the actor's organization. Entra mode disables local password endpoints and default-password seeds; browser clients contain no secrets.
- **UI precision**: PASS. Both clients provide explicit signing-in, access-denied, token-refresh, identity-provider-error, and organization-administration failure states; implementation validation includes the constitution's loading, rollback, pagination, and WCAG contrast requirements.
- **Simplicity/YAGNI**: PASS. Both API implementations reuse one shared authorization schema and equivalent contracts. Microsoft Graph is limited to operator workflows; no runtime Graph service or third application host is introduced.
- **Clean Architecture (.NET)**: PASS. Domain owns organization and authorization entities/interfaces, Application owns scoped use cases, Infrastructure owns EF persistence, and Web projects own MSAL/JWT and HTTP concerns.
- **Dual-stack/shared model**: PASS. Both stacks use the same organization/department entities, memberships, role hierarchy, job-scope rules, tables, and conformance tests while retaining separate API hosts.
- **Deployment technology**: PASS. Tenant resources and app settings use Terraform; context validation, orchestration, and sample seeding use Bash with a storage-backed TypeScript CLI.
- **Blocking violations**: None.

### Post-Design Gate

- **Typed/shared contracts**: PASS. [data-model.md](data-model.md), [auth-api.openapi.yaml](contracts/auth-api.openapi.yaml), and [organization-admin.openapi.yaml](contracts/organization-admin.openapi.yaml) define one public contract with equivalent values, invariants, errors, and migration behavior for the Express and ASP.NET Core API implementations.
- **Server-side security**: PASS. [entra-token-claims.md](contracts/entra-token-claims.md) requires agreement among validated identity, active memberships, applicable assignments, and normalized resource scope on every protected operation.
- **Auditability**: PASS. Organization, department, membership, assignment, seed, and authorization transitions reuse immutable `ProcessingEvents` with scope IDs and correlation IDs but no token material.
- **Storage and layering**: PASS. SQL mutations are routed through the Stack A `StorageProvider` contract and Stack B Domain/Application/Infrastructure interfaces; operator Bash delegates SQL changes to the storage CLI.
- **Simplicity**: PASS. The required hierarchy is represented by normalized shared entities and scoped assignments without a policy engine, separate authorization database, runtime Graph dependency, or new service.
- **Performance measurability**: PASS. The fixed QA Azure SQL benchmark profile defines release mode, warm-up, request count, concurrency, cardinalities, representative multi-scope identity, measurement boundary, and per-stack reporting.
- **Azure deployment technology**: PASS. Terraform owns tenant resources; Bash owns context validation and orchestration. The Azure policy read remains an execution prerequisite because the planning identity received `403`, not a constitution exception.
- **Blocking violations**: None.

## Project Structure

### Documentation (this feature)

```text
specs/001-entra-login-authorization/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/
├── components/                  # auth plus organization/department/role administration UI
├── lib/                         # API facade over MSAL, token-aware transport, and auth mode
├── types/                       # shared identity/authorization contracts
└── styles/                      # existing shared UI styles

server/
├── cli/                         # shared-database portion of bootstrap/operator workflows
├── middleware/                  # token validation and resource-scope policies
├── routes/                      # auth and organization-administration endpoints
├── services/                    # scoped hierarchy resolution and audit
└── storage/                     # StorageProvider contract and normalized repositories

dotnet/src/
├── Domain/                      # organization, department, membership, identity, and role entities
├── Application/                 # scoped authorization and organization-admin use cases
├── Infrastructure/              # EF mappings, migrations, repositories, current-user adapter
├── Web.Server/                  # separate ASP.NET Core API implementing shared contracts
└── Web.Client/                  # MSAL, authorized routing, administration UI, typed API clients

infra/
├── scripts/                     # context validation, bootstrap seed, operator role wrapper
└── terraform/
  ├── modules/foundation/entra/ # app registrations, service principals, roles, optional groups
  └── live/shared/             # tenant-bound module composition and outputs

docs/
└── ENTRA_AUTHORIZATION.md       # role hierarchy, membership, delegation, and revocation guide

tests/
├── integration/                 # Stack A auth, organization-admin, parity, and performance scenarios
└── unit/                        # claim, membership, hierarchy, and job-scope resolution

dotnet/tests/
├── Application.Tests/           # authorization and organization-admin use cases
├── Infrastructure.Tests/        # normalized shared-schema persistence
└── Web.Tests/                   # bearer auth, administration, and parity scenarios
```

**Structure Decision**: Extend the repository's existing dual-stack structure. Stack A and Stack B each retain a separately deployed API implementation, but both expose the same OpenAPI contracts, accept tokens for the same protected API registration, and resolve authorization from the same shared data model. Protocol handling remains in each web host; normalized organization, department, membership, job-scope, and authorization semantics live in the shared database contracts; no third runtime service is introduced. Entra tenant resources belong to shared Terraform state, while delegated organization administration remains application-managed and requires no runtime Graph service.

## Complexity Tracking

No constitution violations require justification.
