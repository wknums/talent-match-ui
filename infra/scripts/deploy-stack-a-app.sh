#!/usr/bin/env bash
# =============================================================================
# deploy-stack-a-app.sh — Deploy packaged Stack A zip to Azure App Service
# =============================================================================
# Usage:
#   ./infra/scripts/deploy-stack-a-app.sh <env-file> [artifact-path]
#
# Examples:
#   ./infra/scripts/deploy-stack-a-app.sh .env_qa
#   ./infra/scripts/deploy-stack-a-app.sh .env_prod artifacts/stack-a.zip
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

source "$SCRIPT_DIR/lib/common.sh"

ENV_FILE="${1:?Usage: deploy-stack-a-app.sh <env-file> [artifact-path]}"
ARTIFACT_PATH="${2:-artifacts/stack-a.zip}"

print_banner "Deploy Stack A App Artifact"

cd "$REPO_ROOT"

load_env_file "$ENV_FILE"
validate_required "RESOURCE_GROUP" "AZURE_SUBSCRIPTION_ID"
validate_azure_context

if [[ ! -f "$ARTIFACT_PATH" ]]; then
  log_fatal "Artifact not found: $ARTIFACT_PATH"
fi

expected_auth_mode="${APP_AUTH_MODE:-simple}"
artifact_auth_mode="$(unzip -p "$ARTIFACT_PATH" dist/build-info.json 2>/dev/null | sed -n 's/.*"authMode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
if [[ -n "$artifact_auth_mode" && "$artifact_auth_mode" != "$expected_auth_mode" ]]; then
  log_fatal "Artifact authentication mode is '$artifact_auth_mode' but environment profile requires '$expected_auth_mode'. Rebuild with: ./infra/scripts/package-stack-a.sh $ENV_FILE"
fi

log_info "Resolving Stack A app name from Terraform output..."
STACK_A_APP_NAME="$(get_terraform_output "infra/terraform/live/stack-a" "app_name")"

if [[ -z "$STACK_A_APP_NAME" ]]; then
  log_fatal "Failed to resolve Stack A app name from terraform output"
fi

scm_build_enabled="$(az webapp config appsettings list \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --query "[?name=='SCM_DO_BUILD_DURING_DEPLOYMENT'].value | [0]" \
  --output tsv)"
oryx_build_enabled="$(az webapp config appsettings list \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --query "[?name=='ENABLE_ORYX_BUILD'].value | [0]" \
  --output tsv)"

if [[ "${scm_build_enabled,,}" == "false" && "${oryx_build_enabled,,}" == "false" ]]; then
  log_success "On-host build is already disabled."
else
  log_info "Disabling on-host build because the artifact contains production dependencies..."
  az webapp config appsettings set \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_A_APP_NAME" \
    --settings SCM_DO_BUILD_DURING_DEPLOYMENT=false ENABLE_ORYX_BUILD=false \
    --output none
  log_info "Waiting for the App Service management update to settle before deployment..."
  sleep 60
fi

log_info "Deploying artifact '$ARTIFACT_PATH' to app '$STACK_A_APP_NAME' in resource group '$RESOURCE_GROUP'..."
deployment_started_at="$(date +%s)"
if ! az webapp deploy \
  --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_A_APP_NAME" \
  --src-path "$ARTIFACT_PATH" \
  --type zip \
  --async true; then
  log_warn "Azure CLI lost the OneDeploy status connection; validating Kudu deployment history."
fi

log_info "Waiting for Kudu to report the final deployment status..."
deployment_status=""
for _ in {1..120}; do
  deployment_status="$(az webapp log deployment list \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_A_APP_NAME" \
    --query "[0].status" \
    --output tsv 2>/dev/null || true)"
  deployment_received_at="$(az webapp log deployment list \
    --subscription "$AZURE_SUBSCRIPTION_ID" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$STACK_A_APP_NAME" \
    --query "[0].received_time" \
    --output tsv 2>/dev/null || true)"

  if [[ "$deployment_status" == "4" && -n "$deployment_received_at" ]]; then
    deployment_received_epoch="$(date -d "$deployment_received_at" +%s 2>/dev/null || echo 0)"
    if (( deployment_received_epoch >= deployment_started_at )); then
      log_success "Kudu confirmed the latest OneDeploy completed successfully."
      exit 0
    fi
  elif [[ "$deployment_status" == "3" && -n "$deployment_received_at" ]]; then
    deployment_received_epoch="$(date -d "$deployment_received_at" +%s 2>/dev/null || echo 0)"
    if (( deployment_received_epoch >= deployment_started_at )); then
      log_fatal "Stack A artifact deployment failed (Kudu status: 3). Review App Service deployment logs."
    fi
  fi

  sleep 15
done

log_fatal "Timed out waiting for Kudu to finalize the Stack A deployment (latest status: ${deployment_status:-unknown})."
