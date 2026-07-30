# Feature Specification: Entra Login and Authorization

**Feature Branch**: `001-entra-login-authorization`  
**Created**: 2026-07-24  
**Status**: Draft  
**Input**: User description: "Plan and implement Entra user login, seed the Azure SQL database owner as an application admin, document role assignment for other users, direct all Azure work to the tenant and subscription specified in `.env_qa_mcaps`, and enforce an authorization hierarchy in which users belong to one or more organizations and organization-specific departments."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sign In with Entra (Priority: P1)

As a user in the configured Microsoft Entra tenant, I can sign in with my organizational identity and receive only the application access assigned to that identity.

**Why this priority**: Production users need secure, tenant-bound authentication before any application feature can be exposed.

**Independent Test**: Configure Entra mode, sign in as one assigned user and one unassigned user from the configured tenant, and verify that only the assigned user reaches the role-appropriate application experience.

**Acceptance Scenarios**:

1. **Given** Entra mode is enabled and a user from the configured tenant has one valid application-role assignment, **When** the user completes sign-in, **Then** the application identifies the user, applies the assigned role, and opens the authorized experience.
2. **Given** a successfully authenticated user has no application-role assignment, **When** the user returns to the application, **Then** access is denied with guidance to contact an administrator and no protected data is shown.
3. **Given** a user authenticates through a tenant other than the tenant declared by the QA environment profile, **When** the sign-in response reaches the application, **Then** access is denied before any application role or protected data is resolved.
4. **Given** Entra mode is enabled, **When** a user visits a protected route without a valid sign-in session, **Then** the user is required to sign in and the protected operation does not run.
5. **Given** a signed-in user's role assignment is removed or the identity is disabled, **When** authorization is next evaluated, **Then** access is revoked without creating a local password fallback.

---

### User Story 2 - Bootstrap Database Owner as Admin (Priority: P1)

As a deployment operator, I can run a sample authorization seed that grants the explicitly designated, tenant-local Azure SQL database owner the application `admin` role so the first administrator can access the application.

**Why this priority**: A secure initial administrator is required to validate the deployment and manage subsequent access without retaining shared default credentials.

**Independent Test**: Run the seed twice against an empty authorization store, verify that exactly one active admin assignment exists for the designated database owner, and verify that this identity can sign in as an admin.

**Acceptance Scenarios**:

1. **Given** the designated database owner is an interactive Entra user in the configured tenant, **When** the sample seed runs, **Then** exactly one active application `admin` assignment is established for that user's immutable tenant identity.
2. **Given** the seed identifies the initial organization and department, **When** the sample seed runs, **Then** the bootstrap Admin is registered as an active member of both and the department belongs to that organization.
3. **Given** the owner already has the seeded admin assignment and memberships, **When** the seed runs again, **Then** it succeeds without creating a duplicate or weakening any existing assignment.
4. **Given** the supplied owner identity belongs to another tenant, is not an interactive user, or cannot be matched to the designated database owner, **When** the seed runs, **Then** it fails closed with a diagnostic and creates no role assignment.
5. **Given** the active Azure context does not match both the tenant and subscription declared by the QA environment profile, **When** an operator starts the seed or another Azure-changing action, **Then** the action stops before changing cloud or application data.
6. **Given** the seed completes, **When** the owner signs in, **Then** the application grants admin capabilities based on the seeded application assignment rather than Azure subscription RBAC or database ownership alone.

---

### User Story 3 - Assign and Revoke Other User Roles (Priority: P2)

As an application administrator, I can follow documented steps to grant, verify, change, and revoke roles for other tenant users without giving them unnecessary Azure or database privileges.

**Why this priority**: The application is usable by a team only when access administration is repeatable, least-privileged, and understandable by operators.

**Independent Test**: Follow the documentation with fresh test identities for each supported role, confirm their permitted and denied actions, then revoke access and confirm denial.

**Acceptance Scenarios**:

1. **Given** a tenant user needs application access, **When** an operator follows the role-assignment guide, **Then** the user receives one supported role without receiving Azure subscription or database-owner access.
2. **Given** a recruiter is assigned, **When** the assignment is completed, **Then** it includes at least one valid organization and department membership required to restrict that recruiter.
3. **Given** an analytics viewer is assigned, **When** the user signs in, **Then** the user can view authorized analytics and cannot perform mutating admin or recruiter actions.
4. **Given** an admin removes a user's role assignment, **When** the user next attempts a protected action, **Then** access is denied and the revocation is auditable.
5. **Given** a user receives a role assignment whose department does not belong to its organization, **When** the assignment is submitted, **Then** it is rejected and no broader access is granted.

---

### User Story 4 - Administer an Organization (Priority: P2)

As an Organization Admin, I can maintain departments and user role assignments within my organization without gaining authority over another organization or the whole application.

**Why this priority**: Organization-scoped administration allows each business unit to manage its own access and structure while preserving tenant-wide least privilege.

**Independent Test**: Assign a user as Organization Admin for one of two organizations, then verify that the user can create a department and manage organization-scoped roles only in the assigned organization.

**Acceptance Scenarios**:

1. **Given** a user is an Organization Admin for one organization, **When** the user creates, renames, or retires a department in that organization, **Then** the change succeeds and is audited.
2. **Given** an Organization Admin manages users, **When** the admin grants, changes, or revokes an organization-scoped role, **Then** only assignments within that organization can be changed.
3. **Given** an Organization Admin attempts to administer another organization or grant application-wide Admin authority, **When** the operation is evaluated, **Then** it is denied and audited.
4. **Given** a user belongs to multiple organizations or departments, **When** the user accesses a job or operation, **Then** authorization is evaluated against the role and memberships applicable to that job's organization and department.
5. **Given** a job is created or changed, **When** its organization and department are selected, **Then** the department must belong to that organization.

---

### User Story 5 - Consistent Authorization Across Both Stacks (Priority: P2)

As a product owner, I receive the same authentication and authorization result whether the user opens Stack A or Stack B in the same environment.

**Why this priority**: Different security outcomes between the two supported application stacks would create privilege gaps and unreliable operations.

**Independent Test**: Use the same set of assigned, unassigned, wrong-tenant, and revoked identities against both stacks and compare every allow or deny result.

**Acceptance Scenarios**:

1. **Given** the same tenant user and role assignment, **When** the user accesses each stack, **Then** both stacks resolve the same identity, role, scope, and permissions.
2. **Given** Entra mode is enabled, **When** a user visits either stack, **Then** local password login, password reset, and default-password administration are unavailable.
3. **Given** simple authentication mode is used for an explicitly local development environment, **When** a developer signs in, **Then** existing simple-mode behavior remains available without affecting Entra-mode authorization data.

### Edge Cases

- The user completes authentication but omits required identity, tenant, or audience evidence; access is denied.
- A user's email or display name changes; the same immutable tenant identity retains the intended assignment.
- Two tenants contain users with the same email address; only the identity from the configured tenant can be authorized.
- The database owner is a service principal, managed identity, SQL-only principal, guest identity, or group rather than an interactive tenant user; the sample user seed refuses to treat it as a login-capable admin.
- A recruiter assignment omits organization or department scope; the assignment is rejected rather than granting broad access.
- A user is assigned to a department without membership in its parent organization; the assignment is rejected.
- A user has valid memberships in multiple organizations or departments; permissions are combined only for the applicable scopes and never broadened across organizations.
- A department is moved to another organization; the operation is rejected because departments are organization-specific and cannot be reparented.
- An operator attempts to retire an organization's only active department or a department referenced by active jobs or memberships; the operation is rejected until valid replacements are assigned.
- A job references a department from a different organization; creation or update is rejected.
- An Organization Admin attempts to grant application-wide Admin or modify another organization's departments or assignments; the operation is denied and audited.
- A role group is deleted, renamed, duplicated, or mapped to multiple application roles; affected access fails closed and produces an actionable diagnostic.
- An active session outlives a role change; the next authorization refresh or protected request enforces the current assignment.
- The identity provider is unavailable; protected operations remain denied and the user receives a retryable sign-in error without application data leakage.
- Repeated or concurrent seed runs target the same owner; the result remains one active admin assignment.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST support a configured Entra authentication mode for organizational user sign-in.
- **FR-002**: Entra mode MUST accept identities only from the tenant declared by the selected environment profile and MUST validate that each sign-in is intended for this application.
- **FR-003**: The system MUST identify a user by immutable tenant and object identifiers; email, username, and display name MUST NOT be authorization keys.
- **FR-004**: Authentication and authorization MUST be enforced for both user-interface navigation and every protected server operation.
- **FR-005**: An authenticated identity with no valid application-role assignment MUST be denied access by default.
- **FR-006**: The supported application roles MUST be `admin`, `organization_admin`, `recruiter`, and `business_panel`, presented to operators as Admin, Organization Admin, Recruiter, and Analytics Viewer respectively.
- **FR-007**: The authorization hierarchy MUST place application-wide Admin above Organization Admin, and Organization Admin above organization-scoped Recruiter and Analytics Viewer permissions; hierarchy MUST be evaluated only within the assignment's organization and department scope.
- **FR-008**: Every application user, including application-wide Admin, MUST be registered in one or more organizations and in at least one department within each registered organization; every department membership MUST reference a department in that same organization.
- **FR-009**: A user MAY have memberships and role assignments in multiple organizations and multiple departments. Authorization MUST evaluate all active assignments applicable to the requested resource, use the highest permitted role within that exact scope, and MUST NOT transfer authority between organizations.
- **FR-010**: Missing, malformed, expired, disabled, out-of-scope, or irreconcilably conflicting assignments MUST fail closed.
- **FR-011**: Each organization MUST contain one or more active departments, and each department MUST belong to exactly one organization and MUST NOT be shared or moved between organizations.
- **FR-012**: Every job MUST be advertised in exactly one organization and for exactly one active department belonging to that organization; mismatched organization/department pairs MUST be rejected.
- **FR-013**: An Organization Admin MUST be authorized to administer departments and organization-scoped user roles only within each organization for which that user has an active Organization Admin assignment.
- **FR-014**: An Organization Admin MUST NOT create, grant, change, or revoke application-wide Admin access, administer another organization, or broaden an assignment beyond the admin's own organization scope.
- **FR-015**: Organization, department, membership, job-scope, and role-assignment changes MUST preserve referential integrity and MUST be denied when they would leave an organization without an active department or leave an active job or membership with an invalid scope.
- **FR-016**: Entra mode MUST disable local password login, default shared credentials, password changes, and password-reset workflows.
- **FR-017**: An explicitly configured local-development simple mode MAY retain the existing username/password workflow, but it MUST remain isolated from Entra-mode role assignments and MUST NOT be an Entra-mode fallback.
- **FR-018**: Both application stacks MUST produce equivalent authentication, role, scope, allow, deny, sign-out, and session-expiration outcomes in the same environment.
- **FR-019**: The system MUST provide a sample seed that registers one explicitly designated Azure SQL database-owner user in an initial organization and department and grants that user the application-wide `admin` role.
- **FR-020**: The seed MUST verify the owner's tenant identity and database-owner designation before creating an assignment and MUST reject non-interactive principals as user-login administrators.
- **FR-021**: The seed MUST be idempotent and safe under repeated or concurrent execution, resulting in no more than one active seeded application-wide Admin assignment for the designated owner.
- **FR-022**: Database ownership, Azure subscription RBAC, and Entra authentication MUST NOT independently grant an application role; only an explicit application-role assignment may authorize application access.
- **FR-023**: Before any feature-related Azure discovery or change, operator workflows MUST verify that both the active tenant and subscription match the values declared by `.env_qa_mcaps`; a mismatch MUST stop the operation before any change.
- **FR-024**: Configuration examples and documentation MUST use the tenant and subscription from `.env_qa_mcaps` as the authoritative QA target and MUST not silently fall back to another Azure context.
- **FR-025**: The system MUST record auditable events for successful sign-in, denied sign-in, sign-out, role resolution failure, organization or department changes, membership changes, role assignment or revocation, and each seed attempt without recording credentials or reusable sign-in artifacts.
- **FR-026**: Role and membership changes and identity disablement MUST take effect no later than the next authorization refresh and within 15 minutes for an otherwise active session.
- **FR-027**: Operator documentation MUST explain prerequisites, role hierarchy and scopes, organization and department membership, first-admin seeding, assignment to other users, Organization Admin delegation, verification, role changes, revocation, wrong-tenant recovery, conflict resolution, and rollback.
- **FR-028**: The role-assignment guide MUST document both operator-managed Entra group assignment and application-managed organization-scoped delegation, and MUST distinguish Entra group membership, application roles, application organization/department membership, Azure RBAC, and Azure SQL database permissions.
- **FR-029**: The documented least-privilege path MUST not require an ordinary application user or Organization Admin to receive subscription-level privileges, database-owner rights, or directory-wide read permissions.
- **FR-030**: Sign-in and access-denied experiences MUST provide a clear next action without exposing internal authorization details or information about other users.

### Scope Boundaries

**In scope**:

- Tenant-bound Entra sign-in and sign-out for Stack A and Stack B.
- Application-role resolution and enforcement for application-wide Admin, Organization Admin, Recruiter, and Analytics Viewer behavior.
- Organization-scoped departments, user memberships, role assignments, and job authorization boundaries.
- A sample, idempotent bootstrap seed for one explicitly designated Azure SQL database-owner user.
- Operator documentation for assigning, validating, changing, and revoking other users' roles.
- Preservation of simple authentication only for explicitly configured local-development use.

**Out of scope**:

- Granting or changing Azure SQL ownership, Azure subscription RBAC, or tenant administrator roles.
- Provisioning users, guest invitations, licenses, conditional-access policy, or multi-factor authentication policy.
- Supporting consumer identities, personal Microsoft accounts, or users from arbitrary tenants.
- Building a general-purpose identity governance or access-review product.
- Changing job content or workflows beyond requiring a valid organization and department scope.

### Key Entities

- **Application Identity**: A tenant-bound user represented by immutable tenant and object identifiers, with display attributes used only for presentation and audit context.
- **Organization**: A top-level application authorization boundary that owns one or more departments, jobs, user memberships, and organization-scoped role assignments.
- **Department**: A unit belonging to exactly one organization; department identity is meaningful only with its parent organization.
- **Organization Membership**: The association registering a user with an organization.
- **Department Membership**: The association registering a user with a department in one of the user's organizations.
- **Application Role**: One of admin, organization_admin, recruiter, or business_panel (Analytics Viewer), defining capabilities at the application, organization, or department level.
- **Role Assignment**: The association between one application identity and one role at a specific application, organization, or organization/department scope, including status, source, and audit timestamps.
- **Role Group Mapping**: The association between a tenant-local access group and one application role used to authorize users other than the bootstrap administrator.
- **Job Scope**: The required pairing of one organization and one department owned by that organization for an advertised job.
- **Admin Seed Definition**: The expected tenant identity and database-owner designation for the one user eligible for sample bootstrap seeding.
- **Authorization Audit Event**: A record of authentication, denial, assignment, revocation, conflict, or seed outcomes with actor, subject, time, result, and correlation context.

### Dependencies

- A tenant-local application registration and corresponding enterprise application exist or can be created in the tenant declared by `.env_qa_mcaps`.
- The QA Azure SQL database is reachable by the deployment operator and has one clearly designated owner identity.
- Operators can create or manage tenant-local role groups and can read the immutable identifiers needed to configure the application and bootstrap user.
- Both application stacks can receive the same environment-specific identity and role-mapping configuration.

### Assumptions

- `.env_qa_mcaps` is the authoritative source for the new tenant and subscription; values in older profiles or active command-line contexts are not trusted implicitly.
- The designated database owner is a member user in the configured tenant who can complete interactive sign-in. If the actual database owner is not such a user, a separate explicitly approved tenant user must be designated before the seed can succeed.
- Group-based assignment remains available to platform operators; Organization Admins use application-managed, audited assignments that do not require Azure, SQL, directory-administrator, or Microsoft Graph privileges.
- Application-wide Admin is reserved for bootstrap and explicitly delegated platform administration. Organization Admin is the standard delegated administrative role and remains organization-scoped.
- A user may hold different roles in different organizations or departments; permissions are resolved independently for the requested scope rather than collapsed into one tenant-wide role.
- The current `business_panel` runtime role corresponds to the PRD's Analytics Viewer persona and will be shown by that friendly label while retaining the existing role value.
- Existing local users and password records are not automatically converted into Entra identities.
- Access-denial and authorization audit records follow the application's existing security audit retention policy.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance testing, 100% of valid assigned users from the configured tenant can sign in to both stacks and reach the same role-appropriate experience.
- **SC-002**: In negative testing, 100% of wrong-tenant, unassigned, disabled, malformed, and conflicting identities are denied before protected data or actions are exposed.
- **SC-003**: Running the sample seed 10 times, including at least two concurrent attempts, results in exactly one active admin assignment for the designated database-owner user.
- **SC-004**: An operator following only the role-assignment guide can assign, verify, change, and revoke each supported role for a test user without granting Azure subscription privileges, database-owner rights, or undocumented access.
- **SC-005**: Recruiter authorization tests show zero access to jobs outside an assigned organization/department membership, Organization Admin tests show zero successful changes outside assigned organizations, and Analytics Viewer tests show zero successful mutating actions.
- **SC-006**: Role revocation or identity disablement blocks new protected actions within 15 minutes for 100% of tested active sessions.
- **SC-007**: Cross-stack parity tests produce identical allow or deny results for every supported role and every documented negative case.
- **SC-008**: Security review finds no default shared Entra-mode credentials, no local-password fallback in Entra mode, and no path where Azure RBAC or database ownership alone grants application access.
- **SC-009**: Operator acceptance testing completes first-admin seeding and one additional-user role assignment using only the documented procedures, with no undocumented intervention.
- **SC-010**: In acceptance testing, 100% of users with multiple organization or department memberships receive the expected permissions for each tested scope and zero permissions transferred between organizations.
- **SC-011**: In data validation testing, 100% of attempts to create a job with a mismatched organization/department pair, orphan a membership, move a department between organizations, or leave an organization without an active department are rejected.
- **SC-012**: Organization Admin acceptance testing creates a department and assigns an organization-scoped user role without platform-operator assistance or access outside the assigned organization.
