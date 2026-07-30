# Research: Entra Login and Authorization

## Decision 1: Use SPA Authorization Code with PKCE and a Shared API Registration

**Decision**: Register one single-tenant protected API and two public SPA clients, one for Stack A and one for Stack B. Define one delegated API scope (`access_as_user`) and the optional app-role values `admin`, `organization_admin`, `recruiter`, and `business_panel` on the API registration. Stack A uses MSAL Browser/MSAL React; Stack B uses Blazor WebAssembly MSAL. Both APIs validate bearer access tokens issued for the shared API.

**Rationale**: Both clients execute in the browser and cannot protect a client secret. Authorization code with PKCE is the recommended SPA flow. API roles support operator-managed group and bootstrap assignments, while application-managed delegated assignments remain in shared SQL so Organization Admins need no directory privileges.

**Alternatives considered**:

- Server-side OpenID Connect sessions: rejected because the two SPA stacks already call APIs and would retain different session architectures.
- One registration for clients and API: rejected because separate clients permit independent redirect URIs and an API-side `azp` allowlist.
- Implicit flow: rejected because Microsoft recommends authorization code with PKCE for SPAs.

## Decision 2: Support Operator Groups and Application-Managed Delegation

**Decision**: Do not require enterprise-application assignment merely to authenticate; every tenant user may establish an identity profile, but unassigned users remain denied by shared SQL. Platform operators may assign tenant-local security groups to API app roles and map group object IDs to scopes. Application-wide Admins and Organization Admins may instead create audited delegated SQL assignments, with Organization Admin operations constrained to their own organization. Group-based enterprise-application assignment requires Microsoft Entra ID P1 or P2; delegated assignment does not.

**Rationale**: Requiring a new Entra group/app-role change for every scoped assignment would prevent Organization Admins from administering roles without runtime Graph permissions. The hybrid model preserves centrally managed groups while allowing least-privileged in-app delegation. Tenant validation plus default-deny SQL authorization means allowing tenant users to authenticate does not grant protected access.

Microsoft's application-RBAC guidance explicitly supports custom datastores for role information and requires applications to retrieve and secure that role state themselves. Microsoft's enterprise-application guidance confirms that when user assignment is not required, users may authenticate to the application; this plan intentionally separates that authentication permission from protected application access, which remains denied until shared SQL has valid memberships and an applicable assignment.

**Alternatives considered**:

- Raw group claims as role names: rejected because group identifiers are tenant-specific and token overage is harder to handle.
- Group-only role assignment: rejected because Organization Admins would need broad Graph permissions or operator intervention for each change.
- Runtime Graph mutation from the APIs: rejected because it would require elevated delegated/application permissions and make directory availability part of the role-management path.
- Nested groups: rejected because enterprise-app group assignment does not cascade nested membership reliably.
- Runtime Microsoft Graph membership queries: rejected because request paths should not require directory-wide Graph permissions or Graph availability.

## Decision 3: Keep Role Scope and Revocation State in Shared SQL

**Decision**: Extend `Users` with identity-provider, immutable tenant/object identifiers, and status fields. Normalize `Organizations` and organization-owned `Departments`; add many-to-many organization/department memberships, ID-based job scope, `RoleGroupMappings`, and many-per-user scoped `RoleAssignments`. Group assignments require matching token/group claims; delegated assignments require validated identity, active memberships, an audited SQL assignment, and resource scope. Bootstrap remains a direct global Admin assignment but also receives initial organization/department memberships.

**Rationale**: Organization and department names are mutable and currently free text, so IDs and foreign keys are required to prove that memberships, jobs, and assignments share the same parent organization. Multiple assignment rows let one user hold different authority in different organizations without cross-scope elevation. Shared tables preserve Stack A/Stack B parity and immediate revocation.

**Alternatives considered**:

- Treat the token as the only authorization store: rejected because recruiter organization/department scope is not represented and revocation timing is not application-controlled.
- Store role or scope only in `Users`: rejected because one row cannot model multi-organization membership or scoped assignment history.
- Continue free-text job organization/department fields: rejected because strings cannot enforce organization ownership or prevent mismatched pairs.
- Separate authorization databases per stack: rejected because the constitution requires a shared data model and parity.

## Decision 4: Enforce a 15-Minute Authorization Freshness Window

**Decision**: APIs reject otherwise valid access tokens whose `iat` is more than 15 minutes old, allowing a small clock-skew tolerance. Browser clients silently request a fresh API token and retry once. Role changes and revocations performed through the operator workflow update SQL before changing Entra membership; identity disablement is bounded by the token-age check because a disabled identity cannot refresh successfully.

**Rationale**: Standard Entra access-token lifetime can exceed the specification's 15-minute revocation objective. Application-controlled token age plus immediate SQL deny state meets the objective without runtime Graph calls.

**Alternatives considered**:

- Accept the normal token lifetime: rejected because it cannot meet the 15-minute requirement.
- Configure tenant-wide token lifetime policy: rejected because it broadens tenant impact and may require licensing or policy privileges unavailable to the app team.
- Query Graph on every request: rejected for latency, availability, and least-privilege reasons.

## Decision 5: Use a Fail-Closed, Idempotent Bootstrap Seed

**Decision**: Implement a Bash wrapper with `check` and `apply` modes and a TypeScript storage CLI. The wrapper loads the selected environment profile, validates the active tenant and subscription, reads the Azure SQL logical server's Microsoft Entra administrator, confirms its object ID matches `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`, and confirms it is a tenant member user. It then ensures one direct Admin app-role assignment through Microsoft Graph and transactionally upserts the initial organization/department, both memberships, one active bootstrap `RoleAssignment`, and audit events through the storage abstraction.

**Rationale**: Microsoft documents that the Azure SQL Microsoft Entra administrator enters every user database as `dbo`/`db_owner`. Verifying the ARM administrator property is therefore a deterministic owner check. Graph and SQL cannot share a transaction, so the safe order is identity verification, idempotent Graph assignment, then SQL activation. A partial failure leaves application access denied until a rerun converges.

**Alternatives considered**:

- Infer application Admin from database ownership at request time: rejected because database authority must not imply application authority.
- Seed a shared password admin: rejected because Entra mode forbids local-password fallback.
- Terraform-only user assignment: rejected because reused SQL resources still require runtime owner verification and an auditable shared-database assignment.
- App-startup seed: rejected because every replica would need directory permissions and startup failures could affect availability.

## Decision 6: Guard Every Azure Operation with the Environment Context

**Decision**: Add `validate_azure_context` to `infra/scripts/lib/common.sh`. After loading `.env_qa_mcaps`, compare `az account show` tenant and subscription values byte-for-byte with `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, stripping Windows carriage returns. Terraform providers receive both identifiers explicitly. Seed and deployment scripts stop before discovery or mutation on mismatch.

**Rationale**: The current deployment wrapper exports the subscription to Terraform but does not verify the active Azure CLI tenant or subscription. The user has moved tenants, so implicit CLI context is unsafe.

**Alternatives considered**:

- Call `az account set` silently: rejected because it can hide an unintended login and does not prove tenant access.
- Trust Terraform provider defaults: rejected because defaults can use stale local credentials or a different subscription.

## Decision 7: Provision or Reuse Entra Resources in Shared Terraform State

**Decision**: Add an `azuread` provider pinned to `AZURE_TENANT_ID` and a shared `foundation/entra` module. It creates or accepts existing IDs for the API app/service principal, Stack A SPA, Stack B SPA, four app roles, API scope, and optional baseline role groups. App-role UUIDs remain stable. Runtime app settings contain only tenant, client, audience, scope, group identifiers, and `APP_AUTH_MODE`; no browser secret is created. The enterprise application remains tenant-only but does not require an Entra assignment before sign-in because SQL authorization remains default-deny.

**Rationale**: Both stacks depend on one tenant authorization contract, so these resources belong in shared state. Reuse inputs support the new tenant if administrators pre-create resources due to permission or policy constraints.

**Alternatives considered**:

- Portal-only setup: rejected because it is difficult to reproduce and audit.
- Bicep: rejected because the constitution mandates Terraform and Bash.
- Client secrets for SPAs: rejected because public browser clients cannot keep secrets.

## Decision 8: Preserve Simple Mode Only for Explicit Local Development

**Decision**: Introduce `APP_AUTH_MODE=simple|entra`, separate from outbound `AWR_AUTH_MODE`. In Entra mode, password login, change/reset endpoints, user password administration, and default-admin initialization are unavailable. Simple mode continues to use existing local records and never accepts Entra authorization data as fallback.

**Rationale**: `AWR_AUTH_MODE` secures calls to AWReason and must not be repurposed for application-user authentication. An explicit switch preserves current local workflows without weakening cloud behavior.

**Alternatives considered**:

- Remove simple mode entirely: rejected because the specification preserves explicitly local development.
- Automatically fall back when Entra fails: rejected because identity-provider failure must fail closed.

## Decision 9: Reuse the Existing Immutable Audit Stream

**Decision**: Record login success/denial, logout, stale token, missing/conflicting role, unmapped scope, organization/department changes, membership changes, assignment activation/revocation, and seed outcomes in `ProcessingEvents`. Use immutable tenant/object IDs for actor and subject identifiers, include organization/department IDs and a correlation ID, and exclude tokens, credentials, and raw claims.

**Rationale**: The existing shared audit table already satisfies the constitution's immutable ledger requirement and avoids another event store.

**Alternatives considered**:

- Authentication logs only in App Service: rejected because role changes and seed events must be queryable with application audit history.
- Store complete token claims: rejected because it unnecessarily retains sensitive identity data and reusable security material.

## Decision 10: Resolve the Role Hierarchy Per Resource Scope

**Decision**: Model application-wide Admin as global, Organization Admin as one-organization management, Recruiter as one organization/department, and Analytics Viewer as read-only organization or organization/department access. A user may hold several assignments. For each request, validate the job's organization/department foreign-key pair, filter assignments to that scope, and apply the highest matching authority without merging authority across organizations.

**Rationale**: A single tenant-wide role cannot represent a user who administers one organization and recruits for another. Per-resource resolution preserves least privilege while allowing legitimate multi-organization membership.

**Alternatives considered**:

- Collapse assignments to the user's highest role globally: rejected because Organization Admin would become cross-organization Admin.
- Duplicate department names as scope keys: rejected because departments are specific to an organization and names can collide.
- Permit department reparenting: rejected because it silently changes the authorization boundary for jobs, memberships, and roles.

## Decision 11: Benchmark Both API Implementations Against One Fixed Authorization Profile

**Decision**: Measure the authorization-stage elapsed time separately in release-mode Express and ASP.NET Core APIs against QA Azure SQL. Seed 10,000 users, 100 organizations, 1,000 departments, and 100,000 active assignments; exercise an identity with 10 organization memberships, 25 department memberships, and 20 active assignments. After a 60-second warm-up that primes JWKS metadata and database connection pools, run at least 20,000 protected requests from 25 concurrent clients. Each API must add less than 100 ms p95 for bearer-token validation plus scoped authorization resolution, excluding browser and network transit.

**Rationale**: A fixed dataset, representative multi-scope identity, concurrency level, warm-up, request count, measurement boundary, and per-stack report make the performance target repeatable and expose query/index regressions without conflating them with client or network latency.

**Alternatives considered**:

- Unqualified "normal load": rejected because implementations could use incomparable datasets, concurrency, and cache states.
- End-to-end browser timing: rejected because network and rendering variance would obscure authorization-stage performance.
- One combined result for both stacks: rejected because it could hide a regression in either API implementation.

## Azure Policy Discovery Result

The read-only policy-assignment query for the subscription declared in `.env_qa_mcaps` returned `403 AuthorizationFailed` for the currently authenticated Azure MCP identity. This does not change the application design, but Azure implementation and deployment are blocked until an operator authenticates to the declared tenant/subscription with at least Reader access and reruns policy discovery. No resource changes were attempted.

## Primary References

- [Authentication flows supported in MSAL](https://learn.microsoft.com/entra/msal/msal-authentication-flows)
- [Role-based access control for application developers](https://learn.microsoft.com/entra/identity-platform/custom-rbac-for-developers)
- [Manage access to an application](https://learn.microsoft.com/entra/identity/enterprise-apps/what-is-access-management)
- [Single-page application: Call a web API](https://learn.microsoft.com/entra/identity-platform/scenario-spa-call-api)
- [Add app roles and receive them in the token](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps)
- [Configure group claims and app roles in tokens](https://learn.microsoft.com/security/zero-trust/develop/configure-tokens-group-claims-app-roles)
- [Secure applications and APIs by validating claims](https://learn.microsoft.com/entra/identity-platform/claims-validation)
- [ASP.NET Core Blazor WebAssembly with Microsoft Entra groups and roles](https://learn.microsoft.com/aspnet/core/blazor/security/webassembly/microsoft-entra-id-groups-and-roles?view=aspnetcore-10.0)
- [Microsoft Entra authentication for Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview?view=azuresql)
