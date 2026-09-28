# Feature Specification: Entra Login and Authorization

**Feature Branch**: `001-entra-login-authorization`  
**Created**: 2026-07-24  
**Status**: Draft  
**Input**: User description: "Plan and implement Entra user login, seed the Azure SQL database owner as an application admin, provide Entra-aware in-application user access management, direct all Azure work to the tenant and subscription specified in the active environment definition file, and enforce an authorization hierarchy in which users belong to one or more organizations and organization-specific departments with an explicit default department."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Sign In with Entra (Priority: P1)

As a user in the configured Microsoft Entra tenant, I can sign in with my organizational identity and receive only the application access assigned to that identity.

**Why this priority**: Production users need secure, tenant-bound authentication before any application feature can be exposed.

**Independent Test**: Configure Entra mode, sign in as one assigned user and one unassigned user from the configured tenant, and verify that only the assigned user reaches the role-appropriate application experience.

**Acceptance Scenarios**:

1. **Given** Entra mode is enabled and a user from the configured tenant has one valid application-role assignment, **When** the user completes sign-in, **Then** the application identifies the user, applies the assigned role, and opens the authorized experience.
2. **Given** a successfully authenticated user has no application-role assignment, **When** the user returns to the application, **Then** access is denied with guidance to contact an administrator and no protected data is shown.
3. **Given** a user authenticates through a tenant other than the tenant declared by the active environment definition file, **When** the sign-in response reaches the application, **Then** access is denied before any application role or protected data is resolved.
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
5. **Given** the active Azure context does not match both the tenant and subscription declared by the active environment definition file, **When** an operator starts the seed or another Azure-changing action, **Then** the action stops before changing cloud or application data.
6. **Given** the seed completes, **When** the owner signs in, **Then** the application grants admin capabilities based on the seeded application assignment rather than Azure subscription RBAC or database ownership alone.

---

### User Story 3 - Onboard and Manage Entra User Access (Priority: P1)

As an application administrator, I can use an Entra-aware access-management experience to onboard a configured-tenant user whose pending profile was established by a prior validated sign-in, assign the user's organization, departments, default department, and scoped role, and later change or revoke that access without managing passwords or editing application data manually.

**Why this priority**: Entra authentication is not operationally complete when administrators must combine portal changes with direct database edits or use password-oriented user management that cannot represent organization scope.

**Independent Test**: Enable Entra mode, complete one configured-tenant sign-in that creates a denied pending profile, onboard that profile from the application UI with one organization, two departments, one default department, and a scoped role, verify the complete access state is committed together and is identical in both stacks, then change and revoke the assignment. Repeat in simple mode and verify that only the existing password-user management experience is shown.

**Acceptance Scenarios**:

1. **Given** Entra mode is active and an application Admin opens user administration, **When** the management experience loads, **Then** the application shows Entra Access Management instead of password-user creation, password reset, or other local-credential controls.
2. **Given** a configured-tenant user's prior validated sign-in established a pending profile, **When** an Admin submits that profile's immutable object identifier, presentation details, organization, one or more departments, one default department, and a supported scoped role, **Then** the memberships, default, and role assignment are created or reactivated as one auditable operation.
3. **Given** any identity, organization, department, default, or role value is invalid or outside the acting Admin's authority, **When** onboarding is submitted, **Then** the entire operation is rejected and no partial user, membership, preference, or assignment state remains.
4. **Given** a recruiter is onboarded, **When** the assignment is completed, **Then** its organization and department scope agree with active memberships and the selected default department belongs to that organization.
5. **Given** an Analytics Viewer is onboarded, **When** the user signs in, **Then** the user starts in the selected default organization and department, can view authorized analytics, and cannot perform mutating Admin or Recruiter actions.
6. **Given** an Admin changes memberships, default department, or scoped roles for an existing Entra identity, **When** the change is saved, **Then** the resulting state remains internally consistent and unrelated organization scopes are unchanged.
7. **Given** an Admin revokes a user's final active role assignment or disables the application identity, **When** the user next attempts a protected action, **Then** access is denied and the change is auditable.
8. **Given** the same Entra identity is submitted again, **When** the onboarding operation is repeated, **Then** it converges on one application identity and the requested active access state without duplicate memberships or assignments.
9. **Given** simple authentication mode is active, **When** an Admin opens user administration, **Then** the existing local user-management experience remains available and Entra Access Management is not shown.

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
4. **Given** the same authentication mode is active in both stacks, **When** an Admin opens user administration, **Then** both stacks expose the same mode-appropriate management capability and enforce the same onboarding constraints.

---

### User Story 6 - Expand the Stack B Workspace (Priority: P2)

As a Stack B user, I can collapse and expand the left navigation menu from every application screen so I can devote more horizontal space to my current work, especially on dense three-panel screens.

**Why this priority**: Persistent navigation consumes valuable workspace width on information-dense screens, while a screen-specific control would create inconsistent navigation behavior and make the menu harder to recover.

**Independent Test**: Visit every Stack B application screen at supported desktop and compact viewport sizes, collapse the left navigation, navigate among screens including a three-panel view, verify that content uses the released width and the state remains consistent, then restore the navigation using pointer and keyboard input.

**Acceptance Scenarios**:

1. **Given** a user is on any Stack B screen that uses the application shell, **When** the screen loads, **Then** a consistently positioned control is available to collapse or expand the left navigation.
2. **Given** the left navigation is expanded, **When** the user collapses it, **Then** the navigation no longer consumes its expanded layout width, the current screen reflows into the available space, and a visible control remains available to restore it.
3. **Given** the left navigation is collapsed, **When** the user navigates to another Stack B screen, **Then** the navigation remains collapsed and the expand control remains in the same predictable location.
4. **Given** the user expands the navigation, **When** the transition completes, **Then** all role-authorized navigation destinations and labels are available without changing the current screen or losing unsaved work.
5. **Given** a user is working in a three-panel view, **When** the navigation is collapsed, **Then** the released width is available to the three work panels without panel overlap, hidden controls, or unreadable content.
6. **Given** a user operates with a keyboard or assistive technology, **When** focus reaches the navigation control, **Then** its current state and action are announced and it can be activated without a pointer.
7. **Given** a compact viewport uses an overlay navigation pattern, **When** the user opens or closes the navigation, **Then** it does not permanently reserve content width, obscure the restore control, or conflict with the global collapse/expand state.
8. **Given** an authenticated user requests a navigation collapse or expansion, **When** the audit outcome is recorded successfully, **Then** the requested state is applied with the same correlation identifier; if the audit outcome cannot be recorded, the prior state remains active and a visible error notification is shown.

### Edge Cases

- The user completes authentication but omits required identity, tenant, or audience evidence; access is denied.
- A user's email or display name changes; the same immutable tenant identity retains the intended assignment.
- An Admin enters a valid-looking object identifier for an identity that has not completed a configured-tenant sign-in; onboarding is rejected because administrator-supplied identifiers alone do not prove tenant ownership and cannot stage a profile.
- An Admin attempts to onboard an identity from another tenant or uses an email address in place of the immutable object identifier; the operation is rejected.
- The same Entra identity is onboarded under a changed email address or display name; presentation data is updated without creating another application identity.
- Two tenants contain users with the same email address; only the identity from the configured tenant can be authorized.
- The database owner is a service principal, managed identity, SQL-only principal, guest identity, or group rather than an interactive tenant user; the sample user seed refuses to treat it as a login-capable admin.
- A recruiter assignment omits organization or department scope; the assignment is rejected rather than granting broad access.
- A selected default department is not among the user's active department memberships in the selected organization; the operation is rejected.
- A user's default department is retired or its membership is revoked; a valid replacement must be selected before the change can complete unless the entire organization membership is being revoked.
- Concurrent onboarding requests target the same tenant and object identifiers; they converge without duplicate identities, memberships, preferences, or assignments.
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
- The Stack B navigation is collapsed and the user follows a deep link, refreshes the current screen, encounters an error screen, or navigates with browser history; the expand control remains available and the screen remains usable.
- The navigation-audit operation fails or times out; the current navigation state remains unchanged, no browser-session preference is overwritten, and the user receives a visible retryable error notification.
- The user's role changes while the Stack B navigation is collapsed; expanding it shows only currently authorized destinations and does not restore stale links.
- A long navigation label, browser zoom, translated text, or narrow viewport cannot fit in the expanded menu; the menu remains operable without overlapping page content or losing the collapse control.
- Motion reduction is requested; collapsing and expanding the Stack B navigation does not depend on animation to communicate state or delay interaction.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST support a configured Entra authentication mode for organizational user sign-in.
- **FR-002**: Entra mode MUST accept identities only from the tenant declared by the active environment definition file and MUST validate that each sign-in is intended for this application.
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
- **FR-023**: Before any feature-related Azure discovery or change, operator workflows MUST verify that both the active tenant and subscription match the values declared by the active environment definition file; a mismatch MUST stop the operation before any change.
- **FR-024**: Configuration examples and documentation MUST use the tenant and subscription from the active environment definition file as the authoritative target and MUST not silently fall back to another Azure context.
- **FR-025**: The system MUST record auditable events for successful sign-in, denied sign-in, sign-out, role resolution failure, organization or department changes, membership changes, role assignment or revocation, and each seed attempt without recording credentials or reusable sign-in artifacts.
- **FR-026**: Role and membership changes and identity disablement MUST take effect no later than the next authorization refresh and within 15 minutes for an otherwise active session.
- **FR-027**: Operator documentation MUST explain prerequisites, role hierarchy and scopes, organization and department membership, first-admin seeding, assignment to other users, Organization Admin delegation, verification, role changes, revocation, wrong-tenant recovery, conflict resolution, and rollback.
- **FR-028**: The role-assignment guide MUST document both operator-managed Entra group assignment and application-managed organization-scoped delegation, and MUST distinguish Entra group membership, application roles, application organization/department membership, Azure RBAC, and Azure SQL database permissions.
- **FR-029**: The documented least-privilege path MUST not require an ordinary application user or Organization Admin to receive subscription-level privileges, database-owner rights, or directory-wide read permissions.
- **FR-030**: Sign-in and access-denied experiences MUST provide a clear next action without exposing internal authorization details or information about other users.
- **FR-031**: When Entra mode is active, both stacks MUST replace the local password-oriented User Management entry point with an Entra Access Management experience; local password creation, password reset, and password status controls MUST NOT be displayed or callable from that experience.
- **FR-032**: When simple mode is active, both stacks MUST retain the existing local User Management experience and MUST NOT expose Entra Access Management operations.
- **FR-033**: Entra Access Management MUST allow an application Admin to identify a tenant-verified profile established by a prior configured-tenant sign-in using its immutable object identifier and manage the profile's presentation details, active state, organization memberships, department memberships, explicit default department per organization, and scoped role assignments. An administrator-supplied object identifier alone MUST NOT create or stage a profile.
- **FR-034**: The initial onboarding action MUST create or reactivate the Entra-linked application identity, one organization membership, one or more department memberships, one default-department selection, and one valid scoped role assignment as a single all-or-nothing application operation.
- **FR-035**: Each active organization membership MUST have exactly one explicit default department selected from that user's active department memberships in the same organization; list order, creation order, names, and client-side sorting MUST NOT determine the default.
- **FR-036**: A default-department selection MUST affect only the user's initial organization/department context and MUST NOT grant access beyond active memberships and role assignments.
- **FR-037**: Entra onboarding and update operations MUST be idempotent by tenant and object identifiers, MUST preserve unrelated scopes, and MUST prevent duplicate active identities, memberships, defaults, or equivalent role assignments under repeated or concurrent requests.
- **FR-038**: Application Admins MAY manage all organization-scoped roles and memberships. Organization Admins MAY use the same Entra Access Management experience only for users, departments, defaults, and delegated roles inside their authorized organizations and MUST remain subject to FR-014.
- **FR-039**: Granting or revoking application-wide Admin authority MUST require the separately controlled platform-administration procedure; the organization-scoped onboarding operation MUST NOT silently elevate a user to application-wide Admin.
- **FR-040**: Entra Access Management MUST provide list, search, inspect, onboard, edit, disable, reactivate, and targeted role-revocation workflows with explicit loading, empty, validation, conflict, success, and failure states; asynchronous failures MUST produce visible error notifications in both stacks.
- **FR-041**: The system MUST show a confirmation summarizing the target identity and resulting organization, department, default, and role scope before an access grant, scope reduction, disablement, or final-role revocation is committed.
- **FR-042**: Every Entra Access Management mutation MUST be authorized on the server, preserve referential integrity, and append one correlated audit outcome that identifies the actor, target immutable identity, affected scopes, requested action, and success or failure without recording credentials or reusable tokens.
- **FR-043**: Stack B MUST provide one consistent collapse/expand control for the left navigation on every screen that uses the authenticated application shell; the capability MUST NOT be limited to three-panel or other selected screens.
- **FR-044**: Collapsing the Stack B navigation MUST release its expanded layout width to the current screen rather than only hiding labels or making the navigation visually transparent.
- **FR-045**: A visible expand control MUST remain available whenever the Stack B navigation is collapsed, and the user MUST be able to collapse or expand it without changing screens, losing entered data, or restarting the current task.
- **FR-046**: The Stack B navigation state MUST remain consistent while the user navigates among application screens in the same browser session, including deep-linked, loading, empty, error, and access-denied screens that use the application shell.
- **FR-047**: The Stack B collapse/expand control MUST expose its current state and action to assistive technology, support keyboard operation and visible focus, and remain usable at supported zoom and viewport sizes.
- **FR-048**: Stack B screens MUST reflow after a navigation-state change without incoherent overlap, clipped primary controls, unreadable panels, or avoidable horizontal page scrolling; dense three-panel screens MUST allocate the released width across their work area.
- **FR-049**: Compact-screen overlay navigation and desktop width-releasing navigation MUST present the same recognizable control and state meaning without allowing one behavior to leave the other inaccessible or contradictory after a viewport change.
- **FR-050**: Navigation collapse or expansion MUST NOT change authorization, expose unauthorized destinations, alter the active route, or discard unsaved user input.
- **FR-051**: Each authenticated user-triggered Stack B navigation collapse or expansion MUST append one immutable audit outcome containing the actor, action, correlation identifier, and timestamp before changing or persisting navigation state. If audit persistence fails, the navigation state and stored preference MUST remain unchanged and the UI MUST show a visible error notification.

### Scope Boundaries

**In scope**:

- Tenant-bound Entra sign-in and sign-out for Stack A and Stack B.
- Application-role resolution and enforcement for application-wide Admin, Organization Admin, Recruiter, and Analytics Viewer behavior.
- Organization-scoped departments, user memberships, role assignments, and job authorization boundaries.
- Mode-specific user administration that replaces local password management with Entra Access Management in Entra mode.
- Transactional onboarding and lifecycle management for Entra-linked application identities, memberships, explicit default departments, and scoped assignments.
- A consistent Stack B left-navigation collapse/expand capability across all application-shell screens, with additional workspace benefit for dense multi-panel views.
- A sample, idempotent bootstrap seed for one explicitly designated Azure SQL database-owner user.
- Operator documentation for assigning, validating, changing, and revoking other users' roles.
- Preservation of simple authentication only for explicitly configured local-development use.

**Out of scope**:

- Granting or changing Azure SQL ownership, Azure subscription RBAC, or tenant administrator roles.
- Provisioning users, guest invitations, licenses, conditional-access policy, or multi-factor authentication policy.
- Treating the in-application access-management experience as an Entra directory editor; target identities must already exist in the configured tenant.
- Supporting consumer identities, personal Microsoft accounts, or users from arbitrary tenants.
- Building a general-purpose identity governance or access-review product.
- Changing job content or workflows beyond requiring a valid organization and department scope.
- Redesigning Stack A navigation or changing the information architecture of either stack.

### Key Entities

- **Application Identity**: A tenant-bound user represented by immutable tenant and object identifiers, with display attributes used only for presentation and audit context.
- **Organization**: A top-level application authorization boundary that owns one or more departments, jobs, user memberships, and organization-scoped role assignments.
- **Department**: A unit belonging to exactly one organization; department identity is meaningful only with its parent organization.
- **Organization Membership**: The association registering a user with an organization.
- **Department Membership**: The association registering a user with a department in one of the user's organizations.
- **Default Department Selection**: The single active department membership chosen as the user's initial context within one organization; it is a navigation preference and never an independent authorization grant.
- **Application Role**: One of admin, organization_admin, recruiter, or business_panel (Analytics Viewer), defining capabilities at the application, organization, or department level.
- **Role Assignment**: The association between one application identity and one role at a specific application, organization, or organization/department scope, including status, source, and audit timestamps.
- **Role Group Mapping**: The association between a tenant-local access group and one application role used to authorize users other than the bootstrap administrator.
- **Job Scope**: The required pairing of one organization and one department owned by that organization for an advertised job.
- **Admin Seed Definition**: The expected tenant identity and database-owner designation for the one user eligible for sample bootstrap seeding.
- **Authorization Audit Event**: A record of authentication, denial, assignment, revocation, conflict, or seed outcomes with actor, subject, time, result, and correlation context.

### Dependencies

- A tenant-local application registration and corresponding enterprise application exist or can be created in the tenant declared by the active environment definition file.
- The Azure database defined in the active environment definition file is reachable by the deployment operator and has one clearly designated owner identity.
- Operators can create or manage tenant-local role groups and can read the immutable identifiers needed to configure the application and bootstrap user.
- Both application stacks can receive the same environment-specific identity and role-mapping configuration.
- Target users can complete one configured-tenant sign-in to establish a denied pending profile before an administrator grants access; the application runtime does not require broad directory-read privileges.

### Assumptions

- The active environment definition file is the authoritative source for the tenant and subscription; values in older files or active command-line contexts are not trusted implicitly.
- The designated database owner is a member user in the configured tenant who can complete interactive sign-in. If the actual database owner is not such a user, a separate explicitly approved tenant user must be designated before the seed can succeed.
- Group-based assignment remains available to platform operators; Organization Admins use application-managed, audited assignments that do not require Azure, SQL, directory-administrator, or Microsoft Graph privileges.
- In-application onboarding manages TalentMatch authorization state only. It does not create, invite, enable, disable, or delete the underlying Entra directory identity.
- An administrator selects or supplies the immutable object identifier of an existing tenant-verified pending profile during onboarding; later validated sign-ins may refresh presentation details but cannot alter assigned access.
- Application-wide Admin is reserved for bootstrap and explicitly delegated platform administration. Organization Admin is the standard delegated administrative role and remains organization-scoped.
- A user may hold different roles in different organizations or departments; permissions are resolved independently for the requested scope rather than collapsed into one tenant-wide role.
- The current `business_panel` runtime role corresponds to the PRD's Analytics Viewer persona and will be shown by that friendly label while retaining the existing role value.
- Existing local users and password records are not automatically converted into Entra identities.
- Access-denial and authorization audit records follow the application's existing security audit retention policy.
- Stack B sign-in screens that do not render the authenticated application shell do not need to display a left-navigation control; every screen within that shell does.
- The user's collapse/expand choice is retained for the current browser session; long-term cross-device preference synchronization is not required.

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
- **SC-013**: In Entra-mode acceptance testing, 100% of user-administration entry points in both stacks open Entra Access Management and expose zero password creation, password reset, or local-credential controls; in simple mode, 100% open the existing local User Management experience instead.
- **SC-014**: For 6 onboarding attempts covering each organization-scoped role, including 2 repeated and 2 concurrent submissions, each successful target has exactly one Entra-linked application identity, one requested organization membership, the requested department memberships, exactly one default department for that organization, and no duplicate equivalent active assignment.
- **SC-015**: In failure testing for invalid tenant identity, organization, department, default department, role scope, and actor authority, 100% of rejected onboarding requests leave the target's pre-request authorization state unchanged.
- **SC-016**: After the target user's configured-tenant sign-in has established a pending profile, Application Admins can complete standard Entra user onboarding in under 3 minutes using only the application UI, and Organization Admins can complete the same task for their own organization without platform-operator assistance.
- **SC-017**: In default-context testing, 100% of users with memberships in multiple departments enter each organization through its explicitly selected default department, while authorization tests show zero additional permissions caused by that default selection.
- **SC-018**: In an inventory-based acceptance test, 100% of Stack B screens using the application shell expose the same collapse/expand capability and retain the selected navigation state while moving between every tested route.
- **SC-019**: On each supported desktop viewport, collapsing the Stack B navigation makes its previously occupied width available to page content within 1 second, with zero overlapping controls, clipped primary actions, or new page-level horizontal scrolling across the tested screen inventory.
- **SC-020**: On the primary three-panel Stack B views, all three panels remain visible and usable after collapsing or expanding navigation at supported viewport sizes, and the collapsed state provides measurably more horizontal work area than the expanded state.
- **SC-021**: Keyboard and assistive-technology acceptance tests complete collapse, cross-screen navigation, and expansion with 100% successful operation, visible focus, and correctly announced expanded or collapsed state.
- **SC-022**: Across supported compact and desktop viewport transitions, the navigation and its restore control remain reachable in 100% of tested cases without route changes, lost form input, or exposure of unauthorized destinations.
- **SC-023**: In navigation-audit acceptance testing, 100% of successful authenticated collapse and expansion requests produce exactly one immutable correlated audit outcome, while 100% of simulated audit failures preserve the prior navigation state and stored preference and display a visible error notification.
