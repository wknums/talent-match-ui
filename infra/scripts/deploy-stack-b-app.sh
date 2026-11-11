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
validate_deployment_target stack-b
validate_required "RESOURCE_GROUP" "AZURE_SUBSCRIPTION_ID"
validate_azure_context

if [[ ! -f "$ARTIFACT_PATH" ]]; then
  log_fatal "Artifact not found: $ARTIFACT_PATH"
fi

if [[ -n "${STACK_B_APP_NAME:-}" ]]; then
  log_info "Using Stack B app name from the deployment environment..."
else
  log_info "Resolving Stack B app name from Terraform output..."
  STACK_B_APP_NAME="$(get_terraform_output "infra/terraform/live/stack-b" "app_name")"
fi

if [[ -z "$STACK_B_APP_NAME" ]]; then
  log_fatal "Failed to resolve Stack B app name; set STACK_B_APP_NAME or initialize the Terraform state"
fi

latest_deployment_received_at() {
  az webapp log deployment list \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_B_APP_NAME" \
    --query "[0].received_time" \
    --output tsv 2>/dev/null || true
}

log_info "Deploying artifact '$ARTIFACT_PATH' to app '$STACK_B_APP_NAME' in resource group '$RESOURCE_GROUP'..."
deployment_started_at="$(date +%s)"
deployment_submitted=false
for attempt in {1..3}; do
  if az webapp deploy \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_B_APP_NAME" \
    --src-path "$ARTIFACT_PATH" \
    --type zip \
    --async true; then
    deployment_submitted=true
    break
  fi

  log_warn "Azure CLI lost the OneDeploy connection on attempt $attempt; checking for a new Kudu deployment."
  deployment_received_at="$(latest_deployment_received_at)"
  if [[ -n "$deployment_received_at" ]]; then
    deployment_received_epoch="$(date -d "$deployment_received_at" +%s 2>/dev/null || echo 0)"
    if (( deployment_received_epoch >= deployment_started_at )); then
      deployment_submitted=true
      break
    fi
  fi

  (( attempt < 3 )) && sleep 15
done

if [[ "$deployment_submitted" != true ]]; then
  log_fatal "Stack B artifact never reached Kudu after three deployment attempts."
fi

log_info "Waiting for Kudu to report the final deployment status..."
deployment_status=""
for _ in {1..120}; do
  deployment_status="$(az webapp log deployment list \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_B_APP_NAME" \
    --query "[0].status" \
    --output tsv 2>/dev/null || true)"
  deployment_received_at="$(latest_deployment_received_at)"

  if [[ "$deployment_status" == "4" && -n "$deployment_received_at" ]]; then
    deployment_received_epoch="$(date -d "$deployment_received_at" +%s 2>/dev/null || echo 0)"
    if (( deployment_received_epoch >= deployment_started_at )); then
      log_success "Kudu confirmed the submitted OneDeploy completed successfully."
      exit 0
    fi
  elif [[ "$deployment_status" == "3" && -n "$deployment_received_at" ]]; then
    deployment_received_epoch="$(date -d "$deployment_received_at" +%s 2>/dev/null || echo 0)"
    if (( deployment_received_epoch >= deployment_started_at )); then
      log_fatal "Stack B artifact deployment failed (Kudu status: 3). Review App Service deployment logs."
    fi
  fi

  sleep 15
done

log_fatal "Timed out waiting for Kudu to finalize the submitted Stack B deployment (latest status: ${deployment_status:-unknown})."
