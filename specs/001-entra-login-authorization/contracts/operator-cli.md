# Authorization Operator CLI Contract

## Shared Safety Contract

Every command accepts an environment profile as its first argument, loads it with `infra/scripts/lib/common.sh`, and calls `validate_azure_context` before Azure or SQL discovery. The active `az` tenant and subscription must exactly match `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`. Commands never switch context silently and never log tokens or credentials.

## Bootstrap Admin Seed

```text
./infra/scripts/seed-entra-admin.sh <env-file> check|apply
```

### Required Inputs

- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `SQL_RG`
- `SQL_SERVER_NAME`
- `SQL_DATABASE_NAME`
- `ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID`
- `ENTRA_ADMIN_APP_ROLE_ID`
- `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`
- `ENTRA_BOOTSTRAP_ORGANIZATION_NAME`
- `ENTRA_BOOTSTRAP_DEPARTMENT_NAME`

### `check` Behavior

1. Validate active tenant/subscription.
2. Read the SQL logical server Microsoft Entra administrator.
3. Require its object ID to equal `ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID`.
4. Require `az ad user show` to resolve that object as an enabled tenant member user.
5. Report whether the direct Admin app-role assignment exists.
6. Report whether the initial organization, its department, and both bootstrap memberships exist and are active.
7. Report whether the matching SQL bootstrap `RoleAssignment` is active.
8. Make no changes.

### `apply` Behavior

1. Perform every `check` identity and context validation.
2. Ensure exactly one direct Admin app-role assignment on the API enterprise application using the Microsoft Graph `appRoleAssignedTo` endpoint.
3. Invoke the storage CLI to upsert the initial organization and department, Entra user profile, organization membership, department membership, and one active bootstrap assignment in a transaction.
4. Append `auth.seed.applied`; repeated converged runs append `auth.seed.checked` without duplicating organization, department, membership, or assignment rows.
5. Re-read Graph and SQL state and fail unless both are converged.

Graph success followed by SQL failure remains fail-closed because the API requires the SQL assignment. Rerunning `apply` must converge without creating a duplicate Graph or SQL assignment.

## Other User Role Management

```text
./infra/scripts/manage-entra-role.sh <env-file> map-group \
  --group-object-id <uuid> \
  --role admin|organization_admin|recruiter|business_panel \
  [--organization-id <uuid>] \
  [--department-id <uuid>]

./infra/scripts/manage-entra-role.sh <env-file> check \
  --user-object-id <uuid>

./infra/scripts/manage-entra-role.sh <env-file> assign \
  --user-object-id <uuid> \
  --mapping-id <uuid>

./infra/scripts/manage-entra-role.sh <env-file> revoke \
  --user-object-id <uuid> \
  --assignment-id <uuid>
```

### Map Group Behavior

1. Validate context and require an app-specific tenant security group.
2. Apply role scope rules: application-wide Admin forbids scope; Organization Admin requires organization and forbids department; Recruiter requires organization and department; Analytics Viewer requires organization and permits an optional department.
3. Verify every organization/department ID exists, is active, and forms a valid parent-child pair.
4. Confirm the group is assigned to the corresponding API app role.
5. Idempotently upsert and enable the SQL `RoleGroupMapping`.
6. Audit and return the mapping ID used by user assignments.

### Assign Behavior

1. Validate context and require an enabled tenant member user.
2. Load the enabled SQL mapping and verify its tenant, security group, app role, and required scope.
3. Idempotently activate the required organization membership and, when scoped, department membership.
4. Permit unrelated active assignments in other valid scopes; reject only an invalid duplicate or irreconcilable assignment for the same mapping/scope.
5. Ensure direct membership in the app-specific security group.
6. Confirm the group is assigned to the expected API app role.
7. Upsert the user profile and activate the SQL assignment.
8. Audit and re-read both planes.

### Revoke Behavior

1. Require the assignment ID so unrelated organization/department roles are retained.
2. Revoke the targeted SQL assignment and audit first, immediately denying that scoped authority.
3. Remove the user from the corresponding app-specific role group only when no other active assignment depends on it.
4. Retain memberships while another active assignment depends on them; otherwise permit a separately audited membership revocation.
5. Re-read both planes and report converged or partial state.

### Check Behavior

Report immutable user identity, all active organization/department memberships, all active SQL assignments and mapped scopes, current direct group memberships, and enterprise-application app-role assignments without changing state.

Organization Admins manage departments, memberships, and delegated organization-scoped roles through the application contract, not this Azure operator CLI. Those actions require no Azure RBAC, SQL permission, directory role, or runtime Microsoft Graph permission.

## Exit Codes

| Code | Meaning |
| --- | --- |
| `0` | Success and requested state is converged |
| `2` | Invalid arguments or missing configuration |
| `3` | Tenant/subscription context mismatch |
| `4` | Principal, owner, role, or mapping validation failed |
| `5` | Microsoft Graph operation failed |
| `6` | Shared SQL operation failed |
| `7` | Partial state remains; access is denied until rerun/recovery |

All modes emit structured, non-secret summaries suitable for operator logs. Human-readable errors include the failed phase and recovery command.
