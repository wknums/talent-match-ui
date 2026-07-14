#!/usr/bin/env bash
# =============================================================================
# deploy-stack-b-app.sh — Deploy packaged Stack B zip to Azure App Service
# =============================================================================
# Usage:
#   ./infra/scripts/deploy-stack-b-app.sh [env-file] [artifact-path]
#
# Examples:
#   ./infra/scripts/deploy-stack-b-app.sh .env_qa
#   ./infra/scripts/deploy-stack-b-app.sh .env_prod artifacts/stack-b.zip
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

source "$SCRIPT_DIR/lib/common.sh"

ENV_FILE="${1:-.env_qa}"
ARTIFACT_PATH="${2:-artifacts/stack-b.zip}"

print_banner "Deploy Stack B App Artifact"

cd "$REPO_ROOT"

load_env_file "$ENV_FILE"
validate_required "RESOURCE_GROUP"

if [[ ! -f "$ARTIFACT_PATH" ]]; then
  log_fatal "Artifact not found: $ARTIFACT_PATH"
fi

log_info "Resolving Stack B app name from Terraform output..."
STACK_B_APP_NAME="$(terraform -chdir=infra/terraform/live/stack-b output -raw app_name)"

if [[ -z "$STACK_B_APP_NAME" ]]; then
  log_fatal "Failed to resolve Stack B app name from terraform output"
fi

log_info "Deploying artifact '$ARTIFACT_PATH' to app '$STACK_B_APP_NAME' in resource group '$RESOURCE_GROUP'..."
az webapp deploy \
  --resource-group "$RESOURCE_GROUP" \
  --name "$STACK_B_APP_NAME" \
  --src-path "$ARTIFACT_PATH" \
  --type zip \
  --async true

log_success "Stack B deploy command submitted successfully"
