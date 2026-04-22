#!/usr/bin/env bash
# =============================================================================
# deploy.sh — Selective Azure Deployment Wrapper
# =============================================================================
# Usage: ./infra/scripts/deploy.sh <env-file> <tf-environment> <action> <target>
#
# Arguments:
#   env-file        .env_local, .env_qa, or .env_prod
#   tf-environment  dev, test, or prod
#   action          plan or apply
#   target          shared-only, stack-a, stack-b, or both
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TF_LIVE_DIR="$REPO_ROOT/infra/terraform/live"

source "$SCRIPT_DIR/lib/common.sh"

# ---------------------------------------------------------------------------
# Parse arguments
# ---------------------------------------------------------------------------
ENV_FILE="${1:?Usage: deploy.sh <env-file> <tf-environment> <action> <target>}"
TF_ENVIRONMENT="${2:?Usage: deploy.sh <env-file> <tf-environment> <action> <target>}"
ACTION="${3:?Usage: deploy.sh <env-file> <tf-environment> <action> <target>}"
TARGET="${4:?Usage: deploy.sh <env-file> <tf-environment> <action> <target>}"

# ---------------------------------------------------------------------------
# Validate inputs
# ---------------------------------------------------------------------------
validate_enum "TF_ENVIRONMENT" "dev" "test" "prod"
validate_enum "ACTION" "plan" "apply"
validate_enum "TARGET" "shared-only" "stack-a" "stack-b" "both"

# ---------------------------------------------------------------------------
# Load environment and export TF_VAR_*
# ---------------------------------------------------------------------------
load_env_file "$ENV_FILE"

# Ensure secrets are not echoed
set +x  # Disable trace mode if it was enabled

export_tf_vars
validate_reuse_coordinates

# Export subscription ID for provider
export TF_VAR_subscription_id="${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"

print_banner "Selective Azure Deployment"
log_info "Environment: $TF_ENVIRONMENT"
log_info "Action:      $ACTION"
log_info "Target:      $TARGET"
log_info "Env File:    $ENV_FILE"

# ---------------------------------------------------------------------------
# Determine which roots to deploy
# ---------------------------------------------------------------------------
DEPLOY_SHARED=false
DEPLOY_STACK_A=false
DEPLOY_STACK_B=false

case "$TARGET" in
  shared-only)
    DEPLOY_SHARED=true
    ;;
  stack-a)
    DEPLOY_SHARED=true
    DEPLOY_STACK_A=true
    ;;
  stack-b)
    DEPLOY_SHARED=true
    DEPLOY_STACK_B=true
    ;;
  both)
    DEPLOY_SHARED=true
    DEPLOY_STACK_A=true
    DEPLOY_STACK_B=true
    ;;
esac

# ---------------------------------------------------------------------------
# Step 1: Shared infrastructure (always first when needed)
# ---------------------------------------------------------------------------
if [[ "$DEPLOY_SHARED" == "true" ]]; then
  print_banner "Step 1: Shared Infrastructure"
  run_terraform "$TF_LIVE_DIR/shared" "$ACTION"

  # Capture shared outputs for stack roots
  if [[ "$ACTION" == "apply" ]]; then
    export TF_VAR_app_service_plan_id="$(get_terraform_output "$TF_LIVE_DIR/shared" "app_service_plan_id")"
    export TF_VAR_sql_server_fqdn="$(get_terraform_output "$TF_LIVE_DIR/shared" "sql_server_fqdn")"
    export TF_VAR_sql_database_name="$(get_terraform_output "$TF_LIVE_DIR/shared" "sql_database_name")"
    export TF_VAR_key_vault_uri="$(get_terraform_output "$TF_LIVE_DIR/shared" "key_vault_uri")"
    export TF_VAR_apim_gateway_url="$(get_terraform_output "$TF_LIVE_DIR/shared" "apim_gateway_url")"
    export TF_VAR_identity_id_stack_a="$(get_terraform_output "$TF_LIVE_DIR/shared" "identity_stack_a_id")"
    export TF_VAR_identity_id_stack_b="$(get_terraform_output "$TF_LIVE_DIR/shared" "identity_stack_b_id")"
    export TF_VAR_identity_client_id_stack_a="$(get_terraform_output "$TF_LIVE_DIR/shared" "identity_stack_a_client_id")"
    export TF_VAR_identity_client_id_stack_b="$(get_terraform_output "$TF_LIVE_DIR/shared" "identity_stack_b_client_id")"

    # Networking (US6): Capture integration subnet ID if networking is configured
    subnet_id="$(get_terraform_output "$TF_LIVE_DIR/shared" "integration_subnet_id")"
    if [[ -n "$subnet_id" ]]; then
      export TF_VAR_integration_subnet_id="$subnet_id"
      log_info "Captured integration_subnet_id from shared root"
    fi

    STACK_A_IDENTITY_NAME="${TF_VAR_identity_id_stack_a##*/}"
    STACK_B_IDENTITY_NAME="${TF_VAR_identity_id_stack_b##*/}"
    export SQL_SERVER_FQDN="${TF_VAR_sql_server_fqdn}"
    export SQL_DATABASE_NAME="${TF_VAR_sql_database_name}"
    export STACK_A_IDENTITY_NAME
    export STACK_B_IDENTITY_NAME

    print_banner "Shared Post-Provisioning: Azure SQL Entra Users"
    node "$(cygpath -w "$SCRIPT_DIR/bootstrap-sql-entra-users.mjs")"
  fi
fi

# ---------------------------------------------------------------------------
# Step 2: Stack A
# ---------------------------------------------------------------------------
if [[ "$DEPLOY_STACK_A" == "true" ]]; then
  print_banner "Step 2: Stack A (Node.js/Express)"

  # Set Stack A identity
  export TF_VAR_identity_id="${TF_VAR_identity_id_stack_a:-}"
  export TF_VAR_identity_client_id="${TF_VAR_identity_client_id_stack_a:-}"

  run_terraform "$TF_LIVE_DIR/stack-a" "$ACTION"

  if [[ "$ACTION" == "apply" ]]; then
    STACK_A_URL="$(get_terraform_output "$TF_LIVE_DIR/stack-a" "app_url")"
    STACK_A_NAME="$(get_terraform_output "$TF_LIVE_DIR/stack-a" "app_name")"
  fi
fi

# ---------------------------------------------------------------------------
# Step 3: Stack B
# ---------------------------------------------------------------------------
if [[ "$DEPLOY_STACK_B" == "true" ]]; then
  print_banner "Step 3: Stack B (.NET Blazor WASM)"

  # Set Stack B identity
  export TF_VAR_identity_id="${TF_VAR_identity_id_stack_b:-}"
  export TF_VAR_identity_client_id="${TF_VAR_identity_client_id_stack_b:-}"

  run_terraform "$TF_LIVE_DIR/stack-b" "$ACTION"

  if [[ "$ACTION" == "apply" ]]; then
    STACK_B_URL="$(get_terraform_output "$TF_LIVE_DIR/stack-b" "app_url")"
    STACK_B_NAME="$(get_terraform_output "$TF_LIVE_DIR/stack-b" "app_name")"
  fi
fi

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
print_banner "Deployment Summary"
log_info "Environment: $TF_ENVIRONMENT"
log_info "Action:      $ACTION"
log_info "Target:      $TARGET"

if [[ "$ACTION" == "apply" ]]; then
  echo ""
  if [[ "$DEPLOY_SHARED" == "true" ]]; then
    log_success "Shared infrastructure: Applied"
    log_info "  Key Vault URI:       ${TF_VAR_key_vault_uri:-N/A}"
    log_info "  SQL Server FQDN:     ${TF_VAR_sql_server_fqdn:-N/A}"
    log_info "  APIM Gateway URL:    ${TF_VAR_apim_gateway_url:-N/A}"
  fi
  if [[ "$DEPLOY_STACK_A" == "true" ]]; then
    log_success "Stack A: Deployed"
    log_info "  App Name: ${STACK_A_NAME:-N/A}"
    log_info "  App URL:  ${STACK_A_URL:-N/A}"
  fi
  if [[ "$DEPLOY_STACK_B" == "true" ]]; then
    log_success "Stack B: Deployed"
    log_info "  App Name: ${STACK_B_NAME:-N/A}"
    log_info "  App URL:  ${STACK_B_URL:-N/A}"
  fi
else
  log_info "Plan completed. Run with 'apply' to deploy."
fi

log_success "Deployment complete!"
