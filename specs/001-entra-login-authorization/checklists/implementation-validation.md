# Implementation Validation Checklist: Entra Login and Authorization

**Purpose**: Record execution results for every acceptance and recovery step in
[quickstart.md](../quickstart.md) (task T102), plus the automated suites required by
T060, T074, T094, T100, and T101.

**Feature**: [Entra Login and Authorization](../spec.md)
**Environment**: QA — active environment definition file `.env_qa_mcaps`
(subscription, tenant, SQL server, and database are read from that file; no values
are reproduced here).

Legend: `[x]` executed and passing · `[ ]` not yet executed · **BLOCKED** notes the
prerequisite that prevents execution.

## Automated Suites

- [x] `npm run lint` — exit 0, 0 errors, 11 pre-existing `react-hooks/exhaustive-deps` warnings.
- [x] `npm run build` — exit 0, 6805 modules transformed.
- [x] `npm run build:server` — exit 0 (full type-check of `tsconfig.server.json`).
- [x] `npm test` (Vitest) — exit 0, 24 files, 210 passed, 7 skipped, 0 unhandled errors.
- [x] Operator CLI suites — `node --test tests/unit/*.mjs` with the active environment
      file: 19 passed, 0 failed.
- [x] `dotnet build dotnet/TalentMatch.slnx -c Release` — exit 0, 0 warnings, 0 errors.
- [x] `dotnet test dotnet/TalentMatch.slnx -c Release` — 266 passed, 0 failed
      (Domain 21, Application 92, Infrastructure 21, Web 132).
- [x] Terraform — `terraform fmt -check`, `terraform validate`, and an exact-context
      non-changing plan for the Entra module and the shared live root: "No changes."
- [ ] Playwright (`npx playwright test`) — ran to completion, **32 tests skipped**.
      **BLOCKED**: `tests/e2e/stack-b-navigation.spec.ts` self-skips unless both
      `E2E_STACK_B_BASE_URL` and `E2E_STACK_B_AUTH_STATE` are supplied. The auth
      storage-state fixture requires an interactive Entra sign-in and cannot be
      produced non-interactively.

## Quickstart Sections

### 1. Prerequisites

- [x] Toolchain present: Node.js 20+, .NET 10 SDK, Terraform 1.12.1, Azure CLI.

### 2. Complete the Active Environment Definition

- [x] `.env_qa_mcaps` supplies tenant, subscription, SQL server/database/resource group,
      bootstrap admin object ID, bootstrap organization and department names, auth mode,
      and reuse coordinates. Loaded via `set -a && . <(tr -d '\r' < "$ACTIVE_ENV_FILE")`.

### 3. Prove Azure Context Before Discovery

- [x] `validate_azure_context` → `[OK] Active Azure CLI context matches the environment
      profile`, exit 0. Validator compares both IDs exactly, strips `\r`, and does not
      switch context.
- [x] Subscription policy assignment read now succeeds (the planning-time `403` is
      resolved). Reviewed constraints: management-group policies enforce
      `AzureSQL_PublicNetwork_Modify` and `AzureSQL_WithoutAzureADOnlyAuthentication_Deny`.

### 4. Plan Tenant and App-Service Configuration

- [x] `./infra/scripts/deploy.sh "$ACTIVE_ENV_FILE" test plan shared-only` → exit 0,
      **"No changes. Your infrastructure matches the configuration."** for the exact
      tenant and subscription. Reused Azure SQL resources are neither recreated nor
      re-administered.

### 5. Apply Shared Schema Changes

- [x] Guarded Azure SQL and SQLite schema changes applied and converged. The ordering
      invariant (create-only table guards cannot converge later column/constraint
      additions) is handled by `server/storage/schema-pre-batch-upgrades.sql`, which both
      stacks run before the batch loop.
- [x] `npm test`, `npm run build`, `dotnet test dotnet/TalentMatch.slnx` — all green
      (see Automated Suites).

### 6. Bootstrap the SQL Entra Administrator

- [x] `seed-entra-admin.sh check` → matching context, SQL Entra administrator object ID,
      member user type, API Admin role ID, initial organization/department state, both
      memberships, Graph assignment state, and SQL assignment state; no changes made.
- [x] `seed-entra-admin.sh apply` run twice — idempotent, second run reported no change.
- [x] Final `check` confirms one initial organization with one department, active
      bootstrap membership in both, one direct Admin app-role assignment, one active
      bootstrap SQL assignment, and seed audit events in `talentmatch.ProcessingEvents`
      (`auth.seed.applied` / `auth.seed.checked`). `authorizationVersion = 1`.

### 7. Map Role Groups

- [ ] `manage-entra-role.sh map-group` for `organization_admin`, `recruiter`, and
      `business_panel`. **BLOCKED (two prerequisites)**:
      1. The three app-specific security groups do not exist in the QA tenant, and the
         active environment file does not declare
         `ENTRA_ORGANIZATION_ADMIN_GROUP_OBJECT_ID`,
         `ENTRA_RECRUITER_ENGINEERING_GROUP_OBJECT_ID`,
         `ENTRA_ANALYTICS_GROUP_OBJECT_ID`, or the API service principal / app role IDs
         the CLI requires. Creating them needs Microsoft Graph group-write consent.
      2. The command writes to Azure SQL, which has `publicNetworkAccess: Disabled`
         enforced by policy. Operator SQL access is only possible from inside the VNet.

### 8. Assign and Verify Other Users

- [ ] `manage-entra-role.sh assign` / `check`. **BLOCKED**: depends on section 7 mappings
      and on the same SQL reachability constraint.

### 9. Verify Entra Access Management

- [ ] Pending-profile sign-in → `403 assignment_missing`; Admin configuration; idempotent
      resubmission; concurrent-equivalent convergence and stale-version `409`;
      default/role change; disable/reactivate; delegated revoke. Organization Admin
      scoping and denial cases. **BLOCKED**: manual UI walkthrough requiring interactive
      Entra sign-in as several distinct identities.
- [x] Equivalent behaviour is covered by automated contract/endpoint tests in both stacks
      (`entra-access-management.test.ts`, `entra-access-management-repository.test.ts`,
      `entra-access-management-ui.test.tsx`, `EntraAccessManagementTests.cs`,
      `EntraAccessManagementEndpointsTests.cs`).

### 10. Run Both Stacks Locally

- [ ] `npm run dev` and `dotnet run --project dotnet/src/Web.Server/...` in Entra mode,
      then explicitly in simple mode. **BLOCKED**: requires interactive Entra sign-in to
      observe the sign-in/access-denied states.
- [x] Mode gating itself (`APP_AUTH_MODE=simple|entra`) is covered by automated tests in
      both stacks.

### 11. Acceptance Matrix

- [x] Every row of the matrix is asserted by the Stack A / Stack B parity contract tests
      (`tests/integration/authorization-parity.test.ts` and the shared fixture), which
      assert identical status, error code, role, and scope outcomes for both stacks —
      including `invalid_job_scope`, `membership_missing`, `assignment_missing`,
      `wrong_tenant`, and the 15-minute token-age rule.
- [ ] Manual re-run of the matrix against live identities. **BLOCKED**: interactive
      Entra sign-in.

### 12. Verify Stack B Navigation

- [x] Points 1-8 are asserted by `NavigationShellStateTests.cs`,
      `NavigationAuditEndpointsTests.cs`, and `MainLayoutTests.cs` (bUnit), all passing.
- [ ] Cross-viewport and axe verification via Playwright. **BLOCKED**: same
      `E2E_STACK_B_AUTH_STATE` prerequisite as the Playwright suite above.

### 13. Revoke and Recover

- [ ] `manage-entra-role.sh revoke` then `check` to converge partial state.
      **BLOCKED**: depends on sections 7-8 and on SQL reachability.
- [x] Revoke ordering (SQL assignment revoked before group membership removal) and the
      structured exit codes are asserted by the operator CLI suite
      (`tests/unit/entra-role-management.test.mjs`, 19 passing).
- [x] Wrong-tenant / wrong-subscription recovery: the validator refuses to continue and
      never calls `az account set` — asserted by `tests/integration/azure-context.test.ts`.

## Outstanding Prerequisites

1. **Playwright auth fixture** — an `E2E_STACK_B_AUTH_STATE` storage-state file produced
   by an interactive Entra sign-in against the deployed Stack B.
2. **Operator role groups** — three app-specific security groups plus their
   group-to-app-role assignments, and the corresponding
   `ENTRA_*_GROUP_OBJECT_ID` / `ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID` /
   `ENTRA_*_APP_ROLE_ID` entries in the active environment definition file.
3. **Operator SQL reachability** — Azure SQL `publicNetworkAccess` is `Disabled` and the
   controlling policy silently reverts changes, so operator commands that touch SQL must
   be run from inside the VNet.
