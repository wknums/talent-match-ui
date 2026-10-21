<!--
Sync Impact Report
- Version change: 2.0.0 -> 3.0.0 (MAJOR: Principle VIII replaces blanket failure of
  all remaining work after a delegated authorization lapse with phase-aware handling)
- Modified principles:
  - VIII. System Communication Architecture -> VIII. System Communication Architecture
    (authorization lapse stops source access while permitting source-independent scoring to finish)
- Added sections: None
- Removed sections: None
- Templates:
  - ✅ .specify/templates/plan-template.md updated
  - ✅ .specify/templates/spec-template.md updated
  - ✅ .specify/templates/tasks-template.md updated
  - ✅ .specify/templates/constitution-template.md reviewed; no project-specific change required
  - ✅ .specify/templates/commands/*.md reviewed; directory is not present
- Runtime guidance:
  - ✅ README.md, AUTHENTICATION.md, INTEGRATION.md, and docs reviewed; no directly dependent
    authorization-lapse wording found
- Deferred items: None
-->

# Talent Matching Platform — Constitution

## Core Principles

### I. Typed & Auditable by Design
All entities (jobs, applications, scoring runs, aggregated results, audit events) MUST be fully typed
via `src/types/index.ts`. Every state-changing operation MUST produce an immutable ledger entry with
a correlation ID, actor, and timestamp. No silent mutations — if it changes state, it leaves a trace.

### II. Layered Architecture (NON-NEGOTIABLE)
The codebase supports two technology stacks (see Technology Stacks below). Regardless of stack,
the same layered separation MUST be maintained:

#### Stack A — React/TypeScript (current prototype)
- **Backend API** (`server/`): Express + TypeScript; storage-agnostic via `StorageProvider` interface;
  routes only in `server/routes/`; no business logic in route handlers.
- **API Client** (`src/lib/spark-client.ts`, `src/lib/api.ts`): all fetch calls go through these
  modules only; components MUST NOT call `fetch` directly.
- **UI Components** (`src/components/`): pure presentation + local state; data fetching via
  `src/lib/api.ts` only.

#### Stack B — .NET Blazor WASM / Clean Architecture (target)
- **Presentation** (Blazor WASM): Razor components, pages, and view models; citizen-facing so
  WASM hosting model is required for client-side execution without server round-trips.
- **API** (.NET Core Web API): minimal API or controller-based endpoints; follows Clean Architecture
  layering (see Principle IX).
- **Application Layer**: use cases / handlers (MediatR or similar); no direct infrastructure
  dependencies.
- **Domain Layer**: entities, value objects, domain events; zero framework dependencies.
- **Infrastructure Layer**: EF Core DbContext, repository implementations, external service
  adapters; the only layer permitted to reference `Microsoft.EntityFrameworkCore`.
- **Shared Kernel** (optional): cross-cutting contracts (DTOs, interfaces) referenced by multiple
  layers but owning no implementation.

### III. Storage Abstraction (NON-NEGOTIABLE)
All persistence goes through the `StorageProvider` interface (`server/storage/types.ts` for Stack A - equivalent for Stack B). The active
provider is selected via `STORAGE_PROVIDER` env var (`local` | `azuresql`). No component or route
may import a concrete storage implementation directly.

### IV. Security Defaults
- Passwords MUST be hashed with `crypto.subtle` SHA-256 before storage; plaintext passwords NEVER
  stored or logged.
- Role-based access MUST be enforced server-side; recruiter users can only see their own department's
  data.
- File uploads MUST be validated (type allowlist, size limit) on both client and server before
  processing.
- No secrets (`API keys`, connection strings) are ever prefixed `VITE_` (client bundle exposure).
- All Azure resource identifiers, including tenant and subscription IDs, resource groups, resource
  names, endpoints, URIs, application or object IDs, storage accounts, containers, and Key Vault
  references, MUST have their concrete values defined only in untracked `.env` files.
- All other environment-specific identifiers, credentials, API keys, tokens, connection strings,
  and secret values MUST also exist only in untracked `.env` files. Code, tests, scripts,
  infrastructure definitions, checked-in configuration, and documentation MUST reference only the
  corresponding environment variable names and MUST NOT hardcode those values.
- Committed environment templates such as `.env.example` MAY declare variable names and describe
  their purpose, but environment-specific identifiers and secret values MUST remain blank.
- Real `.env` files MUST remain excluded from version control. Sensitive or environment-specific
  values MUST NOT be logged, copied into examples, or included in documentation.
- When Blob Storage is used, SAS tokens are forbidden by security policy; RBAC MUST be used instead.

### V. LLM Integration Discipline
All LLM calls MUST go through server-side proxy - in Stack A: `server/routes/llm.ts` equivalent for Stack B. The frontend calls
`/api/llm` (in stack A, and equivalent code path in stack B) only — never an LLM provider directly. Prompts MUST be constructed server-side to
prevent prompt injection from untrusted client input. JSON-mode must be used when structured output
is required.

### VI. UI Precision & Responsiveness
- Async operations MUST show loading state and handle errors with notifications.
- Optimistic updates require rollback on failure.
- Dashboard stats MUST refresh automatically (≤30-second polling interval).
- Tables with >100 rows MUST use virtual scrolling or pagination.
- All colour pairings MUST maintain ≥4.5:1 WCAG contrast ratio.

### VII. Simplicity & YAGNI
Do not add abstractions, helpers, or error handling for scenarios that cannot happen with the current
implementation. Complexity must be justified by a current requirement. Mock/simulated API data is
acceptable while a real backend is not yet connected.

### IX. Clean Architecture (.NET stack — NON-NEGOTIABLE)
When building with the .NET stack, all code MUST follow Clean Architecture:

- **Dependency rule**: dependencies point inward only — Domain ← Application ← Infrastructure /
  Presentation. No inner layer may reference an outer layer.
- **Domain layer** is the centre: entities, aggregates, value objects, domain events, and
  repository interfaces live here. Zero NuGet package dependencies beyond the BCL.
- **Application layer** contains use-case orchestration (commands, queries, handlers,
  validators). References Domain only.
- **Infrastructure layer** implements persistence (Entity Framework Core), messaging, external
  APIs. References Application and Domain.
- **Presentation layer** (Blazor WASM + .NET Core API) references Application only for
  dispatching commands/queries; never bypasses to Infrastructure directly.
- Entity Framework Core is the standard ORM for all data access and code-first migrations.
  Direct ADO.NET or raw SQL is permitted only for documented performance-critical paths.
- Solution structure MUST mirror layers: `src/Domain/`, `src/Application/`,
  `src/Infrastructure/`, `src/Web/` (or equivalent naming agreed at project start).

### VIII. System Communication Architecture
The following diagram defines how this codebase (awr-client) interacts with the platform services
when not in demo/mock API mode. All integrations MUST conform to these boundaries.

- Product authentication and authorization MUST remain separate from Microsoft 365 source
  authorization. Product identity authorizes product jobs and actions; it MUST NOT be treated as
  permission to read a SharePoint or OneDrive source.
- Each arbitrary work or school SharePoint or OneDrive folder journey MUST use an explicit,
  per-user delegated authorization-code connection through a dedicated multi-tenant confidential
  connector. The flow MUST use PKCE and organizational-tenant endpoints. Personal
  Microsoft-account OneDrive sources MUST remain unsupported.
- Before exposing folder content, names, or counts, the API MUST use Microsoft Graph with the
  selecting source user's delegated access to resolve and verify the exact folder. Discovery and
  reads MUST remain confined to that verified folder and its descendants.
- Temporary pre-confirmation and request authorization caches MUST be encrypted and scoped to their
  actor, source account, job, exact folder, lifecycle, and, after confirmation, request. Source
  credentials and protected cache material MUST never be logged, audited as secrets, or returned
  to a browser or other client. The material MUST be discarded when its connection is abandoned or
  expires, when authorization becomes unusable, and when the request reaches its settled terminal
  lifecycle.
- Background processing MUST continue with delegated access as the source user who confirmed the
  request. If that authorization lapses or access is lost, the system MUST immediately stop every
  further source read and MUST fail every non-terminal item that still requires source access with
  an actionable reconnect outcome. An item whose content was already securely imported and handed
  to scoring MUST be allowed to finish because scoring no longer uses Microsoft 365 authorization.
  The system MUST NOT use application-only access, another user's identity, or any other fallback
  identity.
- Deployment-time tenant, host, site, drive, root, or folder allow-lists and pre-granted
  application-only source access are prohibited. The connected user's effective Graph access is
  authoritative.
- User consent MUST be used where tenant policy permits. A consent-policy failure that requires an
  administrator MUST expose a clear admin-consent action and resumable connection outcome without
  reading source content.

```mermaid
sequenceDiagram
    autonumber
    participant Client as Client (awr-client)
    participant OAPI as OpenAPI client
    participant APIM as APIM
    participant API as Platform API
    participant ORCH as Orchestrator
    participant SBIN as SB engine-runs
    participant WKR as Engine worker
    participant CORE as awreason core
    participant AOAI as AOAI (Reasoning) via APIM
    participant BLOB as Blob Storage
    participant SBOUT as SB results
    participant SQL as Azure SQL
    participant SIG as Azure SignalR
    participant EID as Entra ID
    participant KV as Key Vault
    participant GRAPH as Microsoft Graph (SharePoint / OneDrive)
    Client->>OAPI: create run
    OAPI->>APIM: POST /runs
    APIM->>API: forward
    API->>SQL: insert run (Started)
    API->>SIG: push "run started"

    API->>ORCH: start orchestration
    ORCH->>SBIN: enqueue RunMessage
    ORCH->>SIG: push "queued"

    SBIN-->>WKR: deliver RunMessage
    WKR->>CORE: execute run
    CORE->>AOAI: reasoning calls (via APIM)
    CORE->>BLOB: upload artifacts
    WKR->>SIG: push "processing progress"

    WKR->>SBOUT: publish RunResultMessage
    SBOUT-->>ORCH: deliver result
    ORCH->>SQL: update status, timings, tokens
    ORCH->>SIG: push "completed"

    Client->>OAPI: get run status
    OAPI->>APIM: GET /runs/{id}
    APIM->>API: forward
    API-->>OAPI: status + artifact links
    SIG-->>Client: live updates (optional)

    Note over Client,GRAPH: Product auth and per-user Microsoft 365 source auth remain separate
    Client->>API: begin source connection for pasted work/school folder
    API->>EID: multi-tenant authorization code + PKCE (delegated read)
    EID-->>API: callback or admin-consent-required outcome
    API->>KV: wrap the temporary connection data key
    API->>SQL: store encrypted, actor/job-bound authorization cache
    API->>GRAPH: verify exact folder as the selecting source user
    Client->>API: preview verified folder, then confirm request
    API->>KV: re-encrypt/rewrap cache for the request binding
    API->>SQL: atomically store request, items and request authorization
    loop Background worker inside the API host (no client needed)
        API->>SQL: claim items under a lease
        API->>KV: unwrap the data key
        API->>EID: silently acquire delegated access as confirming source user
        API->>GRAPH: read only the verified folder and confirmed items
        API->>BLOB: stage the original and the converted file
        API->>SQL: create the Queued application (existing scoring pipeline continues)
    end
    Note over API,GRAPH: On delegated lapse, stop every further source read immediately
    API->>SQL: fail non-terminal items that still require source access
    Note over API,AOAI: Securely imported items already handed to scoring finish without Microsoft 365 auth
    API->>SQL: discard protected authorization at lifecycle end
    Note over API,GRAPH: No application-only fallback and no deployment-time tenant/folder allow-list
```

## Technology Stacks

This codebase supports two stacks. Stack A is the current prototype; Stack B is an additional target for
 for a specific customer. New features SHOULD be planned for both Stack A and Stack B.
both technology stack use shared database and shared datamodel 
Deployment code and Infrastructure as code use terraform and bash.

### Stack A — React / TypeScript (prototype)

| Layer | Technology |
|---|---|
| Frontend | React 19, TypeScript, Vite 7, Tailwind CSS v4 |
| UI Components | shadcn/ui (New York style), Radix UI primitives |
| State / Data | TanStack Query (optional), `src/lib/api.ts` abstraction |
| Animations | Framer Motion |
| Icons | Phosphor Icons (`@phosphor-icons/react`) |
| Forms | React Hook Form + Zod |
| Charts | Recharts |
| Backend | Express 4, TypeScript, tsx (dev), Node 20+ |
| Storage (local) | shared sqllite (`shared-data/talentmatch.db`) |
| Storage (cloud) | Azure SQL via `mssql` (optional dependency) |
| LLM | OpenAI API or Azure OpenAI (server-side proxy) |
| Fonts | Space Grotesk (UI), JetBrains Mono (data/metrics) |

### Stack B — .NET Blazor WASM / Clean Architecture (target)

| Layer | Technology |
|---|---|
| Frontend | Blazor WebAssembly (.NET 9+), hosted model |
| UI Components | MudBlazor or Fluent UI Blazor (TBD at project start) |
| State / Data | Fluxor or built-in cascading state; HttpClient via typed clients |
| API | ASP.NET Core Web API (minimal APIs preferred) |
| Application | MediatR (CQRS), FluentValidation |
| ORM / Migrations | Entity Framework Core (code-first migrations) |
| Storage | local: shared sqllite in shared-data/talentmatch.db cloud: Azure SQL |
| Architecture | Clean Architecture (see Principle IX) |
| Auth | Microsoft Identity / Entra ID |
| LLM | Azure OpenAI via APIM (server-side only) |

## Development Workflow

1. Features planned and tracked via speckit: `spec.md` → `plan.md` → `tasks.md` → `implement`.
2. All new UI features MUST have corresponding type definitions added to `src/types/index.ts` before
   implementation begins.
3. API shape changes MUST update the endpoint mapping table in `INTEGRATION.md`.
4. Environment variables MUST be documented in `.env.example` with comments.
5. For stack A: `npm run dev` runs both backend (`tsx watch`) and frontend (Vite) concurrently — this is the
   standard local dev command.
6. Type errors are blocking: `tsc -b --noCheck` must pass before PR merge.

## Governance

This constitution supersedes all other practices; amendments require updating this file and noting
the reason and date. All implementation tasks must be checked against these principles before
execution. Complexity that violates YAGNI (YAGNI stands for “You Aren’t Gonna Need It.”) must be explicitly justified in the relevant `plan.md`.

**Version**: 3.0.0 | **Ratified**: 2026-03-03 | **Last Amended**: 2026-10-13

### Amendment Log
| Version | Date | Change |
|---|---|---|
| 1.1.0 | 2026-03-04 | Added Stack B (.NET Blazor WASM, Clean Architecture, EF Core); added Principle IX |
| 1.1.1 | 2026-04-16 | Added Technology for Deployment and Infrastructure as Code (Bash and Terraform)|
| 1.2.0 | 2026-10-05 | Required `.env`-only storage for Azure and environment-specific identifiers and secrets; prohibited hardcoded values in code and documentation |
| 1.3.0 | 2026-10-06 | Principle VIII diagram: added Microsoft Graph (SharePoint/OneDrive), Entra ID and Key Vault for the bulk scoring feature (001-sharepoint-bulk-scoring). Graph is content access only. Same-tenant uses the recruiter's delegated on-behalf-of flow with a per-request encrypted authorization whose data key is wrapped by Key Vault; cross-tenant dev/QA uses app-only `Sites.Selected` read. Reason: the feature adds an external content source and a durable background authorization the diagram did not describe |
| 2.0.0 | 2026-10-13 | Replaced Principle VIII's same-tenant OBO/cross-tenant app-only source model with explicit per-user multi-tenant authorization-code connections for arbitrary work/school SharePoint and OneDrive folders. Exact-folder Graph verification, scoped encrypted cache lifecycles, confirming-user delegated background access, admin-consent recovery, and the prohibitions on personal OneDrive, application-only fallback, and deployment-time source allow-lists are now mandatory. This is a major amendment because it incompatibly redefines the previously approved source-authorization boundary |
| 3.0.0 | 2026-10-13 | Replaced Principle VIII's blanket requirement to fail all remaining work after delegated Microsoft 365 authorization lapses with phase-aware handling: stop every further source read immediately, fail non-terminal items that still require source access, and allow securely imported items already handed to source-independent scoring to finish. No fallback identity is permitted. This is a major amendment because it incompatibly redefines the required lapse behavior |
