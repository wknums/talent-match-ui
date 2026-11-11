# Data Model: Entra Login and Authorization

## Enumerations

### AuthenticationProvider

| Value | Meaning |
| --- | --- |
| `simple` | Existing local-development username/password identity |
| `entra` | Microsoft Entra workforce identity |

### ApplicationRole

| Stored value | Operator label | Scope |
| --- | --- | --- |
| `admin` | Admin | Application-wide management |
| `organization_admin` | Organization Admin | Management within one organization |
| `recruiter` | Recruiter | One organization and department |
| `business_panel` | Analytics Viewer | Read-only analytics within one organization or one organization/department |

### RoleAssignmentSource

| Value | Meaning |
| --- | --- |
| `group` | User is a direct member of a mapped security group assigned to an app role |
| `delegated` | Application Admin or Organization Admin grants an application-managed scoped role |
| `bootstrap` | Verified SQL Microsoft Entra administrator is directly assigned the Admin app role |

### RoleAssignmentStatus

| Value | Meaning |
| --- | --- |
| `active` | Assignment may authorize when all token and mapping checks also pass |
| `revoked` | Assignment cannot authorize |

## Entity: User

The existing shared `Users` table remains the user profile surface for both stacks. It is extended additively so existing local identities continue to work.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID/string | Yes | Existing primary key; for Entra users, use the immutable Entra object ID |
| `AuthenticationProvider` | enum | Yes | Defaults to `simple` for existing rows |
| `EntraTenantId` | UUID | Entra only | Must equal the configured tenant |
| `EntraObjectId` | UUID | Entra only | Must equal the validated token `oid` |
| `Username` | string(100) | Yes | Login key only for simple users; display data for Entra users |
| `Role` | ApplicationRole | Simple only | Existing simple-mode authority; never an Entra authorization source |
| `FullName` | string(200) | Yes | Display data, never an authorization key |
| `Email` | string(320) | No | Display/contact data, never an authorization key |
| `PasswordHash` | string(128) | Simple only | Null for Entra users |
| `PasswordResetRequired` | boolean | Simple only | Always false for Entra users |
| `IsActive` | boolean | Yes | Local application deny switch; defaults true |
| `AuthorizationVersion` | integer | Entra only | Optimistic concurrency token; starts at 0 and increments after each successful access-management mutation |
| `CreatedAt` | UTC timestamp | Yes | Immutable creation time |
| `LastLogin` | UTC timestamp | No | Updated after successful authorization resolution |

### User Indexes

- Primary key on `Id`.
- Unique filtered index on (`EntraTenantId`, `EntraObjectId`) when provider is `entra`.
- Unique filtered index on `Username` when provider is `simple`.

### User Validation

- `simple` requires `PasswordHash` and forbids Entra identifiers.
- `entra` requires both Entra identifiers, requires null `PasswordHash`, and sets `PasswordResetRequired=false`.
- Email, username, and full name changes never change identity or authorization.
- An Entra user has no tenant-wide role snapshot; active scoped assignments and validated token claims are always required.
- A tenant-validated first sign-in may create or refresh an active profile with no memberships or assignments; this is a pending-access profile and remains denied with `assignment_missing` until atomic onboarding succeeds.
- A disabled Entra profile remains discoverable to authorized administrators but always fails protected authorization with `identity_disabled`.

## Entity: Organization

Defines a top-level application authorization boundary.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key and authorization key |
| `Name` | string(200) | Yes | Unique active display name; never used as a foreign key |
| `Status` | enum | Yes | `active` or `retired` |
| `CreatedAt` | UTC timestamp | Yes | Immutable creation time |
| `UpdatedAt` | UTC timestamp | Yes | Last change |
| `UpdatedBy` | string | Yes | Actor immutable identity |

An organization is created transactionally with its first active department. An active organization must always contain at least one active department.

## Entity: Department

Defines an organization-specific unit. A department cannot be shared with or moved to another organization.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key |
| `OrganizationId` | UUID | Yes | Immutable foreign key to `Organizations.Id` |
| `Name` | string(100) | Yes | Unique among active departments in the same organization |
| `Status` | enum | Yes | `active` or `retired` |
| `CreatedAt` | UTC timestamp | Yes | Immutable creation time |
| `UpdatedAt` | UTC timestamp | Yes | Last change |
| `UpdatedBy` | string | Yes | Actor immutable identity |

## Entity: OrganizationMembership

Registers a user in an organization independently of role.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key |
| `UserId` | UUID/string | Yes | Foreign key to `Users.Id` |
| `OrganizationId` | UUID | Yes | Foreign key to active organization |
| `DefaultDepartmentMembershipId` | UUID | Active only | Composite reference to one `DepartmentMembership` for this user and organization |
| `Status` | enum | Yes | `active` or `revoked` |
| `EffectiveAt` | UTC timestamp | Yes | Activation time |
| `RevokedAt` | UTC timestamp | Revoked only | Required when revoked |
| `UpdatedBy` | string | Yes | Actor immutable identity |

At most one active membership exists per (`UserId`, `OrganizationId`). Every active organization membership must have at least one active `DepartmentMembership` for that user in the same organization and exactly one valid default pointer. The pointer is nullable in storage only for migration staging and revoked history. Activation, default replacement, and revocation enforce the active invariant transactionally.

## Entity: DepartmentMembership

Registers a user in a department under one of the user's organizations.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key |
| `UserId` | UUID/string | Yes | Foreign key to `Users.Id` |
| `OrganizationId` | UUID | Yes | Must match active organization membership and department parent |
| `DepartmentId` | UUID | Yes | Foreign key to active department |
| `Status` | enum | Yes | `active` or `revoked` |
| `EffectiveAt` | UTC timestamp | Yes | Activation time |
| `RevokedAt` | UTC timestamp | Revoked only | Required when revoked |
| `UpdatedBy` | string | Yes | Actor immutable identity |

At most one active membership exists per (`UserId`, `DepartmentId`). A composite (`DepartmentId`, `OrganizationId`) reference prevents cross-organization membership.

### Explicit Default Constraints

- Add a candidate key on `DepartmentMembership (Id, UserId, OrganizationId)`.
- Add a composite foreign key from `OrganizationMembership (DefaultDepartmentMembershipId, UserId, OrganizationId)` to that candidate key with restricted deletion.
- A service-level invariant requires the referenced department membership and its department to be active whenever the organization membership is active; ordinary Azure SQL and SQLite foreign keys cannot express current status across rows.
- Changing a default updates one organization-membership pointer and never creates a membership, assignment, or authorization.
- Revoking a non-default department membership does not change the default. Revoking the default requires a replacement in the same transaction unless the entire organization membership is revoked.

## Entity: RoleGroupMapping

Maps one tenant-local security group to an application role and business scope.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key |
| `TenantId` | UUID | Yes | Must equal configured tenant |
| `GroupObjectId` | UUID | Yes | Immutable Entra security-group object ID |
| `Role` | ApplicationRole | Yes | Must match the app role assigned to this group |
| `OrganizationId` | UUID | Conditional | Required for every role except application-wide `admin` |
| `DepartmentId` | UUID | Conditional | Required for recruiter; optional for Analytics Viewer |
| `Enabled` | boolean | Yes | Disabled mappings deny all dependent assignments |
| `CreatedAt` | UTC timestamp | Yes | Immutable creation time |
| `UpdatedAt` | UTC timestamp | Yes | Last configuration change |
| `UpdatedBy` | string | Yes | Immutable Entra actor key or deployment identity |

### RoleGroupMapping Indexes and Validation

- Unique index on (`TenantId`, `GroupObjectId`).
- `admin` requires empty organization and department and is reserved for explicitly approved platform administration.
- `organization_admin` requires an active organization and forbids department scope.
- `recruiter` requires an active organization and one active department owned by that organization.
- `business_panel` requires an active organization and permits optional department scope owned by that organization.
- Group IDs are compared as normalized UUIDs; group display names are never accepted for authorization.
- Disabling a mapping is fail-closed and does not delete assignment history.

## Entity: RoleAssignment

Represents the application's explicit authorization of one Entra user. It complements, but never replaces, the Entra app-role assignment.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `Id` | UUID | Yes | Primary key |
| `UserId` | UUID/string | Yes | Foreign key to `Users.Id` |
| `TenantId` | UUID | Yes | Must match user and configured tenant |
| `UserObjectId` | UUID | Yes | Must match user and token `oid` |
| `Role` | ApplicationRole | Yes | Must match token role and mapping role |
| `OrganizationId` | UUID | Conditional | Copied immutable scope from mapping; null only for bootstrap Admin |
| `DepartmentId` | UUID | Conditional | Copied immutable scope from mapping when present |
| `RoleGroupMappingId` | UUID | Group only | Foreign key to enabled mapping; null for delegated/bootstrap |
| `Source` | RoleAssignmentSource | Yes | `group`, `delegated`, or `bootstrap` |
| `Status` | RoleAssignmentStatus | Yes | `active` or `revoked` |
| `EffectiveAt` | UTC timestamp | Yes | Activation time |
| `RevokedAt` | UTC timestamp | Revoked only | Required when status is revoked |
| `CreatedAt` | UTC timestamp | Yes | First creation time |
| `UpdatedAt` | UTC timestamp | Yes | Last transition time |
| `UpdatedBy` | string | Yes | Actor immutable identity or deployment identity |

### RoleAssignment Indexes and Validation

- At most one active assignment per (`TenantId`, `UserObjectId`, `RoleGroupMappingId`).
- Unique idempotency key on (`TenantId`, `UserObjectId`, `Source`, `Role`, `OrganizationId`, `DepartmentId`).
- `group` requires `RoleGroupMappingId`, and role/tenant must equal the referenced mapping.
- `delegated` requires null `RoleGroupMappingId`, a non-global organization scope, required memberships, and an actor authorized to grant that role in the organization.
- Scoped group assignments require active organization membership; department-scoped assignments also require active membership in the mapped department.
- `bootstrap` requires null mapping and scope IDs, role `admin`, and the configured bootstrap object ID.
- A user cannot activate an assignment when `Users.IsActive=false`.
- Assignment rows are revoked, never deleted, so history remains auditable.

## Logical Model: EntraAccessAggregate

The aggregate is returned by access-management search/detail and mutation operations. It is assembled from persisted entities and is not a separate table.

| Field | Type | Rules |
| --- | --- | --- |
| `objectId` | UUID | Immutable target identity in the configured tenant |
| `username`, `fullName`, `email` | display fields | Presentation only; updates do not change identity |
| `isActive` | boolean | Application-wide activation state; only application Admin may change globally |
| `authorizationVersion` | integer | Required as `expectedVersion` for mutations |
| `organizations` | array | Only organization scopes visible to the actor |
| `organizations[].organizationId` | UUID | Immutable application scope |
| `organizations[].status` | enum | `active` or `revoked` |
| `organizations[].departmentIds` | UUID array | Complete active department-membership set in that organization |
| `organizations[].defaultDepartmentId` | UUID | Exactly one member of `departmentIds` while active |
| `organizations[].roleAssignments` | array | Complete delegated role set managed by the aggregate command; group/bootstrap rows are read-only |

### Logical Model: PutOrganizationAccessRequest

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `expectedVersion` | integer | Yes | `409` when stale unless the desired result already exists |
| `profile` | object | Yes | Presentation fields only; immutable tenant/object IDs come from the path/configuration |
| `membership.status` | enum | Yes | `active` or `revoked` |
| `membership.departmentIds` | UUID array | Active only | Non-empty, unique, and all owned by the path organization |
| `membership.defaultDepartmentId` | UUID | Active only | Must occur in `departmentIds` |
| `roleAssignments` | array | Yes | Complete desired delegated assignments for the path organization; excludes global Admin |

The configured tenant is never request data. One command changes one target organization only and preserves unrelated organizations, group assignments, and bootstrap assignments.

## Existing Entity: Job

Jobs are migrated from free-text scope to normalized authorization keys.

| Field | Type | Required | Rules |
| --- | --- | --- | --- |
| `OrganizationId` | UUID | Yes | Foreign key to active organization |
| `DepartmentId` | UUID | Yes | Composite foreign key with `OrganizationId`; department must belong to organization |

Existing `Organisation` and `Department` text values remain display/backfill inputs during migration only. New and updated jobs resolve and persist IDs, and invalid or ambiguous pairs are rejected.

## Computed Model: AuthorizationContext

Returned by `/api/auth/me` and attached to protected server requests after successful resolution. It is not persisted as a separate table.

| Field | Source |
| --- | --- |
| `userId` | `Users.Id` |
| `tenantId` | Validated access-token `tid` |
| `objectId` | Validated access-token `oid` |
| `username`, `fullName`, `email` | User display profile |
| `globalRole` | `admin` for a valid bootstrap/platform assignment; otherwise null; membership is still required |
| `memberships` | Active organizations and their active departments |
| `memberships[].defaultDepartmentId` | Explicit active default selected for that organization |
| `authorizations` | All active role assignments with role, organization ID, optional department ID, and source |
| `authorizationVersion` | Current optimistic version of the Entra access aggregate |
| `tokenIssuedAt` | Access-token `iat` |
| `refreshRequiredAt` | `tokenIssuedAt + 15 minutes` |

### Authorization Resolution Invariants

An Entra request is authorized only when all conditions hold:

1. Signature, issuer, audience, tenant, authorized client, delegated scope, lifetime, and maximum token age validate.
2. `oid` is present and identifies an active Entra `User` in the configured tenant.
3. If `roles` is present, it contains only supported application roles; bootstrap and group assignments have their matching token role.
4. At least one active `RoleAssignment` exists. Delegated assignments are authorized by SQL state and do not require a token role claim.
5. For each group assignment, token `groups` contains the mapped group and the mapping is enabled.
6. For `bootstrap` source, the user object ID equals the configured bootstrap administrator and the user has at least one valid organization/department membership.
7. Every scoped assignment has the required active organization and department memberships.
8. Every active organization membership has a valid explicit default pointing to one of its active department memberships; this validation never grants authority.
9. The requested job/action has a valid organization/department pair and is satisfied by an applicable assignment.
10. If multiple assignments apply, the highest role in the hierarchy is used only inside that exact scope; assignments never broaden access into another organization.

Failure of any invariant produces no partial authorization context.

## Existing Entity: ProcessingEvent

Reuse the shared immutable audit stream. Required authorization actions are:

- `auth.login.succeeded`
- `auth.login.denied`
- `auth.logout`
- `auth.token.stale`
- `auth.role.missing`
- `auth.role.conflict`
- `auth.scope.unmapped`
- `auth.membership.activated`
- `auth.membership.revoked`
- `auth.assignment.activated`
- `auth.assignment.revoked`
- `auth.access.onboarded`
- `auth.access.updated`
- `auth.access.disabled`
- `auth.access.reactivated`
- `auth.access.failed`
- `auth.organization.changed`
- `auth.department.changed`
- `auth.seed.checked`
- `auth.seed.applied`
- `auth.seed.failed`
- `navigation.sidebar.collapsed`
- `navigation.sidebar.expanded`

Access-management audit details contain actor and target tenant/object IDs, affected organization/department/default/role IDs, requested action, resulting authorization version when successful, result/reason code, and correlation ID. One success outcome is inserted in the aggregate transaction; one failure outcome is inserted after rollback. Navigation audit details contain the authenticated actor, requested collapsed/expanded state, correlation ID, timestamp, and success result; the client does not change or persist navigation state until that immutable outcome succeeds. Audit records never contain access tokens, refresh tokens, credentials, or complete raw claim sets.

## Relationships

```text
User 1 ─────── 0..* RoleAssignment
User 1 ─────── 1..* OrganizationMembership
User 1 ─────── 1..* DepartmentMembership
Organization 1 ─────── 1..* Department
Organization 1 ─────── 0..* OrganizationMembership
Department 1 ─────── 0..* DepartmentMembership
OrganizationMembership 1 ─────── 1 default DepartmentMembership (while active)
Organization/Department 1 ─────── 0..* Job
RoleGroupMapping 1 ─────── 0..* RoleAssignment (group source only)
User/Membership/RoleAssignment/RoleGroupMapping 1 ─────── 0..* ProcessingEvent (logical audit link)
```

## State Transitions

### Role Assignment

```text
absent ──activate after Entra success──> active
active ──documented revoke starts──────> revoked
revoked ──documented reassign succeeds─> active
```

- New group-sourced assignment: ensure organization/department membership, Entra group membership, and app-role assignment first, then activate the SQL assignment.
- New delegated assignment: validate the tenant-verified profile, actor authority, and memberships, then activate the audited SQL assignment without Microsoft Graph mutation.
- Group revocation: revoke the targeted SQL assignment first, then remove only the corresponding Entra group membership; retain unrelated scopes and memberships.
- Delegated revocation: revoke only the targeted SQL assignment and retain unrelated scopes and memberships.
- Role change: revoke the old assignment, complete source-specific group changes when applicable, then activate the replacement assignment.
- Partial failures remain denied. Rerunning the operation converges by idempotency key.

### Role Group Mapping

```text
enabled ──disable──> disabled
disabled ──enable──> enabled
```

Mapping disablement immediately denies all dependent assignments. Mapping scope IDs are immutable; changing scope creates a new mapping and revokes the old mapping's assignments.

### Organization and Department

- Create an organization and its first department in one transaction.
- Retire a department only when it is not the organization's last active department and no active job, membership, mapping, or assignment references it.
- Departments cannot be reparented. A replacement department is created in the target organization and references are explicitly migrated before retirement.
- Organization Admin operations require a matching active `organization_admin` assignment for the target organization and cannot create global Admin assignments.

### Entra Access Aggregate

```text
pending profile ──atomic onboarding──> active scoped access
active scoped access ──scope update──> active scoped access (version + 1)
active scoped access ──global disable──> disabled profile
disabled profile ──reactivate + valid scope──> active scoped access
active organization membership ──revoke scope──> revoked organization membership
```

- Onboard/reactivate: create or activate department memberships first, then publish the active organization membership with an explicit default and delegated assignments in the same transaction.
- Add department: leave the default unchanged unless the request explicitly selects the new department.
- Change default: validate the target membership and update one pointer.
- Remove default department: require a replacement in the same transaction unless revoking the organization membership.
- Retire department: require valid replacement defaults for every active organization membership that points to it before retirement.
- Revoke organization membership: revoke that organization's department memberships and delegated assignments together; preserve unrelated organizations and immutable history.
- Reactivate organization membership: require an explicit currently active default; never reuse a stale pointer silently.
- Repeated equivalent commands converge. A divergent stale command returns `409` and leaves the aggregate unchanged.

## Session Model: Stack B NavigationShellState

This state is browser-session presentation data and is not stored in the shared database or authorization context.

| Field | Lifetime | Rules |
| --- | --- | --- |
| `desktopCollapsed` | Current browser tab/session | Persist in `sessionStorage`; restore across routes and same-tab reloads |
| `compactOverlayOpen` | Current rendered shell | Never persist; close on route change, backdrop, Escape, or compact/desktop transition |
| `isCompact` | Current viewport | Derived from the shell breakpoint; does not change authorization or route |

The routed body remains mounted while these fields change so unsaved component input and route state are preserved.

### Navigation Audit Contract

| Field | Type | Rules |
| --- | --- | --- |
| `action` | enum | `collapse` or `expand` |
| `correlationId` | UUID | Generated once by the client and returned unchanged |
| `requestedAt` | timestamp | UTC request time |
| `actorObjectId` | UUID | Server-derived from the validated token; never accepted from request data |
| `recordedAt` | timestamp | Server timestamp for the immutable audit outcome |

The audit endpoint authenticates the actor, derives immutable actor identity from the token, and appends exactly one `navigation.sidebar.collapsed` or `navigation.sidebar.expanded` outcome. The client updates `desktopCollapsed` or `compactOverlayOpen` and writes `sessionStorage` only after a successful response with the matching correlation ID. Timeout or failure leaves state and preference unchanged and produces a visible retryable notification.

## Migration and Backfill

1. Add nullable Entra fields to `Users`; add `AuthenticationProvider`, `IsActive`, and `AuthorizationVersion` with safe defaults. Retain legacy role/department fields for simple mode only.
2. Backfill all existing rows to `AuthenticationProvider=simple` and `IsActive=true` without changing credentials or IDs.
3. Make `PasswordHash` nullable only after the provider constraint exists.
4. Replace the global username unique index with a simple-provider filtered unique index.
5. Create `Organizations`, `Departments`, `OrganizationMemberships`, `DepartmentMemberships`, `RoleGroupMappings`, and `RoleAssignments` with equivalent Azure SQL and SQLite definitions.
6. Normalize distinct existing job organization/department text pairs. Stop migration for blank or ambiguous pairs; do not guess parentage.
7. Add `OrganizationId` and `DepartmentId` to `Jobs`, backfill only validated pairs, then enforce the composite foreign key and required fields.
8. Add nullable `DefaultDepartmentMembershipId` and the department-membership candidate key. Backfill active organization memberships only when exactly one valid active department membership exists; stop readiness for zero or multiple candidates and require explicit operator selection.
9. After backfill validation, add the composite default foreign key and enforce the active aggregate invariant in both persistence implementations. Existing SQLite databases use a guarded table rebuild where required.
10. Add matching EF Core entities/migration and Stack A repositories; retain guarded SQL schema updates for shared bootstrap.
11. Do not convert existing simple users into Entra memberships automatically. A validated Entra sign-in may create a pending profile, while memberships/defaults/assignments are provisioned only by the documented aggregate or seed flows.
