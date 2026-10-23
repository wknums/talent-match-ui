#!/usr/bin/env bash
# Populate generated profile settings from existing Terraform state without applying resources.
set -euo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

[[ $# -eq 1 ]] || log_fatal "Usage: sync-deployment-profile.sh <env-file>"
load_env_file "$1"
STACK_TARGET="${STACK_TARGET:-both}"
validate_enum STACK_TARGET shared-only stack-a stack-b both
validate_required ENVIRONMENT
validate_azure_context

persist_deployment_profile "$1" shared
stack_a=false
stack_b=false
[[ "$STACK_TARGET" == stack-a || "$STACK_TARGET" == both ]] && stack_a=true
[[ "$STACK_TARGET" == stack-b || "$STACK_TARGET" == both ]] && stack_b=true
persist_deployment_profile "$1" apps "$stack_a" "$stack_b"
