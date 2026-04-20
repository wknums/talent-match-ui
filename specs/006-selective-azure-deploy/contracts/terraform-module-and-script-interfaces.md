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

## Networking Foundation Module Contract (US6)

Path: `infra/terraform/modules/foundation/networking`

### Required Inputs

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `vnet_name` | string | (required) | Existing VNet name |
| `vnet_resource_group` | string | (required) | Resource group containing the VNet |
| `existing_subnet_name` | string | `""` | Existing delegated subnet name (mutually exclusive with `subnet_cidr`) |
| `subnet_cidr` | string | `""` | CIDR for new delegated subnet (mutually exclusive with `existing_subnet_name`) |
| `subnet_name` | string | `"snet-appservice-integration"` | Name for newly created subnet (used only when creating) |
| `tags` | map(string) | `{}` | Resource tags |

### Validation Rules

| Rule | Error Message |
|------|---------------|
| `vnet_name` must not be empty | "vnet_name is required for VNet data source lookup" |
| `vnet_resource_group` must not be empty | "vnet_resource_group is required for VNet data source lookup" |
| Exactly one of `existing_subnet_name` or `subnet_cidr` must be non-empty | "Specify exactly one of existing_subnet_name (reuse) or subnet_cidr (create)" |
| `subnet_cidr` prefix must be ≤26 when set | "Integration subnet CIDR must be at least /26 for App Service delegation" |

### Required Outputs

| Output | Type | Description |
|--------|------|-------------|
| `vnet_id` | string | VNet resource ID |
| `vnet_name` | string | VNet name |
| `integration_subnet_id` | string | Integration subnet resource ID (stable regardless of reuse/create mode) |
| `integration_subnet_name` | string | Integration subnet name |

### Behavioral Contract

1. **VNet**: Always looked up via `data "azurerm_virtual_network"` — never created.
2. **Subnet (reuse mode)**: When `existing_subnet_name != ""`, looked up via `data "azurerm_subnet"`.
3. **Subnet (create mode)**: When `subnet_cidr != ""`, created via `resource "azurerm_subnet"` with delegation to `Microsoft.Web/serverFarms`.
4. **Private Endpoint**: No Private Endpoint resources are created. The existing PE on the VNet is relied upon for SQL connectivity.
5. **Output stability**: `integration_subnet_id` is always set, regardless of whether the subnet was reused or created.

---

## Updated App Service Foundation Module Contract (US6)

Path: `infra/terraform/modules/foundation/app-service`

### New Inputs (added for US6)

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `virtual_network_subnet_id` | string | `null` | Integration subnet ID for VNet Integration. When set, enables outbound VNet connectivity. |
| `allowed_ips` | list(string) | `[]` | List of public IP addresses to allow. When non-empty, deny-all default is enforced. |

### Modified Resource Behavior

The `azurerm_linux_web_app` resource gains:
```hcl
virtual_network_subnet_id = var.virtual_network_subnet_id

site_config {
  vnet_route_all_enabled        = var.virtual_network_subnet_id != null ? true : null
  ip_restriction_default_action = length(var.allowed_ips) > 0 ? "Deny" : null

  dynamic "ip_restriction" {
    for_each = var.allowed_ips
    content {
      ip_address = "${ip_restriction.value}/32"
      action     = "Allow"
      priority   = 100 + ip_restriction.key
      name       = "AllowIP-${ip_restriction.key}"
    }
  }
}
```

### Backward Compatibility

- `virtual_network_subnet_id = null` (default): No VNet Integration configured — existing behavior preserved.
- `allowed_ips = []` (default): No IP restrictions configured — existing behavior preserved.
- Both defaults ensure backward compatibility with T001–T054 deployments that don't yet set networking variables.

---

## Updated Shared Root Contract (US6)

### New Inputs (added for US6)

| Input | Type | Default | Description |
|-------|------|---------|-------------|
| `reuse_vnet` | bool | `false` | Must be `true` for VNet Integration (VNet creation not supported) |
| `vnet_name` | string | `""` | Existing VNet name |
| `vnet_resource_group` | string | `""` | Existing VNet resource group |
| `existing_integration_subnet_name` | string | `""` | Existing delegated subnet name |
| `integration_subnet_cidr` | string | `""` | CIDR for new delegated subnet |
| `reuse_sql_private_endpoint` | bool | `false` | Skip PE creation when `true` |
| `allowed_ips` | list(string) | `[]` | Allowed public IPs for App Service access |

### New Outputs (added for US6)

| Output | Type | Description |
|--------|------|-------------|
| `integration_subnet_id` | string | Delegated subnet ID for App Service VNet Integration |

---

## Updated Stack Root Contracts (US6)

### New Inputs for `infra/terraform/live/stack-a` and `infra/terraform/live/stack-b`

| Input | Type | Description |
|-------|------|-------------|
| `integration_subnet_id` | string | From shared root output — delegated subnet for VNet Integration |
| `allowed_ips` | list(string) | Allowed public IPs for IP restrictions |

These flow through the stack composition module (`modules/stack-a/` or `modules/stack-b/`) to the foundation `app-service` module.

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
| `integration_subnet_id` | string | Shared networking output (US6) |
| `allowed_ips` | list(string) | Allowed public IPs (US6) |

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
| `integration_subnet_id` | string | Shared networking output (US6) |
| `allowed_ips` | list(string) | Allowed public IPs (US6) |

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
- After shared `apply`, capture `integration_subnet_id` output and export as `TF_VAR_integration_subnet_id` for stack roots.
- Pass `TF_VAR_allowed_ips` (JSON-encoded list) to both shared and stack roots.

### New `export_tf_vars` Entries (US6)

The following TF_VAR exports must be added to `common.sh`:

```bash
# VNet / Networking (US6)
export TF_VAR_reuse_vnet="$(bool_to_tf "${AZ_VNET_REUSE:-FALSE}")"
export TF_VAR_vnet_name="${AZ_VNET_NAME:-}"
export TF_VAR_vnet_resource_group="${AZ_VNET_RG:-}"
export TF_VAR_existing_integration_subnet_name="${AZ_INTEGRATION_SUBNET_NAME:-}"
export TF_VAR_integration_subnet_cidr="${AZ_INTEGRATION_SUBNET_CIDR:-}"
export TF_VAR_reuse_sql_private_endpoint="$(bool_to_tf "${AZ_SQL_PRIVATE_ENDPOINT_REUSE:-FALSE}")"

# IP Restrictions (US6)
# Convert comma-separated IPs to JSON array for Terraform list variable
if [[ -n "${AZ_ALLOWED_IPS:-}" ]]; then
  IFS=',' read -ra IP_ARRAY <<< "$AZ_ALLOWED_IPS"
  TF_IPS="["
  for i in "${!IP_ARRAY[@]}"; do
    ip=$(echo "${IP_ARRAY[$i]}" | xargs)
    [[ $i -gt 0 ]] && TF_IPS+=","
    TF_IPS+="\"$ip\""
  done
  TF_IPS+="]"
  export TF_VAR_allowed_ips="$TF_IPS"
else
  export TF_VAR_allowed_ips="[]"
fi
```

### New `validate_reuse_coordinates` Entries (US6)

```bash
if [[ "${AZ_VNET_REUSE:-FALSE}" == "TRUE" ]]; then
  [[ -z "${AZ_VNET_NAME:-}" ]]  && errors+=("AZ_VNET_NAME required when AZ_VNET_REUSE=TRUE")
  [[ -z "${AZ_VNET_RG:-}" ]]    && errors+=("AZ_VNET_RG required when AZ_VNET_REUSE=TRUE")
  # Exactly one of subnet name or CIDR must be set
  if [[ -z "${AZ_INTEGRATION_SUBNET_NAME:-}" && -z "${AZ_INTEGRATION_SUBNET_CIDR:-}" ]]; then
    errors+=("Either AZ_INTEGRATION_SUBNET_NAME or AZ_INTEGRATION_SUBNET_CIDR required when AZ_VNET_REUSE=TRUE")
  fi
  if [[ -n "${AZ_INTEGRATION_SUBNET_NAME:-}" && -n "${AZ_INTEGRATION_SUBNET_CIDR:-}" ]]; then
    errors+=("Set only one of AZ_INTEGRATION_SUBNET_NAME or AZ_INTEGRATION_SUBNET_CIDR, not both")
  fi
  # CIDR size validation
  if [[ -n "${AZ_INTEGRATION_SUBNET_CIDR:-}" ]]; then
    PREFIX_LEN="${AZ_INTEGRATION_SUBNET_CIDR##*/}"
    if [[ "$PREFIX_LEN" -gt 26 ]]; then
      errors+=("AZ_INTEGRATION_SUBNET_CIDR prefix /$PREFIX_LEN is too small; minimum is /26 for App Service delegation")
    fi
  fi
  # AZ_ALLOWED_IPS required when VNet is configured
  [[ -z "${AZ_ALLOWED_IPS:-}" ]] && errors+=("AZ_ALLOWED_IPS is required — App Services must not be deployed without IP restrictions")
fi
```

### New `deploy.sh` Shared Output Capture (US6)

After shared `apply`, add:
```bash
export TF_VAR_integration_subnet_id="$(get_terraform_output "$TF_LIVE_DIR/shared" "integration_subnet_id")"
```

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