# Entra Authorization Operations

This runbook covers least-privilege bootstrap and role operations for Entra mode. The active environment definition file is authoritative for tenant, subscription, SQL, and application identifiers.

## Safety Boundary

Every operator command validates the active Azure CLI tenant and subscription before discovery or mutation. Commands do not switch context automatically.

```bash
source infra/scripts/lib/common.sh
load_env_file "$ACTIVE_ENV_FILE"
validate_azure_context
```

If validation fails, authenticate to the IDs declared in the environment file and retry. Do not override the validator or edit the environment file to match an unintended active context.

The browser applications use public SPA registrations and have no client secret. Application access is authorized by active shared-database memberships and assignments; Azure RBAC, Azure SQL ownership, and Entra authentication alone grant no application authority.

## Least-Privilege Operator Permissions

Grant only what each activity needs, and grant it for the duration of the activity.

| Activity | Minimum permission | Do not use |
| --- | --- | --- |
| Read tenant, app, and group state | Directory Readers, or `Application.Read.All` on the operator's own account | Global Reader on a standing basis |
| Create or update the API and SPA registrations | Owner of those specific application objects, or Application Administrator scoped to the change window | Global Administrator |
| Assign or remove the API app role for a group | Owner of the API service principal | Privileged Role Administrator |
| Manage app-specific security groups | Owner of that group only | Groups Administrator |
| Set the SQL logical server Entra administrator | `Microsoft.Sql/servers/administrators/write` on that server | Subscription Owner |
| Run the bootstrap and role scripts against SQL | `db_owner` on the shared application database only | `sysadmin` or server-level roles |
| Day-to-day access changes after bootstrap | Application `admin` or `organization_admin` through Entra Access Management | Any directory role |

After bootstrap, all routine access changes must go through the in-app delegation path. Directory
and Azure roles are only for the one-time platform setup and for break-glass recovery.

## Bootstrap Admin

The bootstrap identity must be an enabled member user in the configured tenant and must exactly match the Azure SQL logical server's Microsoft Entra administrator object ID.

Run the read-only check first:

```bash
./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" check
```

The check validates:

- Exact Azure tenant and subscription context.
- Bootstrap object identity, enabled state, and member-user type.
- Azure SQL Microsoft Entra administrator object ID.
- Enabled Admin app role on the configured API service principal.
- Direct Graph app-role assignment state.
- Shared SQL profile, organization, department, memberships, explicit default, bootstrap assignment, and authorization version.

After reviewing the output, apply and prove convergence:

```bash
./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" apply
./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" apply
./infra/scripts/seed-entra-admin.sh "$ACTIVE_ENV_FILE" check
```

The SQL operation uses a serializable transaction and application lock. It converges one Entra profile, one initial organization and department, active organization and department memberships, an explicit default department, one active bootstrap Admin assignment, an authorization version, and an immutable seed audit event. Repeated or concurrent runs do not duplicate authorization records.

## Partial Bootstrap Recovery

Graph and SQL cannot share a transaction. The workflow therefore establishes the direct Graph app-role assignment before activating SQL authorization. A Graph-only partial result remains denied because runtime authorization also requires the active SQL assignment and valid memberships.

To recover:

1. Run `check` and retain its non-secret structured output.
2. Correct the reported tenant, owner, role, Graph, or SQL prerequisite.
3. Rerun `apply`; do not manually insert authorization rows.
4. Run `check` again and require both Graph and SQL readiness.

An invalid owner, disabled or guest principal, conflicting active bootstrap administrator, invalid explicit default, or failed postcondition rolls back all SQL mutations. Exit code `7` means partial state remains and requires another recovery run.

## Group-Based Role Operations

Use app-specific security groups with direct user membership:

```bash
./infra/scripts/manage-entra-role.sh "$ACTIVE_ENV_FILE" map-group \
  --group-object-id "$GROUP_OBJECT_ID" \
  --role recruiter \
  --organization-id "$ORGANIZATION_ID" \
  --department-id "$DEPARTMENT_ID"

./infra/scripts/manage-entra-role.sh "$ACTIVE_ENV_FILE" assign \
  --user-object-id "$USER_OBJECT_ID" \
  --mapping-id "$ROLE_GROUP_MAPPING_ID" \
  --default-department-id "$DEFAULT_DEPARTMENT_ID"

./infra/scripts/manage-entra-role.sh "$ACTIVE_ENV_FILE" check \
  --user-object-id "$USER_OBJECT_ID"
```

Mapping and assignment validate tenant-local immutable IDs, role scope, organization/department ownership, group type, direct membership, and the API app-role assignment before enabling SQL authority. The default department must be an active membership in the same organization and is a context preference, not an authorization grant.

## Targeted Revocation

```bash
./infra/scripts/manage-entra-role.sh "$ACTIVE_ENV_FILE" revoke \
  --user-object-id "$USER_OBJECT_ID" \
  --assignment-id "$ROLE_ASSIGNMENT_ID"
```

Revocation targets one assignment, updates SQL first, and preserves unrelated scopes. Group membership is removed only when no other active assignment depends on it. If a later Graph step fails, the SQL deny state remains effective; use `check` and rerun the same command to converge.

## In-App Delegation

Application Admins and scoped Organization Admins manage delegated organization roles through Entra Access Management. These operations require no runtime Microsoft Graph permission, directory role, Azure RBAC, or direct SQL permission. Organization Admins cannot grant global Admin, disable identities globally, or mutate another organization.

Every mutation uses optimistic authorization versions and an atomic aggregate transaction. A stale divergent request returns `409`; clients must reload before retrying. Success and failure outcomes are correlated and audited without tokens, credentials, or raw claims.

## Conflict Handling

All conflicts fail closed and produce an actionable diagnostic rather than a partial grant.

| Conflict | Symptom | Resolution |
| --- | --- | --- |
| Another active bootstrap Admin already exists | `apply` exits `4` and names the existing assignment | Confirm the intended owner, revoke the stale assignment, then rerun `apply` |
| A group maps to more than one application role | Sign-in returns `role_conflict` | Remove the extra mapping so each group maps to exactly one role and scope |
| A group mapping points at a deleted or renamed group | Sign-in returns `scope_unmapped` | Remap the role to the current group object ID, or revoke the mapping |
| An assignment's scope shape is invalid for its role | Sign-in returns `role_conflict` | Recreate the assignment with the correct organization/department shape |
| The explicit default department is inactive or in another organization | Sign-in returns `membership_missing` | Set a valid active department membership as the default and rerun `check` |
| Two administrators edit the same identity concurrently | In-app request returns `409 version_conflict` | Reload the identity, review the newer state, and resubmit |
| Token claims a role that SQL does not grant | Requests return `403` | This is expected. Token claims never grant access; add the SQL assignment if the access is intended |

## Rollback

Rollback is always "return to denied", never "leave partially granted".

- SQL mutations run inside a serializable transaction with an application lock. Any failed
  precondition or postcondition rolls back every row the run touched.
- If a Graph step fails after SQL succeeded, rerun the same command. The scripts are idempotent and
  converge without duplicating authorization records.
- If a SQL step fails after a Graph app-role assignment succeeded, access remains denied because
  runtime authorization also requires the active SQL assignment. Rerun `apply`, or run `revoke` to
  remove the Graph assignment if the change is being abandoned.
- To roll back a completed change, use `revoke` with the specific assignment ID. Do not delete rows
  by hand: manual deletes skip the audit event and the authorization version increment, which leaves
  cached authorization contexts stale.
- Exit code `7` means partial state remains. Access stays denied until a rerun converges.

## Wrong-Context Recovery

The most common operator error is running against the wrong tenant, subscription, or environment
file.

1. Stop. Do not edit the environment file to match the active CLI context.
2. Identify the mismatch:

   ```bash
   az account show --query "{tenantId:tenantId, subscriptionId:id, name:name}" -o json
   grep -E "AZURE_TENANT_ID|AZURE_SUBSCRIPTION_ID" "$ACTIVE_ENV_FILE"
   ```

3. Re-authenticate to the IDs declared in the environment file:

   ```bash
   az login --tenant "$AZURE_TENANT_ID"
   az account set --subscription "$AZURE_SUBSCRIPTION_ID"
   validate_azure_context
   ```

4. Run `check` before any further `apply`. Exit code `3` means the context is still wrong.
5. If a mutation already ran against the wrong environment, run `check` there to capture what
   changed, `revoke` the assignments that were created, and confirm the audit trail in both
   environments before retrying.

On Windows with Git Bash, export `MSYS_NO_PATHCONV=1` before running these scripts so ARM resource
IDs beginning with `/subscriptions/` are not rewritten into local paths.

## Exit Codes

| Code | Meaning |
| --- | --- |
| `0` | Requested state is converged |
| `2` | Invalid arguments or missing configuration |
| `3` | Tenant or subscription mismatch |
| `4` | Principal, owner, role, or mapping validation failed |
| `5` | Microsoft Graph operation failed |
| `6` | Shared SQL operation failed |
| `7` | Partial state remains; rerun recovery while access stays denied |

Operator output is structured and non-secret. Never add tokens, credentials, SPA secrets, raw claims, or environment files to incident notes.