#!/usr/bin/env bash
# =============================================================================
# deprovision.sh — Safe Destroy Wrapper with Reuse Protection
# =============================================================================
# Usage: ./infra/scripts/deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]
#
# Arguments:
#   env-file        .env_local, .env_qa, .env_qa_mcaps, or .env_prod (REQUIRED)
#   tf-environment  dev, test, or prod
#   target          shared, stack-a, stack-b, or both
#   --dry-run       Show plan -destroy without destroying
#   --force         Skip interactive confirmation (CI only)
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TF_LIVE_DIR="$REPO_ROOT/infra/terraform/live"

source "$SCRIPT_DIR/lib/common.sh"

# ---------------------------------------------------------------------------
# Parse arguments
# ---------------------------------------------------------------------------
ENV_FILE="${1:?Usage: deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]}"
TF_ENVIRONMENT="${2:?Usage: deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]}"
TARGET="${3:?Usage: deprovision.sh <env-file> <tf-environment> <target> [--dry-run] [--force]}"
shift 3

DRY_RUN=false
FORCE=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN=true ;;
    --force)   FORCE=true ;;
    *) log_fatal "Unknown option: $1" ;;
  esac
  shift
done

# ---------------------------------------------------------------------------
# Validate
# ---------------------------------------------------------------------------
validate_enum "TF_ENVIRONMENT" "dev" "test" "prod"
validate_enum "TARGET" "shared" "stack-a" "stack-b" "both"

# ---------------------------------------------------------------------------
# Load environment
# ---------------------------------------------------------------------------
load_env_file "$ENV_FILE"
export_tf_vars
export TF_VAR_subscription_id="${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"

# ---------------------------------------------------------------------------
# Build destroy and protected summaries
# ---------------------------------------------------------------------------
print_banner "Deprovision Summary — $TF_ENVIRONMENT"

declare -a DESTROY_LIST=()
declare -a PROTECTED_LIST=()

# Check shared resource reuse flags
check_resource() {
  local name="$1"
  local reuse_flag="$2"
  if [[ "${!reuse_flag:-FALSE}" == "TRUE" ]]; then
    PROTECTED_LIST+=("$name")
  else
    DESTROY_LIST+=("$name")
  fi
}

if [[ "$TARGET" == "shared" || "$TARGET" == "both" ]]; then
  check_resource "App Service Plan" "AZ_APP_SERVICE_PLAN_REUSE"
  check_resource "Azure SQL"        "AZ_SQL_REUSE"
  check_resource "Key Vault"        "AZ_KEY_VAULT_REUSE"
  check_resource "API Management"   "AZ_APIM_REUSE"
  check_resource "Identities"       "AZ_IDENTITIES_REUSE"
  DESTROY_LIST+=("Resource Group (if all resources destroyed)")
fi

if [[ "$TARGET" == "stack-a" || "$TARGET" == "both" ]]; then
  DESTROY_LIST+=("Stack A App Service")
fi

if [[ "$TARGET" == "stack-b" || "$TARGET" == "both" ]]; then
  DESTROY_LIST+=("Stack B App Service")
fi

# Print summary
echo ""
if [[ ${#DESTROY_LIST[@]} -gt 0 ]]; then
  for item in "${DESTROY_LIST[@]}"; do
    echo -e "  ${RED}[DESTROY]${NC}   $item"
  done
fi
if [[ ${#PROTECTED_LIST[@]} -gt 0 ]]; then
  for item in "${PROTECTED_LIST[@]}"; do
    echo -e "  ${GREEN}[PROTECTED]${NC} $item (reuse=true — will NOT be destroyed)"
  done
fi
echo ""

# ---------------------------------------------------------------------------
# Dry-run mode
# ---------------------------------------------------------------------------
if [[ "$DRY_RUN" == "true" ]]; then
  log_info "DRY RUN — showing terraform plan -destroy without making changes"

  if [[ "$TARGET" == "stack-a" || "$TARGET" == "both" ]]; then
    log_info "Dry-run: Stack A"
    pushd "$TF_LIVE_DIR/stack-a" > /dev/null
    terraform init -input=false
    terraform plan -destroy -input=false
    popd > /dev/null
  fi

  if [[ "$TARGET" == "stack-b" || "$TARGET" == "both" ]]; then
    log_info "Dry-run: Stack B"
    pushd "$TF_LIVE_DIR/stack-b" > /dev/null
    terraform init -input=false
    terraform plan -destroy -input=false
    popd > /dev/null
  fi

  if [[ "$TARGET" == "shared" ]]; then
    log_info "Dry-run: Shared Infrastructure"
    pushd "$TF_LIVE_DIR/shared" > /dev/null
    terraform init -input=false
    terraform plan -destroy -input=false
    popd > /dev/null
  fi

  log_success "Dry run complete — no resources were destroyed"
  exit 0
fi

# ---------------------------------------------------------------------------
# Confirmation
# ---------------------------------------------------------------------------
if [[ "$FORCE" != "true" ]]; then
  echo ""
  log_warn "This will DESTROY the resources listed above in the '$TF_ENVIRONMENT' environment."
  echo -n "Type the environment name ($TF_ENVIRONMENT) to confirm: "
  read -r CONFIRM
  if [[ "$CONFIRM" != "$TF_ENVIRONMENT" ]]; then
    log_fatal "Confirmation failed. Expected '$TF_ENVIRONMENT', got '$CONFIRM'. Aborting."
  fi
fi

# ---------------------------------------------------------------------------
# Execute destruction — stacks first, then shared
# ---------------------------------------------------------------------------
if [[ "$TARGET" == "stack-a" || "$TARGET" == "both" ]]; then
  print_banner "Destroying Stack A"
  run_terraform "$TF_LIVE_DIR/stack-a" "destroy"
fi

if [[ "$TARGET" == "stack-b" || "$TARGET" == "both" ]]; then
  print_banner "Destroying Stack B"
  run_terraform "$TF_LIVE_DIR/stack-b" "destroy"
fi

if [[ "$TARGET" == "shared" ]]; then
  print_banner "Destroying Shared Infrastructure"
  log_warn "This destroys shared infrastructure. Stack roots should be destroyed first."
  run_terraform "$TF_LIVE_DIR/shared" "destroy"
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
print_banner "Deprovision Complete"
log_success "Target: $TARGET in $TF_ENVIRONMENT"

if [[ ${#PROTECTED_LIST[@]} -gt 0 ]]; then
  log_info "Protected resources (untouched):"
  for item in "${PROTECTED_LIST[@]}"; do
    echo -e "  ${GREEN}[PROTECTED]${NC} $item"
  done
fi

log_success "Deprovision finished."
