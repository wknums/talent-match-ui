# Quickstart: Entra Login and Authorization

This is the implementation and acceptance workflow for the feature. Commands that change Azure are not safe to run until `validate_azure_context` succeeds against the values loaded from `.env_qa_mcaps`.

## 1. Prerequisites

- Azure CLI authenticated as an identity with Reader on the declared subscription.
- Directory permissions to create/manage app registrations and enterprise-app assignments, or existing resource IDs supplied through reuse inputs.
- Ownership of the app-specific security groups, or Groups Administrator permission.
- Microsoft Entra ID P1 or P2 for group-based enterprise-application assignment.
- Terraform supported by the repository, Node.js 20+, npm, and .NET 10 SDK.
- The reused Azure SQL logical server has a Microsoft Entra administrator that is an enabled member user in the configured tenant.
- The intended bootstrap user object ID is known and equals that SQL Microsoft Entra administrator object ID.

Ordinary application users do not need Azure subscription RBAC, Azure SQL permissions, or directory administrator roles.

## 2. Complete the QA Environment Profile

Treat `.env_qa_mcaps` as authoritative. Preserve its existing tenant, subscription, location, resource group, and reused SQL coordinates, then add the feature inputs produced or consumed by Terraform:

```dotenv
APP_AUTH_MODE=entra

# Shared protected API
ENTRA_API_APP_CLIENT_ID=
ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=
ENTRA_API_IDENTIFIER_URI=
ENTRA_API_SCOPE=access_as_user

# Browser clients
ENTRA_STACK_A_CLIENT_ID=
ENTRA_STACK_B_CLIENT_ID=

# Stable API app-role IDs
ENTRA_ADMIN_APP_ROLE_ID=
ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=
ENTRA_RECRUITER_APP_ROLE_ID=
ENTRA_BUSINESS_PANEL_APP_ROLE_ID=

# Verified first administrator
ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID=
ENTRA_BOOTSTRAP_ORGANIZATION_NAME=
ENTRA_BOOTSTRAP_DEPARTMENT_NAME=
```

The IDs and scope are public configuration. Do not add a SPA client secret. If the shared Terraform module creates the resources, populate downstream runtime settings from Terraform outputs instead of manually duplicating values.

## 3. Prove Azure Context Before Discovery

```bash
source infra/scripts/lib/common.sh
load_env_file .env_qa_mcaps

az login --tenant "$AZURE_TENANT_ID"
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
validate_azure_context
```

The validator must compare both IDs exactly and remove `\r` from Azure CLI TSV output on Windows/Git Bash. It must not switch context by itself.

After context validation, list policy assignments for the selected subscription. The planning-time attempt returned `403` for the previously active identity, so implementation/deployment remains blocked until this read succeeds and policy constraints are reviewed.

## 4. Plan Tenant and App-Service Configuration

The shared Terraform root must create or reuse:

- One single-tenant API application and service principal.
- Delegated `access_as_user` scope.
- Stable Admin, Organization Admin, Recruiter, and Analytics Viewer app roles.
- Stack A and Stack B SPA registrations with environment-specific redirect/logout URIs.
- Optional app-specific security groups and group-to-app-role assignments for operator-managed access.
- Tenant, audience, client allowlist, scope, role IDs, and group-claim settings for both App Services.

Run a non-changing plan first:

```bash
./infra/scripts/deploy.sh .env_qa_mcaps test plan shared-only
```

Review the plan for the exact tenant and subscription before any apply. Reused Azure SQL resources must not be recreated or have their administrator changed by this feature.

## 5. Apply Shared Schema Changes

Implement equivalent guarded Azure SQL and SQLite schema changes plus the EF Core migration described in [data-model.md](data-model.md). Normalize organizations and organization-owned departments from existing jobs, stop on blank/ambiguous pairs, backfill job scope IDs, and enforce the parent-child foreign key. Verify that existing users are backfilled to `simple`, existing password login remains usable only in local simple mode, and no Entra membership is inferred by migration.

Required migration checks:

```bash
npm test
npm run build
dotnet test dotnet/TalentMatch.slnx
```

## 6. Bootstrap the SQL Entra Administrator

Check first:

```bash
./infra/scripts/seed-entra-admin.sh .env_qa_mcaps check
```

The check must show matching active context, SQL Microsoft Entra administrator object ID, member-user type, API Admin role ID, initial organization/department state, both memberships, Graph assignment state, and SQL assignment state without making changes.

After review, apply and prove idempotency:

```bash
./infra/scripts/seed-entra-admin.sh .env_qa_mcaps apply
./infra/scripts/seed-entra-admin.sh .env_qa_mcaps apply
./infra/scripts/seed-entra-admin.sh .env_qa_mcaps check
```

Expected result: one initial organization with at least one department, active bootstrap membership in both, one direct Admin app-role assignment, one active bootstrap SQL assignment, and auditable seed events. Database ownership or Azure RBAC alone must still produce access denied.

## 7. Map Role Groups

Use app-specific security groups with direct user membership. Create one mapping for each distinct business scope. Examples:

```bash
./infra/scripts/manage-entra-role.sh .env_qa_mcaps map-group \
  --group-object-id "$ENTRA_ORGANIZATION_ADMIN_GROUP_OBJECT_ID" \
  --role organization_admin \
  --organization-id "$CONTOSO_ORGANIZATION_ID"

./infra/scripts/manage-entra-role.sh .env_qa_mcaps map-group \
  --group-object-id "$ENTRA_RECRUITER_ENGINEERING_GROUP_OBJECT_ID" \
  --role recruiter \
  --organization-id "$CONTOSO_ORGANIZATION_ID" \
  --department-id "$CONTOSO_ENGINEERING_DEPARTMENT_ID"

./infra/scripts/manage-entra-role.sh .env_qa_mcaps map-group \
  --group-object-id "$ENTRA_ANALYTICS_GROUP_OBJECT_ID" \
  --role business_panel \
  --organization-id "$CONTOSO_ORGANIZATION_ID"
```

The command must verify each group-to-app-role assignment before enabling the SQL mapping. Group display names and nested groups are not authorization inputs.

## 8. Assign and Verify Other Users

```bash
./infra/scripts/manage-entra-role.sh .env_qa_mcaps assign \
  --user-object-id "$USER_OBJECT_ID" \
  --mapping-id "$ROLE_GROUP_MAPPING_ID"

./infra/scripts/manage-entra-role.sh .env_qa_mcaps check \
  --user-object-id "$USER_OBJECT_ID"
```

The assignment workflow adds direct group membership, verifies the group's API app role, activates required organization/department memberships, then activates one scoped SQL assignment. Repeat with another mapping to register the same user in additional organizations or departments. Unrelated valid assignments must coexist; invalid duplicates and mismatched organization/department pairs fail closed. The workflow must not grant Azure RBAC or SQL access.

## 9. Verify Organization Admin Delegation

Using the operations defined in [organization-admin.openapi.yaml](contracts/organization-admin.openapi.yaml), sign in as an Organization Admin and verify:

1. Create and rename a department in the assigned organization.
2. Register a tenant user in that organization and one or more of its departments.
3. Grant the user `organization_admin`, `recruiter`, or `business_panel` within that organization.
4. Revoke one assignment without changing the user's unrelated organization/department assignments.
5. Confirm that creating global Admin, changing another organization, reparenting a department, and selecting a foreign department are denied and audited.

These operations update application memberships and delegated assignments only. They require no Azure RBAC, SQL permission, directory role, or runtime Microsoft Graph permission.

## 10. Run Both Stacks Locally

Use local redirect URIs registered for each client and non-secret local Entra configuration. Start Stack A:

```bash
npm run dev
```

Start Stack B separately:

```bash
dotnet run --project dotnet/src/Web.Server/TalentMatch.Web.Server.csproj
```

For each stack, verify that Entra mode shows sign-in/access-denied states and does not expose password login, password reset, password management, or the shared default admin.

## 11. Acceptance Matrix

Run the same identities against both stacks:

| Identity | Expected result |
| --- | --- |
| Bootstrap SQL Entra administrator | Global Admin plus valid initial organization/department memberships |
| Organization Admin | Department and scoped-role management only in assigned organizations |
| Mapped Recruiter group member | Only mapped organization/department data and actions |
| Mapped Analytics Viewer | Read-only analytics at mapped scope |
| User with roles in two organizations | Each role applies only in its own organization/department scope |
| Job with mismatched organization/department | Rejected with `invalid_job_scope` |
| User missing parent organization membership | Rejected with `membership_missing` |
| Configured-tenant user without assignment | `403 assignment_missing` |
| Other-tenant identity | `401 wrong_tenant` |
| Token older than 15 minutes | One silent refresh; deny if refresh fails |
| Azure/SQL owner without application assignment | Access denied |

Contract tests must assert identical status, error code, role, and scope outcomes for Stack A and Stack B.

## 12. Revoke and Recover

```bash
./infra/scripts/manage-entra-role.sh .env_qa_mcaps revoke \
  --user-object-id "$USER_OBJECT_ID" \
  --assignment-id "$ROLE_ASSIGNMENT_ID"
```

The targeted SQL assignment is revoked before its group membership is removed, so the next protected request loses that scoped authority even if an older Entra token still contains role claims. Other organization/department assignments and memberships remain active. Use `check` to identify partial state and rerun the same command to converge.

For a wrong-tenant or wrong-subscription failure, do not override the validator. Authenticate to the IDs already declared in `.env_qa_mcaps`, rerun context and policy checks, then retry the non-changing command.
