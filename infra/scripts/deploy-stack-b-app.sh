#!/usr/bin/env bash
# =============================================================================
# deploy-stack-b-app.sh — Deploy packaged Stack B zip to Azure App Service
# =============================================================================
# Usage:
#   ./infra/scripts/deploy-stack-b-app.sh <env-file> [artifact-path]
#
# Examples:
#   ./infra/scripts/deploy-stack-b-app.sh .env_qa
#   ./infra/scripts/deploy-stack-b-app.sh .env_prod artifacts/stack-b.zip
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

source "$SCRIPT_DIR/lib/common.sh"

ENV_FILE="${1:?Usage: deploy-stack-b-app.sh <env-file> [artifact-path]}"
ARTIFACT_PATH="${2:-artifacts/stack-b.zip}"

print_banner "Deploy Stack B App Artifact"

cd "$REPO_ROOT"

load_env_file "$ENV_FILE"
validate_required "RESOURCE_GROUP" "AZURE_SUBSCRIPTION_ID"
validate_azure_context

if [[ ! -f "$ARTIFACT_PATH" ]]; then
  log_fatal "Artifact not found: $ARTIFACT_PATH"
fi

log_info "Resolving Stack B app name from Terraform output..."
STACK_B_APP_NAME="$(get_terraform_output "infra/terraform/live/stack-b" "app_name")"

if [[ -z "$STACK_B_APP_NAME" ]]; then
  log_fatal "Failed to resolve Stack B app name from terraform output"
fi

log_info "Deploying artifact '$ARTIFACT_PATH' to app '$STACK_B_APP_NAME' in resource group '$RESOURCE_GROUP'..."
if ! az webapp deploy \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_B_APP_NAME" \
  --src-path "$ARTIFACT_PATH" \
  --type zip \
  --async true; then
  log_warn "Azure CLI lost the OneDeploy status connection; checking Kudu deployment history."
  deployment_status="$(az webapp log deployment list \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_B_APP_NAME" \
    --query "[0].status" \
    --output tsv)"

  if [[ "$deployment_status" != "4" ]]; then
    log_fatal "Stack B artifact deployment did not complete successfully (Kudu status: ${deployment_status:-unknown})."
  fi

  log_success "Kudu confirmed the latest OneDeploy completed successfully."
fi

log_success "Stack B deploy command submitted successfully"
