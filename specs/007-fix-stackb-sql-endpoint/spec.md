# Feature Specification: Fix Stack B Azure SQL Bootstrap and Wire AWR Endpoint

**Feature Branch**: `007-fix-stackb-sql-endpoint`
**Created**: 2025-07-15
**Status**: Draft
**Input**: User description: "Fix Stack B Azure SQL Bootstrap and Wire AWR Endpoint — two runtime bugs: SQL schema bootstrap failure due to curly-brace format placeholders and missing AWR_SEQ_API_ENDPOINT environment variable in Stack B's Terraform configuration."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Complete Database Schema Creation on Azure (Priority: P1)

As a platform operator deploying Stack B to Azure, I need the database schema bootstrap to execute all SQL statements successfully so that all application tables (including those after ExtractionArtifacts, section 9 onwards) are created. Currently, the bootstrap halts partway through because the execution mechanism misinterprets curly braces in SQL default-value expressions as parameter format placeholders, causing a format exception. This means tables required for extraction, scoring, and other downstream features are never created, and Stack B is non-functional on Azure.

**Why this priority**: Without a fully-bootstrapped database, Stack B cannot persist any data for the features defined in sections 9+ of the schema. This is a hard blocker for all Azure deployments — the application is fundamentally broken without it.

**Independent Test**: Deploy Stack B to Azure against a fresh (empty) SQL database and verify all tables defined in the schema file are created without errors.

**Acceptance Scenarios**:

1. **Given** Stack B is deployed to Azure with SQL Server configured and an empty database, **When** the application starts and the background schema bootstrap runs, **Then** every table defined in the schema file is created without any exceptions.
2. **Given** the schema SQL contains default value expressions with curly braces (e.g., `DEFAULT '{}'` for JSON columns), **When** the bootstrap processes those SQL batches, **Then** the curly braces are treated as literal SQL text, not as parameter placeholders.
3. **Given** the schema bootstrap has already run against a database that has all tables present, **When** the application restarts, **Then** the bootstrap completes idempotently without errors or duplicate table creation (the existing `IF NOT EXISTS` guards are honored).
4. **Given** the schema file is missing from the deployment artifact, **When** the bootstrap attempts to run, **Then** a clear error is logged indicating the schema file was not found.

---

### User Story 2 - AWR API Connectivity from Stack B (Priority: P1)

As a platform operator deploying Stack B to Azure, I need the AWR sequential API endpoint to be configured as an environment variable so that Stack B can perform scoring, extraction, prompt generation, and health checks against the AWR service. Currently, the `AWR_SEQ_API_ENDPOINT` variable is only wired into Stack A's infrastructure configuration, so Stack B has no connectivity to the AWR API and those features silently fail or show "skipped" status.

**Why this priority**: Stack B's scoring and extraction features (core application functionality) depend on the AWR API. Without this endpoint wired in, those features are entirely non-functional in production. This is co-P1 with the schema fix because both are deploy-time blockers.

**Independent Test**: Run `terraform plan` for Stack B's live root and verify the AWR endpoint variable appears in the planned app settings; after apply, verify the Stack B health endpoint reports `awrApi` connectivity.

**Acceptance Scenarios**:

1. **Given** Stack B's infrastructure is deployed with the `AWR_SEQ_API_ENDPOINT` value provided, **When** the application runs its health check, **Then** the health endpoint reports AWR API connectivity status (not "skipped").
2. **Given** the deployment script already exports `TF_VAR_awr_seq_api_endpoint` from the environment, **When** `terraform plan` is run for Stack B, **Then** the plan shows `AWR_SEQ_API_ENDPOINT` being added to the app settings.
3. **Given** the `AWR_SEQ_API_ENDPOINT` variable is empty or not provided, **When** Stack B is deployed, **Then** no extra app setting is added for this variable (matching Stack A's conditional behavior).

---

### User Story 3 - Post-Fix Deployment Verification (Priority: P2)

As a platform operator, after applying both fixes, I need to rebuild and redeploy Stack B and then run Terraform apply to confirm the combined fix produces a fully working Stack B deployment — all schema tables created and AWR API health check passing.

**Why this priority**: While the individual fixes are P1, verifying the combined end-to-end deployment is important but depends on both fixes being implemented first.

**Independent Test**: Perform a full rebuild of Stack B, deploy the new artifact, run Terraform apply for Stack B, then verify both the database schema completeness and the health endpoint AWR connectivity.

**Acceptance Scenarios**:

1. **Given** both fixes have been applied to the codebase, **When** Stack B is rebuilt, redeployed, and Terraform apply is run, **Then** the application starts without schema bootstrap errors and the health endpoint shows `awrApi` connectivity.
2. **Given** the combined deployment is complete, **When** users access Stack B features that depend on tables from section 9+ of the schema (e.g., extraction artifacts), **Then** those features work correctly without data persistence errors.

---

### Edge Cases

- What happens when the database connection is temporarily unavailable during schema bootstrap? The existing retry mechanism (background task with `ExecuteWithSqlWarmupRetryAsync`) should handle transient connection failures.
- What happens when only some tables were created in a prior failed bootstrap attempt? The `IF NOT EXISTS` guards in the schema SQL ensure idempotent re-runs — already-existing tables are skipped, and missing tables are created.
- What happens when the schema SQL file contains unexpected encoding (e.g., BOM characters)? The existing file-read mechanism should handle standard UTF-8 encoding; non-standard encodings would surface as SQL syntax errors in the application logs.
- What happens when `AWR_SEQ_API_ENDPOINT` is set to a malformed URL? Stack B should still start, but health checks should report the AWR API as unreachable, surfacing the misconfiguration.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The schema bootstrap MUST execute all SQL batches from the schema file without throwing format-related exceptions, regardless of special characters present in the SQL (curly braces, percent signs, etc.).
- **FR-002**: The schema bootstrap MUST use a SQL execution mechanism that treats the entire SQL batch as a literal command with no parameter placeholder interpretation.
- **FR-003**: The schema bootstrap MUST preserve the existing connection-management approach (obtaining the connection from the same database context used elsewhere).
- **FR-004**: The schema bootstrap MUST NOT alter the background task execution structure (the `Task.Run` wrapper and retry logic must remain unchanged).
- **FR-005**: The schema SQL file content MUST NOT be modified as part of this fix.
- **FR-006**: Stack B's infrastructure configuration MUST include the `AWR_SEQ_API_ENDPOINT` environment variable as an app setting, following the same conditional pattern used in Stack A.
- **FR-007**: The `AWR_SEQ_API_ENDPOINT` variable MUST only be added to app settings when a non-empty value is provided (conditional inclusion matching Stack A's behavior).
- **FR-008**: Stack B's infrastructure variable definition MUST mirror Stack A's definition for `awr_seq_api_endpoint` (same type, default, and description pattern).
- **FR-009**: No changes MUST be made to the `.env_qa` file or the common deployment script (`common.sh`), as they already have the correct configuration.
- **FR-010**: The Stack B infrastructure module MUST already accept the `extra_app_settings` variable — this is a pre-existing capability that must be verified, not created.

### Assumptions

- The schema SQL file (`server/storage/schema.sql`) is syntactically correct and has been validated in a prior session. The only issue is how the application executes it, not the SQL content itself.
- The Stack B infrastructure module already defines an `extra_app_settings` input variable with a map type and merges it into app settings (verified: `modules/stack-b/variables.tf` line 77 and `modules/stack-b/main.tf` line 35).
- The `common.sh` deployment script already exports `TF_VAR_awr_seq_api_endpoint` from the `AWR_SEQ_API_ENDPOINT` environment variable (verified: line 132).
- The background task structure (lines 62-81 of Program.cs) is working correctly and must not be modified.
- The existing regex-based SQL batch splitting logic (splitting on `IF NOT EXISTS` and `CREATE INDEX` boundaries) correctly segments the schema file and does not need changes.

### Constraints

- The schema bootstrap fix is scoped to the SQL execution method only — no changes to batch-splitting logic, retry behavior, or the `EnsureSharedAzureSqlSchemaIfNeeded` method signature.
- The Terraform changes are scoped to the Stack B live root only — no changes to shared modules, Stack A configuration, or shared infrastructure roots.
- The `.env_qa` file must not be modified.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of tables defined in the schema file are present in the Azure SQL database after a fresh Stack B deployment (currently approximately 60% are created before the failure).
- **SC-002**: The schema bootstrap completes without any format-related exceptions in the application logs during startup.
- **SC-003**: The Stack B health endpoint reports AWR API connectivity status instead of "skipped" after deployment with the endpoint configured.
- **SC-004**: `terraform plan` for Stack B shows the `AWR_SEQ_API_ENDPOINT` app setting when the variable value is provided, and shows no change when the variable is empty.
- **SC-005**: Existing functionality (Stack A deployments, schema bootstrap on non-Azure environments, other Terraform stacks) remains completely unaffected by these changes.
