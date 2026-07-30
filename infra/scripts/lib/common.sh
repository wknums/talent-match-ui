#!/usr/bin/env bash
# =============================================================================
# common.sh — Shared helpers for selective Azure deployment scripts
# =============================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Git Bash Azure CLI path-conversion safeguard
# ---------------------------------------------------------------------------
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL="*"

# ---------------------------------------------------------------------------
# Colours (disabled when not on a terminal)
# ---------------------------------------------------------------------------
if [[ -t 1 ]]; then
  RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
  CYAN='\033[0;36m'; NC='\033[0m'
else
  RED=''; GREEN=''; YELLOW=''; CYAN=''; NC=''
fi

# ---------------------------------------------------------------------------
# Logging helpers
# ---------------------------------------------------------------------------
log_info()    { echo -e "${CYAN}[INFO]${NC} $*"; }
log_success() { echo -e "${GREEN}[OK]${NC} $*"; }
log_warn()    { echo -e "${YELLOW}[WARN]${NC} $*"; }
log_error()   { echo -e "${RED}[ERROR]${NC} $*" >&2; }
log_fatal()   { log_error "$@"; exit 1; }

# ---------------------------------------------------------------------------
# load_env_file — Source a .env_* file, skipping comments and blank lines
# ---------------------------------------------------------------------------
load_env_file() {
  local env_file="${1:?Usage: load_env_file <path>}"
  [[ -f "$env_file" ]] || log_fatal "Environment file not found: $env_file"

  log_info "Loading environment file: $env_file"
  while IFS='=' read -r key value; do
    # Skip comments and blank lines
    [[ -z "$key" || "$key" =~ ^[[:space:]]*# ]] && continue
    # Trim whitespace
    key=$(echo "$key" | xargs)
    value=$(echo "$value" | xargs)
    # Strip surrounding quotes
    value="${value%\"}"
    value="${value#\"}"
    value="${value%\'}"
    value="${value#\'}"
    export "$key=$value"
  done < "$env_file"
}

# ---------------------------------------------------------------------------
# validate_required — Fail fast if any required env vars are missing
# ---------------------------------------------------------------------------
validate_required() {
  local missing=()
  for var in "$@"; do
    if [[ -z "${!var:-}" ]]; then
      missing+=("$var")
    fi
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    log_fatal "Missing required environment variables: ${missing[*]}"
  fi
}

# ---------------------------------------------------------------------------
# validate_azure_context — Require the active Azure CLI context to match profile
# ---------------------------------------------------------------------------
validate_azure_context() {
  validate_required "AZURE_TENANT_ID" "AZURE_SUBSCRIPTION_ID"

  local expected_tenant expected_subscription active_tenant active_subscription
  expected_tenant="$(printf '%s' "$AZURE_TENANT_ID" | tr -d '\r')"
  expected_subscription="$(printf '%s' "$AZURE_SUBSCRIPTION_ID" | tr -d '\r')"

  active_tenant="$(az account show --query tenantId --output tsv 2>/dev/null | tr -d '\r')" \
    || log_fatal "Unable to read the active Azure CLI tenant. Run az login for the target tenant first."
  active_subscription="$(az account show --query id --output tsv 2>/dev/null | tr -d '\r')" \
    || log_fatal "Unable to read the active Azure CLI subscription. Run az login for the target subscription first."

  [[ "$active_tenant" == "$expected_tenant" ]] \
    || log_fatal "Active Azure tenant does not match AZURE_TENANT_ID. Refusing to continue."
  [[ "$active_subscription" == "$expected_subscription" ]] \
    || log_fatal "Active Azure subscription does not match AZURE_SUBSCRIPTION_ID. Refusing to continue."

  log_success "Active Azure CLI context matches the environment profile"
}

# ---------------------------------------------------------------------------
# validate_enum — Check a variable's value is in an allowed set
# ---------------------------------------------------------------------------
validate_enum() {
  local var_name="$1"; shift
  local value="${!var_name:-}"
  local allowed=("$@")
  for a in "${allowed[@]}"; do
    [[ "$value" == "$a" ]] && return 0
  done
  log_fatal "$var_name='$value' is not valid. Allowed: ${allowed[*]}"
}

# ---------------------------------------------------------------------------
# export_tf_vars — Export TF_VAR_* from environment profile settings
# ---------------------------------------------------------------------------
export_tf_vars() {
  # Core variables
  export TF_VAR_tenant_id="${AZURE_TENANT_ID:?}"
  export TF_VAR_environment="${ENVIRONMENT:?}"
  export TF_VAR_location="${AZURE_LOCATION:?}"
  export TF_VAR_resource_group_name="${RESOURCE_GROUP:?}"
  export TF_VAR_project_name="${PROJECT_NAME:-talentmatch}"
  export TF_VAR_stack_target="${STACK_TARGET:-both}"

  # App Service Plan
  export TF_VAR_app_service_plan_sku="${APP_SERVICE_PLAN_SKU:-B1}"
  export TF_VAR_reuse_app_service_plan="$(bool_to_tf "${AZ_APP_SERVICE_PLAN_REUSE:-FALSE}")"
  export TF_VAR_existing_app_service_plan_name="${AZ_APP_SERVICE_PLAN_NAME:-}"
  export TF_VAR_existing_app_service_plan_rg="${AZ_APP_SERVICE_PLAN_RG:-}"

  # SQL
  export TF_VAR_reuse_sql="$(bool_to_tf "${AZ_SQL_REUSE:-FALSE}")"
  export TF_VAR_existing_sql_server_name="${SQL_SERVER_NAME:-}"
  export TF_VAR_existing_sql_database_name="${SQL_DATABASE_NAME:-}"
  export TF_VAR_existing_sql_rg="${SQL_RG:-}"
  export TF_VAR_sql_admin_login="${SQL_ADMIN_LOGIN:-sqladmin}"
  export TF_VAR_sql_admin_password="${SQL_ADMIN_PASSWORD:-}"
  export TF_VAR_sql_aad_admin_login="${SQL_AAD_ADMIN_LOGIN:-}"
  export TF_VAR_sql_aad_admin_object_id="${SQL_AAD_ADMIN_OBJECT_ID:-}"

  # Key Vault
  export TF_VAR_enable_key_vault="$(bool_to_tf "${AZ_KEY_VAULT_ENABLED:-FALSE}")"
  export TF_VAR_reuse_key_vault="$(bool_to_tf "${AZ_KEY_VAULT_REUSE:-FALSE}")"
  export TF_VAR_existing_key_vault_name="${AZ_KEY_VAULT_NAME:-}"
  export TF_VAR_existing_key_vault_rg="${AZ_KEY_VAULT_RG:-}"

  # APIM
  export TF_VAR_enable_apim="$(bool_to_tf "${AZ_APIM_ENABLED:-FALSE}")"
  export TF_VAR_reuse_apim="$(bool_to_tf "${AZ_APIM_REUSE:-FALSE}")"
  export TF_VAR_existing_apim_name="${AZ_APIM_NAME:-}"
  export TF_VAR_existing_apim_rg="${AZ_APIM_RG:-}"

  # Identities
  export TF_VAR_reuse_identities="$(bool_to_tf "${AZ_IDENTITIES_REUSE:-FALSE}")"
  export TF_VAR_existing_identity_stack_a_name="${AZ_IDENTITY_STACK_A_NAME:-}"
  export TF_VAR_existing_identity_stack_b_name="${AZ_IDENTITY_STACK_B_NAME:-}"
  export TF_VAR_existing_identities_rg="${AZ_IDENTITIES_RG:-}"

  # Microsoft Entra applications
  export TF_VAR_reuse_entra="$(bool_to_tf "${ENTRA_REUSE:-FALSE}")"
  export TF_VAR_existing_entra_api_app_client_id="${ENTRA_API_APP_CLIENT_ID:-}"
  export TF_VAR_existing_entra_api_service_principal_object_id="${ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID:-}"
  export TF_VAR_existing_entra_stack_a_client_id="${ENTRA_STACK_A_CLIENT_ID:-}"
  export TF_VAR_existing_entra_stack_b_client_id="${ENTRA_STACK_B_CLIENT_ID:-}"
  export TF_VAR_entra_api_identifier_uri="${ENTRA_API_IDENTIFIER_URI:-}"
  export TF_VAR_entra_api_scope="${ENTRA_API_SCOPE:-access_as_user}"
  export TF_VAR_entra_stack_a_redirect_uris="$(csv_to_json "${ENTRA_STACK_A_REDIRECT_URIS:-}")"
  export TF_VAR_entra_stack_b_redirect_uris="$(csv_to_json "${ENTRA_STACK_B_REDIRECT_URIS:-}")"
  export TF_VAR_entra_create_role_groups="$(bool_to_tf "${ENTRA_CREATE_ROLE_GROUPS:-FALSE}")"
  export TF_VAR_entra_bootstrap_admin_object_id="${ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID:-}"

  if [[ -n "${ENTRA_ADMIN_APP_ROLE_ID:-}" && -n "${ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID:-}" \
    && -n "${ENTRA_RECRUITER_APP_ROLE_ID:-}" && -n "${ENTRA_BUSINESS_PANEL_APP_ROLE_ID:-}" ]]; then
    export TF_VAR_entra_app_role_ids="{\"admin\":\"$ENTRA_ADMIN_APP_ROLE_ID\",\"organization_admin\":\"$ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID\",\"recruiter\":\"$ENTRA_RECRUITER_APP_ROLE_ID\",\"business_panel\":\"$ENTRA_BUSINESS_PANEL_APP_ROLE_ID\"}"
  fi


  # APIM settings
  export TF_VAR_apim_sku="${APIM_SKU:-Consumption_0}"
  export TF_VAR_apim_publisher_name="${APIM_PUBLISHER_NAME:-TalentMatch}"
  export TF_VAR_apim_publisher_email="${APIM_PUBLISHER_EMAIL:-admin@example.com}"

  # Runtime settings (non-secret, safe to export)
  export TF_VAR_storage_provider="${STORAGE_PROVIDER:-azuresql}"
  export TF_VAR_awr_auth_mode="${AWR_AUTH_MODE:-none}"
  export TF_VAR_awr_aad_audience="${AWR_AAD_AUDIENCE:-}"
  export TF_VAR_awr_api_client_id="${AWR_API_CLIENT_ID:-}"
  export TF_VAR_awr_api_app_role_value="${AWR_API_APP_ROLE_VALUE:-TalentMatch.Access}"
  export TF_VAR_awr_max_parallel="${AWR_MAX_PARALLEL:-1}"
  export TF_VAR_awr_seq_api_endpoint="${AWR_SEQ_API_ENDPOINT:-}"
  export TF_VAR_api_mode="${API_MODE:-mock}"
  export TF_VAR_database_provider="${DATABASE_PROVIDER:-sqlserver}"
  local use_key_vault_default="TRUE"
  if [[ "${ENVIRONMENT:-}" != "prod" ]]; then
    use_key_vault_default="FALSE"
  fi
  if [[ "${AZ_KEY_VAULT_ENABLED:-FALSE}" == "TRUE" ]]; then
    export TF_VAR_use_key_vault_secret_refs="$(bool_to_tf "${USE_KEY_VAULT_SECRET_REFS:-$use_key_vault_default}")"
  else
    export TF_VAR_use_key_vault_secret_refs="false"
  fi
  export TF_VAR_awr_api_key="${AWR_API_KEY:-}"

  # VNet / Networking (US6)
  export TF_VAR_reuse_vnet="$(bool_to_tf "${AZ_VNET_REUSE:-FALSE}")"
  export TF_VAR_vnet_name="${AZ_VNET_NAME:-}"
  export TF_VAR_vnet_resource_group="${AZ_VNET_RG:-}"
  export TF_VAR_existing_integration_subnet_name="${AZ_INTEGRATION_SUBNET_NAME:-}"
  export TF_VAR_integration_subnet_cidr="${AZ_INTEGRATION_SUBNET_CIDR:-}"
  export TF_VAR_reuse_sql_private_endpoint="$(bool_to_tf "${AZ_SQL_PRIVATE_ENDPOINT_REUSE:-FALSE}")"
  export TF_VAR_sql_private_endpoint_subnet_name="${AZ_SQL_PRIVATE_ENDPOINT_SUBNET_NAME:-snet-sql-private-endpoints}"
  export TF_VAR_sql_private_endpoint_subnet_cidr="${AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR:-}"

  # IP Restrictions (US6)
  # Convert comma-separated IPs to JSON array for Terraform list variable
  export TF_VAR_allowed_ips="$(csv_to_json "${AZ_ALLOWED_IPS:-}")"

  log_info "Exported TF_VAR_* variables for environment=$ENVIRONMENT"
}

# ---------------------------------------------------------------------------
# bool_to_tf — Convert TRUE/FALSE string to Terraform boolean
# ---------------------------------------------------------------------------
bool_to_tf() {
  local val="${1^^}"  # uppercase
  [[ "$val" == "TRUE" ]] && echo "true" || echo "false"
}

csv_to_json() {
  local value="${1:-}"
  local items=()
  local result="["

  [[ -z "$value" ]] && { echo "[]"; return; }
  IFS=',' read -ra items <<< "$value"
  for i in "${!items[@]}"; do
    local item
    item="$(echo "${items[$i]}" | xargs)"
    [[ $i -gt 0 ]] && result+=","
    result+="\"$item\""
  done
  result+="]"
  echo "$result"
}

# ---------------------------------------------------------------------------
# validate_reuse_coordinates — Check that reuse-enabled resources have coords
# ---------------------------------------------------------------------------
validate_reuse_coordinates() {
  local errors=()

  if [[ "${AZ_APP_SERVICE_PLAN_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_APP_SERVICE_PLAN_NAME:-}" ]] && errors+=("AZ_APP_SERVICE_PLAN_NAME required when AZ_APP_SERVICE_PLAN_REUSE=TRUE")
    [[ -z "${AZ_APP_SERVICE_PLAN_RG:-}" ]]   && errors+=("AZ_APP_SERVICE_PLAN_RG required when AZ_APP_SERVICE_PLAN_REUSE=TRUE")
  fi
  if [[ "${AZ_SQL_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${SQL_SERVER_NAME:-}" ]]   && errors+=("SQL_SERVER_NAME required when AZ_SQL_REUSE=TRUE")
    [[ -z "${SQL_DATABASE_NAME:-}" ]] && errors+=("SQL_DATABASE_NAME required when AZ_SQL_REUSE=TRUE")
    [[ -z "${SQL_RG:-}" ]]            && errors+=("SQL_RG required when AZ_SQL_REUSE=TRUE")
  fi
  if [[ "${AZ_KEY_VAULT_ENABLED:-FALSE}" == "TRUE" && "${AZ_KEY_VAULT_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_KEY_VAULT_NAME:-}" ]] && errors+=("AZ_KEY_VAULT_NAME required when AZ_KEY_VAULT_REUSE=TRUE")
    [[ -z "${AZ_KEY_VAULT_RG:-}" ]]   && errors+=("AZ_KEY_VAULT_RG required when AZ_KEY_VAULT_REUSE=TRUE")
  fi
  if [[ "${AZ_KEY_VAULT_ENABLED:-FALSE}" != "TRUE" && "${AZ_KEY_VAULT_REUSE:-FALSE}" == "TRUE" ]]; then
    errors+=("AZ_KEY_VAULT_REUSE cannot be TRUE when AZ_KEY_VAULT_ENABLED is not TRUE")
  fi
  if [[ "${AZ_APIM_ENABLED:-FALSE}" == "TRUE" && "${AZ_APIM_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_APIM_NAME:-}" ]] && errors+=("AZ_APIM_NAME required when AZ_APIM_REUSE=TRUE")
    [[ -z "${AZ_APIM_RG:-}" ]]   && errors+=("AZ_APIM_RG required when AZ_APIM_REUSE=TRUE")
  fi
  if [[ "${AZ_IDENTITIES_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_IDENTITY_STACK_A_NAME:-}" ]] && errors+=("AZ_IDENTITY_STACK_A_NAME required when AZ_IDENTITIES_REUSE=TRUE")
    [[ -z "${AZ_IDENTITY_STACK_B_NAME:-}" ]] && errors+=("AZ_IDENTITY_STACK_B_NAME required when AZ_IDENTITIES_REUSE=TRUE")
    [[ -z "${AZ_IDENTITIES_RG:-}" ]]         && errors+=("AZ_IDENTITIES_RG required when AZ_IDENTITIES_REUSE=TRUE")
  fi

  if [[ "${APP_AUTH_MODE:-entra}" == "entra" ]]; then
    [[ -z "${ENTRA_API_IDENTIFIER_URI:-}" ]] && errors+=("ENTRA_API_IDENTIFIER_URI is required when APP_AUTH_MODE=entra")
    [[ -z "${ENTRA_STACK_A_REDIRECT_URIS:-}" ]] && errors+=("ENTRA_STACK_A_REDIRECT_URIS is required when APP_AUTH_MODE=entra")
    [[ -z "${ENTRA_STACK_B_REDIRECT_URIS:-}" ]] && errors+=("ENTRA_STACK_B_REDIRECT_URIS is required when APP_AUTH_MODE=entra")
    [[ -z "${ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID:-}" ]] && errors+=("ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID is required when APP_AUTH_MODE=entra")
  fi
  if [[ "${AWR_AUTH_MODE:-none}" == "entra" ]]; then
    [[ -z "${AWR_AAD_AUDIENCE:-}" ]] && errors+=("AWR_AAD_AUDIENCE is required when AWR_AUTH_MODE=entra")
    [[ -z "${AWR_API_CLIENT_ID:-}" ]] && errors+=("AWR_API_CLIENT_ID is required when AWR_AUTH_MODE=entra")
  fi
  if [[ "${ENTRA_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${ENTRA_API_APP_CLIENT_ID:-}" ]] && errors+=("ENTRA_API_APP_CLIENT_ID required when ENTRA_REUSE=TRUE")
    [[ -z "${ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID:-}" ]] && errors+=("ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID required when ENTRA_REUSE=TRUE")
    [[ -z "${ENTRA_STACK_A_CLIENT_ID:-}" ]] && errors+=("ENTRA_STACK_A_CLIENT_ID required when ENTRA_REUSE=TRUE")
    [[ -z "${ENTRA_STACK_B_CLIENT_ID:-}" ]] && errors+=("ENTRA_STACK_B_CLIENT_ID required when ENTRA_REUSE=TRUE")
  fi
  if [[ "${AZ_SQL_REUSE:-FALSE}" != "TRUE" ]]; then
    [[ -z "${SQL_AAD_ADMIN_LOGIN:-}" ]] && errors+=("SQL_AAD_ADMIN_LOGIN is required when creating Azure SQL")
    [[ -z "${SQL_AAD_ADMIN_OBJECT_ID:-}" ]] && errors+=("SQL_AAD_ADMIN_OBJECT_ID is required when creating Azure SQL")
    [[ "${ALLOW_CREATE_AZURE_SQL:-FALSE}" != "TRUE" ]] && errors+=("ALLOW_CREATE_AZURE_SQL=TRUE is required when AZ_SQL_REUSE is not TRUE")
  fi

  # VNet / Networking (US6)
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
    if [[ "${AZ_SQL_PRIVATE_ENDPOINT_REUSE:-FALSE}" != "TRUE" ]]; then
      [[ -z "${AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR:-}" ]] && errors+=("AZ_SQL_PRIVATE_ENDPOINT_SUBNET_CIDR required when AZ_SQL_PRIVATE_ENDPOINT_REUSE is not TRUE")
    fi
    # AZ_ALLOWED_IPS required when VNet is configured
    [[ -z "${AZ_ALLOWED_IPS:-}" ]] && errors+=("AZ_ALLOWED_IPS is required — App Services must not be deployed without IP restrictions")
  fi

  if [[ ${#errors[@]} -gt 0 ]]; then
    log_error "Reuse coordinate validation failed:"
    for e in "${errors[@]}"; do
      log_error "  - $e"
    done
    exit 1
  fi
  log_success "Reuse coordinate validation passed"
}

# ---------------------------------------------------------------------------
# run_terraform — Execute terraform in a given root directory
# ---------------------------------------------------------------------------
run_terraform() {
  local root_dir="${1:?Usage: run_terraform <root_dir> <action>}"
  local action="${2:?Usage: run_terraform <root_dir> <action>}"
  local plan_exit_code=0

  TERRAFORM_PLAN_HAS_CHANGES=false

  log_info "Running terraform $action in $root_dir"
  pushd "$root_dir" > /dev/null

  terraform init -input=false
  terraform workspace select -or-create "$(terraform_workspace_name)"
  case "$action" in
    plan)
      terraform plan -input=false -detailed-exitcode -out=tfplan || plan_exit_code=$?
      if [[ "$plan_exit_code" -eq 2 ]]; then
        TERRAFORM_PLAN_HAS_CHANGES=true
      elif [[ "$plan_exit_code" -ne 0 ]]; then
        popd > /dev/null
        log_fatal "Terraform plan failed in $root_dir with exit code $plan_exit_code"
      fi
      ;;
    apply)
      terraform plan -input=false -out=tfplan
      terraform apply -input=false -auto-approve tfplan
      ;;
    destroy)
      terraform plan -destroy -input=false -out=tfplan
      terraform apply -input=false -auto-approve tfplan
      ;;
    *)
      log_fatal "Unknown terraform action: $action"
      ;;
  esac

  popd > /dev/null
  log_success "Terraform $action completed in $root_dir"
}

import_existing_resource_group() {
  local root_dir="${1:?Usage: import_existing_resource_group <root_dir>}"
  local resource_group="${RESOURCE_GROUP:?RESOURCE_GROUP is required}"
  local subscription="${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"

  [[ "$(az group exists --name "$resource_group" --output tsv)" == "true" ]] || return

  pushd "$root_dir" > /dev/null
  terraform init -input=false
  terraform workspace select -or-create "$(terraform_workspace_name)"
  if ! terraform state show azurerm_resource_group.main >/dev/null 2>&1; then
    log_info "Importing existing resource group '$resource_group' into the isolated Terraform workspace"
    terraform import azurerm_resource_group.main "/subscriptions/$subscription/resourceGroups/$resource_group"
  fi
  popd > /dev/null
}

# ---------------------------------------------------------------------------
# get_terraform_output — Retrieve a terraform output from a root directory
# ---------------------------------------------------------------------------
get_terraform_output() {
  local root_dir="${1:?}"
  local output_name="${2:?}"
  pushd "$root_dir" > /dev/null
  local output_json output
  output_json="$(TF_WORKSPACE="$(terraform_workspace_name)" terraform output -json "$output_name" 2>/dev/null || true)"
  if [[ -n "$output_json" ]]; then
    output="$(node -e 'let input=""; process.stdin.on("data", chunk => input += chunk).on("end", () => process.stdout.write(String(JSON.parse(input))))' <<< "$output_json")"
  else
    output=""
  fi
  popd > /dev/null
  echo "$output"
}

get_terraform_output_json() {
  local root_dir="${1:?}"
  local output_name="${2:?}"
  pushd "$root_dir" > /dev/null
  local output
  output="$(TF_WORKSPACE="$(terraform_workspace_name)" terraform output -json "$output_name" 2>/dev/null || true)"
  popd > /dev/null
  echo "$output"
}

terraform_workspace_name() {
  local subscription="${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"
  local environment="${ENVIRONMENT:?ENVIRONMENT is required}"
  printf '%s' "${subscription}-${environment}" | tr -c '[:alnum:]_-' '-'
}

# ---------------------------------------------------------------------------
# print_banner — Display a deployment step banner
# ---------------------------------------------------------------------------
print_banner() {
  echo ""
  echo -e "${CYAN}=============================================================================${NC}"
  echo -e "${CYAN} $*${NC}"
  echo -e "${CYAN}=============================================================================${NC}"
  echo ""
}

# ---------------------------------------------------------------------------
# create_zip_artifact — Create a zip file from a directory
# ---------------------------------------------------------------------------
create_zip_artifact() {
  local source_dir="${1:?Usage: create_zip_artifact <source_dir> <artifact_path>}"
  local artifact_path="${2:?Usage: create_zip_artifact <source_dir> <artifact_path>}"

  rm -f "$artifact_path"

  # Prefer Python zipfile for deterministic, cross-platform archives.
  # This avoids Windows backslash path issues and works on GitHub Actions runners.
  if command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then
    local py_exec="python"
    if command -v python3 >/dev/null 2>&1; then
      py_exec="python3"
    fi

    "$py_exec" - "$source_dir" "$artifact_path" <<'PY'
import os
import sys
import zipfile

source_dir = os.path.abspath(sys.argv[1])
artifact_path = os.path.abspath(sys.argv[2])

os.makedirs(os.path.dirname(artifact_path), exist_ok=True)

with zipfile.ZipFile(artifact_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for root, dirs, files in os.walk(source_dir):
        dirs[:] = [d for d in dirs if ".git" not in d]
        for filename in files:
            if ".git" in filename:
                continue
            full_path = os.path.join(root, filename)
            rel_path = os.path.relpath(full_path, source_dir)
            # Ensure Linux-compatible forward slashes in archive entries.
            arcname = rel_path.replace(os.sep, "/")
            zf.write(full_path, arcname)
PY
    return
  fi

  if command -v zip >/dev/null 2>&1; then
    (
      cd "$source_dir"
      zip -r "$artifact_path" . -x "*.git*"
    )
    return
  fi

  if command -v powershell.exe >/dev/null 2>&1; then
    local source_dir_win="$source_dir"
    local artifact_path_win="$artifact_path"

    if command -v cygpath >/dev/null 2>&1; then
      source_dir_win="$(cygpath -w "$source_dir")"
      artifact_path_win="$(cygpath -w "$artifact_path")"
    fi

    powershell.exe -NoProfile -Command "& {
      \$source = '$source_dir_win\\*'
      \$destination = '$artifact_path_win'
      if (Test-Path \$destination) { Remove-Item \$destination -Force }
      Compress-Archive -Path \$source -DestinationPath \$destination -Force
    }"
    return
  fi

  if command -v tar >/dev/null 2>&1; then
    (
      cd "$source_dir"
      tar -a -cf "$artifact_path" .
    )
    return
  fi

  log_fatal "No supported archiver found (python3/python, zip, powershell, tar) for $artifact_path"
}
