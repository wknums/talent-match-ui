#!/usr/bin/env bash
# =============================================================================
# resolve-targets.sh — Merge manual target override with changed-path detection
# =============================================================================
# Called by the GitHub Actions workflow to determine which deployment scopes
# should run. Outputs are consumed by subsequent workflow jobs.
#
# Environment variables (set by the workflow):
#   GITHUB_EVENT_NAME     push | workflow_dispatch
#   GITHUB_REF            refs/heads/main | refs/heads/develop | ...
#   INPUT_TARGET          Manual target override (shared-only|stack-a|stack-b|both)
#   INPUT_ENVIRONMENT     Manual environment override (development|staging|production)
#   CHANGED_SHARED        true/false from path filter
#   CHANGED_STACK_A       true/false from path filter
#   CHANGED_STACK_B       true/false from path filter
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

# ---------------------------------------------------------------------------
# Resolve environment from branch or manual input
# ---------------------------------------------------------------------------
resolve_environment() {
  if [[ "${GITHUB_EVENT_NAME:-}" == "workflow_dispatch" && -n "${INPUT_ENVIRONMENT:-}" ]]; then
    echo "$INPUT_ENVIRONMENT"
    return
  fi

  local ref="${GITHUB_REF:-}"
  case "$ref" in
    refs/heads/main)    echo "production" ;;
    refs/heads/develop) echo "staging" ;;
    *)                  echo "development" ;;
  esac
}

# ---------------------------------------------------------------------------
# Map GitHub environment to Terraform environment and env file
# ---------------------------------------------------------------------------
map_environment() {
  local gh_env="$1"
  case "$gh_env" in
    development) echo "dev .env_local" ;;
    staging)     echo "test .env_qa" ;;
    production)  echo "prod .env_prod" ;;
    *)           log_fatal "Unknown GitHub environment: $gh_env" ;;
  esac
}

# ---------------------------------------------------------------------------
# Resolve deployment targets
# ---------------------------------------------------------------------------
GH_ENVIRONMENT="$(resolve_environment)"
read -r TF_ENV ENV_FILE <<< "$(map_environment "$GH_ENVIRONMENT")"

# Defaults
DEPLOY_SHARED=false
DEPLOY_STACK_A=false
DEPLOY_STACK_B=false
PACKAGE_STACK_A=false
PACKAGE_STACK_B=false

if [[ "${GITHUB_EVENT_NAME:-}" == "workflow_dispatch" && -n "${INPUT_TARGET:-}" ]]; then
  # --- Manual target override ---
  log_info "Manual target override: ${INPUT_TARGET}"
  case "${INPUT_TARGET}" in
    shared-only)
      DEPLOY_SHARED=true
      ;;
    stack-a)
      DEPLOY_SHARED=true
      DEPLOY_STACK_A=true
      PACKAGE_STACK_A=true
      ;;
    stack-b)
      DEPLOY_SHARED=true
      DEPLOY_STACK_B=true
      PACKAGE_STACK_B=true
      ;;
    both)
      DEPLOY_SHARED=true
      DEPLOY_STACK_A=true
      DEPLOY_STACK_B=true
      PACKAGE_STACK_A=true
      PACKAGE_STACK_B=true
      ;;
  esac
else
  # --- Automatic detection from changed paths ---
  log_info "Resolving targets from changed paths"

  if [[ "${CHANGED_SHARED:-false}" == "true" ]]; then
    DEPLOY_SHARED=true
  fi
  if [[ "${CHANGED_STACK_A:-false}" == "true" ]]; then
    DEPLOY_SHARED=true
    DEPLOY_STACK_A=true
    PACKAGE_STACK_A=true
  fi
  if [[ "${CHANGED_STACK_B:-false}" == "true" ]]; then
    DEPLOY_SHARED=true
    DEPLOY_STACK_B=true
    PACKAGE_STACK_B=true
  fi

  # If nothing changed specifically, deploy all (safety net for infra changes)
  if [[ "$DEPLOY_SHARED" == "false" && "$DEPLOY_STACK_A" == "false" && "$DEPLOY_STACK_B" == "false" ]]; then
    log_warn "No specific changes detected — defaulting to deploy all"
    DEPLOY_SHARED=true
    DEPLOY_STACK_A=true
    DEPLOY_STACK_B=true
    PACKAGE_STACK_A=true
    PACKAGE_STACK_B=true
  fi
fi

# ---------------------------------------------------------------------------
# Output results
# ---------------------------------------------------------------------------
log_info "Resolved targets:"
log_info "  gh_environment:  $GH_ENVIRONMENT"
log_info "  tf_environment:  $TF_ENV"
log_info "  env_file:        $ENV_FILE"
log_info "  deploy_shared:   $DEPLOY_SHARED"
log_info "  deploy_stack_a:  $DEPLOY_STACK_A"
log_info "  deploy_stack_b:  $DEPLOY_STACK_B"
log_info "  package_stack_a: $PACKAGE_STACK_A"
log_info "  package_stack_b: $PACKAGE_STACK_B"

# Write to GITHUB_OUTPUT if available (GitHub Actions)
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "gh_environment=$GH_ENVIRONMENT"
    echo "tf_environment=$TF_ENV"
    echo "env_file=$ENV_FILE"
    echo "deploy_shared=$DEPLOY_SHARED"
    echo "deploy_stack_a=$DEPLOY_STACK_A"
    echo "deploy_stack_b=$DEPLOY_STACK_B"
    echo "package_stack_a=$PACKAGE_STACK_A"
    echo "package_stack_b=$PACKAGE_STACK_B"
  } >> "$GITHUB_OUTPUT"
fi
