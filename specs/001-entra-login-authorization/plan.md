# Implementation Plan: Entra Login, Access Management, and Stack B Workspace

**Branch**: `001-entra-login-authorization` | **Date**: 2026-07-30 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-entra-login-authorization/spec.md`

## Summary

Complete the existing single-tenant Entra authorization foundation with mode-aware access management in both application stacks and shell-wide collapsible navigation in Stack B. A validated first sign-in may create a denied pending Entra profile; application Admins and scoped Organization Admins then use one transactional aggregate command to converge that profile's organization membership, active departments, explicit default department, and delegated role assignments. Shared Azure SQL/SQLite constraints, server-side authorization, optimistic concurrency, and correlated immutable audit outcomes keep both stacks equivalent and fail closed. Stack B owns navigation state in the application shell, persists the desktop preference for the browser session, uses a transient overlay on compact screens, audits user-triggered state changes before applying them, and reflows routed content without discarding route or form state.

## Technical Context

**Language/Version**: TypeScript 5.7 on Node.js 20+; React 19; C# on .NET 10 / Blazor WebAssembly 10
**Primary Dependencies**: Express 4, MSAL Browser/React 4/3, `jose` 6, `mssql` 11, better-sqlite3 12; ASP.NET Core 10, Microsoft.Identity.Web 4.7, EF Core 10, MediatR, FluentValidation, Blazor WebAssembly MSAL 10
**Storage**: Shared SQLite database for local development and the Azure database selected by the active environment definition file for cloud; equivalent Stack A repositories and Stack B EF Core mappings/migrations
**Testing**: Vitest 4, Testing Library, Playwright 1.58 with axe; xUnit 2.9, bUnit 2.7, FluentAssertions, Moq, EF Core SQLite, WebApplicationFactory
**Target Platform**: Azure App Service APIs plus browser-hosted React and Blazor WebAssembly clients; modern desktop, tablet, and mobile browsers
**Project Type**: Dual-stack web application with shared authorization data model and contracts
**Performance Goals**: Standard Entra onboarding is completable in under 3 minutes; navigation width is released and content settles within 1 second
**Constraints**: Single tenant; no local-password fallback in Entra mode; no runtime Microsoft Graph dependency for application-managed delegation; identity and scope mutations are transactional and auditable; access tokens older than 15 minutes are rejected; no cross-organization privilege inheritance; explicit defaults grant no authority; Stack B navigation preserves route and unsaved state and changes only after audit success; no deprecated packages
**Scale/Scope**: Two API/client stacks, four roles, multiple organizations and departments per user, all authenticated Stack B shell routes, and desktop/compact navigation variants

## Constitution Check

*GATE: Passed before Phase 0 research and re-checked after Phase 1 design.*

- **Typed and auditable by design**: PASS. Shared request/response and UI-state types cover Entra profiles, access aggregates, explicit defaults, optimistic versions, canonical errors, and Stack B navigation audit commands before implementation. Every state mutation emits an immutable correlated outcome. Navigation state changes only after audit persistence succeeds; failure retains the prior state and preference.
- **Layered architecture**: PASS. Stack A routes delegate to services and repositories exposed by `StorageProvider`; React uses the typed API client boundary. Stack B keeps entities and interfaces in Domain, orchestration and validation in Application, EF implementations in Infrastructure, and endpoint/layout concerns in Web projects.
- **Storage abstraction**: PASS. Stack A access-management persistence is added to `StorageProvider` and selected-provider construction. Stack B persistence remains behind Domain repository interfaces. Azure SQL and SQLite receive equivalent constraints and transaction semantics.
- **Security defaults**: PASS. APIs validate tenant-bound bearer tokens and re-read actor authority in each mutation transaction. Administrator-supplied object IDs cannot stage profiles. Organization Admins cannot cross organization boundaries, globally disable identities, or grant global Admin. Password APIs and UI remain unavailable in Entra mode.
- **UI precision and responsiveness**: PASS. Both access-management clients include loading, empty, validation, conflict, confirmation, success, failure, and visible asynchronous-error notification states. Navigation includes keyboard and assistive semantics, visible focus, the 4.5:1 contrast floor, reduced-motion behavior, compact overlay behavior, route persistence, audit-failure rollback, and responsive route-inventory validation.
- **Simplicity/YAGNI**: PASS. One organization-access aggregate replaces unsafe composition of independently committing repositories. The default pointer remains on organization membership. Navigation uses one scoped state service and a small browser adapter rather than a general state framework. No high-volume benchmark or speculative infrastructure is included.
- **Clean Architecture (.NET)**: PASS. Commands, queries, validators, and audit requests reside in Application; repository interfaces and authorization entities remain in Domain; EF transactions are Infrastructure-owned; endpoints and Blazor components dispatch typed use cases.
- **Dual-stack/shared model**: PASS. Authentication, onboarding, defaults, authorization, errors, and audit semantics share one contract and data model. The navigation enhancement is intentionally Stack B-only and does not alter authorization parity.
- **Infrastructure governance**: PASS. Terraform/Bash ownership remains unchanged. Every Azure workflow loads the active environment definition file and verifies its tenant and subscription before discovery or mutation. No new Azure service or deprecated dependency is introduced.

No constitution violations require justification.

## Phase 0: Research Decisions

Phase 0 is complete in [research.md](research.md). The controlling decisions are:

1. Use SPA authorization code with PKCE, one protected API registration, and separate public clients for Stack A and Stack B.
2. Keep scoped authorization and revocation in the shared database while supporting both operator-managed Entra groups and application-managed delegated assignments.
3. Reject tokens older than 15 minutes and allow one silent client refresh so SQL role changes and identity disablement meet the revocation window.
4. Bootstrap the verified database-owner user through a fail-closed, idempotent operator workflow guarded by the active environment definition file.
5. Preserve simple authentication only for explicit local development and never as an Entra fallback.
6. Reuse the immutable audit stream for authentication, authorization, access-management, seed, organization, and navigation outcomes.
7. Discover onboarding targets only through prior validated configured-tenant sign-in; ordinary onboarding does not call Microsoft Graph.
8. Mutate one target-organization access aggregate in a serializable transaction with deterministic locking and optimistic `AuthorizationVersion` checks.
9. Store `DefaultDepartmentMembershipId` on organization membership so the explicit default must reference an active grant and never grants authority itself.
10. Route administration by authentication mode and own Stack B navigation state in the application shell.

All planning unknowns are resolved. The removed high-volume authorization benchmark is not part of this plan.

## Phase 1: Design and Contracts

### Shared Data Design

- Add `AuthorizationVersion` to Entra users and increment it after each successful access-management mutation.
- Add nullable `DefaultDepartmentMembershipId` to organization memberships; active rows require a valid pointer after migration convergence.
- Add a candidate key on department memberships `(Id, UserId, OrganizationId)` and a composite foreign key from the default pointer plus membership user/organization.
- Extend active delegated-assignment uniqueness equivalently in Azure SQL and SQLite while retaining revoked history.
- Backfill an explicit default only when an active organization membership has exactly one active department membership. Stop readiness for zero or multiple candidates; never infer defaults by ordering or names.
- Return `defaultDepartmentId` and `authorizationVersion` in authorization and access-management contracts. Defaults select initial context only.

### Access-Management Contract

Extend [organization-admin.openapi.yaml](contracts/organization-admin.openapi.yaml) with:

- `GET /api/access-management/users` for authorized paginated search.
- `GET /api/access-management/users/{objectId}` for one actor-filtered access aggregate.
- `PUT /api/access-management/users/{objectId}/organizations/{organizationId}` for atomic onboarding, reactivation, replacement, or organization-scope revocation.
- `PATCH /api/access-management/users/{objectId}` for application-Admin-only presentation and global activation changes.
- `DELETE /api/access-management/users/{objectId}/organizations/{organizationId}/role-assignments/{assignmentId}` for targeted delegated revocation.

The aggregate `PUT` carries `expectedVersion`, presentation fields, membership status, active department IDs, explicit default department ID, and the complete desired delegated role set for one organization. Tenant is server-owned. `400` represents invalid shape or scope, `403` actor authority, `404` an unknown tenant-verified profile or organization, and `409` stale or incompatible concurrent state.

Add a typed Stack B navigation-audit operation through the Application/API boundary. It derives actor identity from the validated token, accepts the requested state action, correlation ID, and timestamp, and returns an immutable outcome. The client applies and persists the state only after success.

### Transaction and Audit Boundary

For each access mutation:

1. Begin a serializable transaction and lock actor and target aggregate rows in deterministic order.
2. Re-read actor authority, target version, organizations, departments, memberships, and assignments inside the transaction.
3. Validate the complete desired state, including default membership and role scope.
4. Converge presentation state, memberships, default, and delegated assignments while preserving unrelated organizations and global/group/bootstrap assignments.
5. Increment `AuthorizationVersion`, append one success event in the transaction, and commit.
6. Roll back on failure, then append one correlated failure outcome without tokens or other users' data.

Application Admin may manage all organization scopes and global application activation. Organization Admin may inspect and mutate only administered organizations and cannot globally disable a user. Neither access-management path grants or revokes application-wide Admin.

### Stack B Navigation Design

- Move navigation ownership from `NavMenu` to `MainLayout` through a scoped `NavigationShellState`.
- Define navigation state and audit request/result contracts in `src/types/index.ts` before implementation and mirror runtime contracts in the .NET Application layer.
- Render one native icon button outside the collapsible region with dynamic accessible name, `aria-expanded`, `aria-controls`, visible focus, and stable dimensions.
- Before a user-triggered collapse or expansion, submit the audit command. Apply and persist state only on success; otherwise retain prior state and show a visible error notification.
- On desktop, change the sidebar grid track from its established width to zero and give the main region `min-width: 0`.
- On compact screens, use a transient off-canvas overlay/backdrop that closes on route change, backdrop, Escape, or breakpoint transition and is never persisted.
- Persist only the desktop collapsed preference in `sessionStorage` and reconcile it deterministically across responsive breakpoints.
- Keep the routed body mounted and use container-responsive one-, two-, and three-column page layouts plus local dense-table overflow containers.

### Post-Design Constitution Re-check

PASS. The design preserves typed contracts, immutable audit outcomes, storage abstraction, server-side authorization, dual-stack data parity, .NET Clean Architecture, error notifications, and responsive/accessibility requirements. The aggregate repository and navigation adapter are required by current atomicity and shell-state requirements and are not general-purpose abstractions.

## Project Structure

### Documentation (this feature)

```text
specs/001-entra-login-authorization/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── auth-api.openapi.yaml
│   ├── organization-admin.openapi.yaml
│   ├── entra-token-claims.md
│   └── operator-cli.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── components/                    # mode-aware simple/Entra administration UI
├── lib/                           # typed clients and auth-context conversion
└── types/                         # access aggregate and navigation contracts

server/
├── routes/                        # auth, access-management, and audit adapters
├── services/                      # authorization and aggregate use cases
└── storage/
  ├── repos/                     # aggregate and existing repositories
  ├── types.ts                   # StorageProvider contract
  ├── schema.sql
  └── schema-sqlite.sql

dotnet/src/
├── Domain/                        # entities and repository interfaces
├── Application/
│   ├── AccessManagement/          # commands, queries, validators, DTOs
│   └── Navigation/                # navigation audit use case
├── Infrastructure/
│   └── Persistence/               # EF aggregate transaction and migration
├── Web.Server/
│   └── Endpoints/                 # mode-aware endpoints and audit adapter
└── Web.Client/
  ├── Layout/                    # shell-owned navigation and responsive CSS
  ├── Pages/                     # mode-aware access page and panel reflow
  ├── Components/                # Entra access workflow
  ├── Services/                  # typed API client and navigation state
  └── wwwroot/js/                # session storage and breakpoint adapter

infra/
└── scripts/                       # environment-gated bootstrap/operator flows

tests/
├── unit/                          # React UI, seed, and service tests
├── integration/                   # Stack A API/schema/concurrency tests
└── e2e/                           # navigation inventory and accessibility

dotnet/tests/
├── Domain.Tests/
├── Application.Tests/
├── Infrastructure.Tests/
└── Web.Tests/                     # endpoints, bUnit UI, audit, and parity
```

**Structure Decision**: Keep the repository's existing dual-stack layout. Shared authorization contracts and schemas remain authoritative; each stack implements equivalent aggregate behavior inside its established layers. Stack B navigation presentation state remains in Web.Client, while its immutable audit use case crosses the typed Application/API boundary.

## Implementation Sequence

1. Extend shared access, navigation-state, and audit contracts; register Stack A aggregate persistence through `StorageProvider`; then extend schema definitions, EF entities, migrations, and readiness checks for authorization version and explicit defaults.
2. Update authorization context resolution and bootstrap seed to require and return explicit defaults while keeping defaults non-authorizing.
3. Implement aggregate access-management repositories and services with rollback, idempotency, concurrency, actor revalidation, and correlated audits.
4. Add equivalent mode-gated endpoints and typed clients; disable password-management routes in Entra mode.
5. Build access management in both clients with confirmation, all specified states, and visible asynchronous-error notifications; preserve simple-mode UI.
6. Move Stack B navigation state to the shell, require immutable audit success before user-triggered changes, implement notification-backed rollback and desktop/compact behavior, and reflow dense pages.
7. Run focused tests after each slice, then shared-schema parity, cross-stack authorization/onboarding checks, navigation route inventory and accessibility checks, full builds, and the quickstart acceptance matrix.

## Complexity Tracking

No constitution violations require tracking.
