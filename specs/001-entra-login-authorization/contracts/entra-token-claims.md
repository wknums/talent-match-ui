# Entra Access-Token Contract

Both APIs consume the same v2 access token for the shared TalentMatch API registration. Browser clients treat the token as opaque; only the APIs validate and interpret it.

## Required Claims

| Claim | Validation |
| --- | --- |
| `ver` | Exactly `2.0` |
| `iss` | Exact configured tenant issuer: `https://login.microsoftonline.com/{tenantId}/v2.0` |
| `tid` | Exact tenant ID loaded from the active environment definition file |
| `aud` | Exact shared API application/client ID |
| `azp` | Stack A or Stack B SPA client ID allowlist |
| `oid` | Valid UUID; immutable user key within `tid` |
| `scp` | Contains delegated scope `access_as_user` |
| `roles` | Optional; when present, contains only `admin`, `organization_admin`, `recruiter`, and/or `business_panel` |
| `groups` | For group assignments, contains each group object ID needed for the authorization being evaluated |
| `iat` | No more than 15 minutes old, subject to configured clock skew |
| `nbf`, `exp` | Standard lifetime validation succeeds |

Signature validation uses tenant-specific OpenID Connect metadata and cached JWKS keys. Unknown algorithms, missing key IDs, unsigned tokens, ID tokens, and Microsoft Graph access tokens are rejected.

## Assignment Rules

After all token claims validate, an unknown configured-tenant identity may be idempotently recorded as a pending-access Entra profile before the API returns `403 assignment_missing`. This profile discovery grants no membership, default, role, or protected access. Entra Access Management mutates only profiles established by this validated sign-in path; administrator-supplied object IDs alone are not proof of tenant ownership.

### Group Source

Authorization requires all of the following:

1. One or more active SQL `RoleAssignment` records for (`tid`, `oid`).
2. Each assignment has source `group`, `delegated`, or `bootstrap` and satisfies that source's validation rules.
3. An enabled `RoleGroupMapping` for each assignment considered for the request.
4. For group assignments, each mapping role is present in the token roles.
5. Token `groups` contains each considered mapping's immutable group object ID.
6. The user has active organization membership for every scoped assignment and active department membership for every department-scoped assignment.
7. Every active organization membership has one explicit default selected from its active department memberships. The default selects initial context and is never evaluated as authority.
8. The requested job or operation has a valid organization/department pair.
9. At least one assignment applies to that exact scope. If several apply, the API uses the highest applicable role in the hierarchy: `admin` > `organization_admin` > `recruiter`/`business_panel` capabilities.

Assignments outside the requested organization or department are ignored, not combined into broader authority. A user may therefore be Organization Admin in one organization and Recruiter or Analytics Viewer in another.

### Delegated Source

Authorization requires an active SQL assignment created by an application-wide Admin or by an Organization Admin acting within the same organization. The user must have the required active organization/department memberships. No app-role or group claim is required for a delegated assignment; the validated tenant identity, delegated API scope, auditable SQL assignment, and resource scope are authoritative. This avoids runtime Microsoft Graph permissions for Organization Admins.

### Bootstrap Source

Authorization requires the Admin token role, one active global bootstrap assignment, an `oid` equal to the configured bootstrap administrator, and at least one valid organization/department membership established by the seed. A group claim is not required because the seed creates a direct user-to-app-role assignment.

## Fail-Closed Conditions

- A group/bootstrap assignment whose required role is missing: `403 role_missing`.
- Any unsupported application role, or assignments that cannot be reconciled to valid scopes: `403 role_conflict`.
- Group overage marker instead of the required mapped group: `403 scope_unmapped`; no runtime Graph fallback.
- No active SQL assignment: `403 assignment_missing`.
- Disabled mapping or revoked assignment: `403 assignment_revoked`.
- Missing required organization or department membership: `403 membership_missing`.
- Missing or invalid explicit default department: `403 membership_missing`; no partial authorization context is returned.
- A job whose department does not belong to its organization: `403 invalid_job_scope` and no job data is returned.
- Token older than 15 minutes: `401 token_stale`; clients silently acquire a fresh token and retry once.
- Wrong issuer, tenant, audience, client, or delegated scope: `401` with the corresponding safe error code.

## Client Behavior

- Acquire the shared API scope silently before each API call.
- Attach the token only to configured TalentMatch API origins.
- On `token_stale`, force one silent renewal and retry the idempotent request once.
- On interaction-required errors, return to the sign-in flow without falling back to local credentials.
- Client-side role display is advisory; it never replaces API authorization.
