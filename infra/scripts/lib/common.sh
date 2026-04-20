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

  # Key Vault
  export TF_VAR_reuse_key_vault="$(bool_to_tf "${AZ_KEY_VAULT_REUSE:-FALSE}")"
  export TF_VAR_existing_key_vault_name="${AZ_KEY_VAULT_NAME:-}"
  export TF_VAR_existing_key_vault_rg="${AZ_KEY_VAULT_RG:-}"

  # APIM
  export TF_VAR_reuse_apim="$(bool_to_tf "${AZ_APIM_REUSE:-FALSE}")"
  export TF_VAR_existing_apim_name="${AZ_APIM_NAME:-}"
  export TF_VAR_existing_apim_rg="${AZ_APIM_RG:-}"

  # Identities
  export TF_VAR_reuse_identities="$(bool_to_tf "${AZ_IDENTITIES_REUSE:-FALSE}")"
  export TF_VAR_existing_identity_stack_a_name="${AZ_IDENTITY_STACK_A_NAME:-}"
  export TF_VAR_existing_identity_stack_b_name="${AZ_IDENTITY_STACK_B_NAME:-}"
  export TF_VAR_existing_identities_rg="${AZ_IDENTITIES_RG:-}"


  # APIM settings
  export TF_VAR_apim_sku="${APIM_SKU:-Consumption_0}"
  export TF_VAR_apim_publisher_name="${APIM_PUBLISHER_NAME:-TalentMatch}"
  export TF_VAR_apim_publisher_email="${APIM_PUBLISHER_EMAIL:-admin@example.com}"

  # Runtime settings (non-secret, safe to export)
  export TF_VAR_storage_provider="${STORAGE_PROVIDER:-azuresql}"
  export TF_VAR_awr_auth_mode="${AWR_AUTH_MODE:-none}"
  export TF_VAR_database_provider="${DATABASE_PROVIDER:-sqlserver}"

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

  log_info "Exported TF_VAR_* variables for environment=$ENVIRONMENT"
}

# ---------------------------------------------------------------------------
# bool_to_tf — Convert TRUE/FALSE string to Terraform boolean
# ---------------------------------------------------------------------------
bool_to_tf() {
  local val="${1^^}"  # uppercase
  [[ "$val" == "TRUE" ]] && echo "true" || echo "false"
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
  if [[ "${AZ_KEY_VAULT_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_KEY_VAULT_NAME:-}" ]] && errors+=("AZ_KEY_VAULT_NAME required when AZ_KEY_VAULT_REUSE=TRUE")
    [[ -z "${AZ_KEY_VAULT_RG:-}" ]]   && errors+=("AZ_KEY_VAULT_RG required when AZ_KEY_VAULT_REUSE=TRUE")
  fi
  if [[ "${AZ_APIM_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_APIM_NAME:-}" ]] && errors+=("AZ_APIM_NAME required when AZ_APIM_REUSE=TRUE")
    [[ -z "${AZ_APIM_RG:-}" ]]   && errors+=("AZ_APIM_RG required when AZ_APIM_REUSE=TRUE")
  fi
  if [[ "${AZ_IDENTITIES_REUSE:-FALSE}" == "TRUE" ]]; then
    [[ -z "${AZ_IDENTITY_STACK_A_NAME:-}" ]] && errors+=("AZ_IDENTITY_STACK_A_NAME required when AZ_IDENTITIES_REUSE=TRUE")
    [[ -z "${AZ_IDENTITY_STACK_B_NAME:-}" ]] && errors+=("AZ_IDENTITY_STACK_B_NAME required when AZ_IDENTITIES_REUSE=TRUE")
    [[ -z "${AZ_IDENTITIES_RG:-}" ]]         && errors+=("AZ_IDENTITIES_RG required when AZ_IDENTITIES_REUSE=TRUE")
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

  log_info "Running terraform $action in $root_dir"
  pushd "$root_dir" > /dev/null

  terraform init -input=false
  case "$action" in
    plan)
      terraform plan -input=false -out=tfplan
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

# ---------------------------------------------------------------------------
# get_terraform_output — Retrieve a terraform output from a root directory
# ---------------------------------------------------------------------------
get_terraform_output() {
  local root_dir="${1:?}"
  local output_name="${2:?}"
  terraform -chdir="$root_dir" output -raw "$output_name" 2>/dev/null || echo ""
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
