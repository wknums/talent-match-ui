# Contract: Terraform Module and Script Interfaces

**Feature**: 006-selective-azure-deploy
**Date**: 2026-04-17

> Defines the Terraform module contracts and Bash script interfaces consumed by the selective deployment workflow.

## Shared Root Contract

Path: `infra/terraform/live/shared`

### Required Inputs

| Input | Type | Description |
|-------|------|-------------|
| `environment` | string | `dev`, `test`, or `prod` |
| `location` | string | Azure region |
| `resource_group_name` | string | Managed deployment resource group |
| `stack_target` | string | `shared-only`, `stack-a`, `stack-b`, or `both` |

### Required Outputs

| Output | Type | Description |
|--------|------|-------------|
| `app_service_plan_id` | string | Shared App Service plan ID |
| `sql_server_fqdn` | string | SQL Server endpoint |
| `sql_database_name` | string | SQL database name |
| `key_vault_uri` | string | Key Vault URI |
| `apim_gateway_url` | string | APIM gateway URL |
| `identity_stack_a_id` | string | Identity resource ID for Stack A |
| `identity_stack_b_id` | string | Identity resource ID for Stack B |

All outputs must remain stable whether the backing resource is created or reused.

---

## Foundation Module Pattern

Paths:

- `infra/terraform/modules/foundation/app-service-plan`
- `infra/terraform/modules/foundation/sql`
- `infra/terraform/modules/foundation/key-vault`
- `infra/terraform/modules/foundation/apim`
- `infra/terraform/modules/foundation/identities`
- `infra/terraform/modules/foundation/app-service`

### Standard Reuse Inputs

| Input | Type | Description |
|-------|------|-------------|
| `reuse` | bool | Whether to look up the resource instead of creating it |
| `existing_name` | string | Existing resource name when reuse is enabled |
| `existing_resource_group` | string | Existing resource group when reuse is enabled |

### Standard Output Rule

Every module must expose the same IDs, names, URIs, and hostnames regardless of whether it used a `resource` block or a `data` source.

### Ownership Rule

Modules must skip role assignments, secret creation, or policy changes when `reuse = true` and the action would alter an externally owned shared resource.

---

## Stack Root Contracts

### `infra/terraform/live/stack-a`

| Input | Type | Description |
|-------|------|-------------|
| `environment` | string | `dev`, `test`, or `prod` |
| `app_service_plan_id` | string | Shared plan output |
| `identity_id` | string | Stack A identity |
| `key_vault_uri` | string | Shared Key Vault output |
| `sql_server_fqdn` | string | Shared SQL output |
| `sql_database_name` | string | Shared SQL output |
| `apim_gateway_url` | string | Shared APIM output |

| Output | Type | Description |
|--------|------|-------------|
| `app_url` | string | Stack A public URL |
| `app_name` | string | Stack A App Service name |

### `infra/terraform/live/stack-b`

| Input | Type | Description |
|-------|------|-------------|
| `environment` | string | `dev`, `test`, or `prod` |
| `app_service_plan_id` | string | Shared plan output |
| `identity_id` | string | Stack B identity |
| `key_vault_uri` | string | Shared Key Vault output |
| `sql_server_fqdn` | string | Shared SQL output |
| `sql_database_name` | string | Shared SQL output |
| `apim_gateway_url` | string | Shared APIM output |

| Output | Type | Description |
|--------|------|-------------|
| `app_url` | string | Stack B public URL |
| `app_name` | string | Stack B App Service name |

---

## Bash Script Interfaces

### `infra/scripts/deploy.sh`

```bash
./infra/scripts/deploy.sh <env-file> <tf-environment> <action> <target>
```

| Argument | Description |
|----------|-------------|
| `env-file` | `.env_local`, `.env_qa`, or `.env_prod` |
| `tf-environment` | `dev`, `test`, or `prod` |
| `action` | `plan` or `apply` |
| `target` | `shared-only`, `stack-a`, `stack-b`, or `both` |

Responsibilities:

- Load the env file.
- Export `TF_VAR_*` inputs for reuse flags and resource coordinates.
- Execute Terraform in the correct root order.

### `infra/scripts/deprovision.sh`

```bash
./infra/scripts/deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]
```

Responsibilities:

- Require an explicit env file.
- Print `[DESTROY]` and `[PROTECTED]` summaries.
- Keep stack-only teardown separate from shared teardown.

### `infra/scripts/resolve-targets.sh`

Inputs:

- GitHub event context
- changed-path detection results
- optional manual target override

Outputs:

- `deploy_shared`
- `deploy_stack_a`
- `deploy_stack_b`
- `tf_environment`
- `env_file`

### Packaging Scripts

- `infra/scripts/package-stack-a.sh` packages the built Stack A app for App Service deployment.
- `infra/scripts/package-stack-b.sh` publishes Stack B from `dotnet/src/Web.Server` and packages the result for deployment.