#!/usr/bin/env bash
# Explicitly provision the selected stacks' managed identities before deployment.
# App registrations, role assignments, the profile, and Terraform state are not modified.
# Usage: bash infra/scripts/setup-identity.sh <env-file>
set -euo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

if [[ $# -eq 1 && "$1" == --help ]]; then
  printf 'Usage: bash infra/scripts/setup-identity.sh <env-file>\n'
  printf 'Check/create managed identities selected by STACK_TARGET and print reuse coordinates.\n'
  exit 0
fi
[[ $# -eq 1 ]] || log_fatal "Usage: bash infra/scripts/setup-identity.sh <env-file>"
load_env_file "$1"

STACK_TARGET="${STACK_TARGET:-both}"
validate_enum STACK_TARGET shared-only stack-a stack-b both
if [[ "$STACK_TARGET" == shared-only ]]; then
  log_info "STACK_TARGET=shared-only: no managed identities are required."
  exit 0
fi

validate_required ENVIRONMENT AZURE_LOCATION
identity_rg="${AZ_IDENTITIES_RG:-${RESOURCE_GROUP:-}}"
[[ -n "$identity_rg" ]] || log_fatal "Set AZ_IDENTITIES_RG or RESOURCE_GROUP for managed-identity setup."

declare -a identity_variables=() identity_names=()
for stack in a b; do
  [[ "$STACK_TARGET" == both || "$STACK_TARGET" == "stack-$stack" ]] || continue
  variable="AZ_IDENTITY_STACK_${stack^^}_NAME"
  runtime=node
  [[ "$stack" == b ]] && runtime=blazor
  name="${!variable:-id-${PROJECT_NAME:-talentmatch}-${runtime}-${ENVIRONMENT}}"
  [[ "$name" =~ ^[a-zA-Z0-9][a-zA-Z0-9_-]{2,127}$ ]] \
    || log_fatal "Invalid managed identity name in $variable: use 3-128 letters, digits, underscores, or hyphens, starting with a letter or digit."
  identity_variables+=("$variable")
  identity_names+=("$name")
done

command -v az >/dev/null 2>&1 || log_fatal "Azure CLI is required for managed-identity setup."
validate_azure_context
group_exists="$(az group exists --name "$identity_rg" --subscription "$AZURE_SUBSCRIPTION_ID" --output tsv | tr -d '\r')" \
  || log_fatal "Unable to check managed-identity resource group '$identity_rg'."
case "$group_exists" in
  true) ;;
  false) log_fatal "Managed-identity resource group '$identity_rg' does not exist. Create it before running setup." ;;
  *) log_fatal "Unexpected resource-group lookup response for '$identity_rg'." ;;
esac

if [[ "${AZ_IDENTITIES_REUSE:-FALSE}" != TRUE ]]; then
  log_warn "AZ_IDENTITIES_REUSE is not TRUE: Terraform still owns identity creation. For a new separately provisioned environment, record the reuse settings printed below before deployment. Do not change an existing deployment's ownership without a reviewed Terraform state migration."
fi

for name in "${identity_names[@]}"; do
  identities="$(az identity list --resource-group "$identity_rg" --subscription "$AZURE_SUBSCRIPTION_ID" \
    --query "[].[name,id]" --output tsv | tr -d '\r')" \
    || log_fatal "Unable to look up managed identity '$name'. Check Azure access; no creation was attempted for this identity."
  identity_id=""
  while IFS=$'\t' read -r existing_name existing_id; do
    [[ "${existing_name,,}" == "${name,,}" ]] || continue
    [[ -n "$existing_id" ]] || log_fatal "Unexpected managed identity lookup response for '$name'."
    identity_id="$existing_id"
    break
  done <<< "$identities"
  if [[ -z "$identity_id" ]]; then
    log_info "Creating managed identity '$name' in '$identity_rg'."
    identity_id="$(az identity create --name "$name" --resource-group "$identity_rg" \
      --location "$AZURE_LOCATION" --subscription "$AZURE_SUBSCRIPTION_ID" --query id --output tsv | tr -d '\r')" \
      || log_fatal "Unable to create managed identity '$name'."
  fi
  expected_id="/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$identity_rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$name"
  [[ "${identity_id,,}" == "${expected_id,,}" ]] \
    || log_fatal "Unexpected managed identity resource ID for '$name'; setup could not verify the target resource."
  log_success "Managed identity '$name' is available."
done

log_success "Managed-identity setup complete. App registrations and role assignments were not changed."
log_info "For separately provisioned identities, record these settings in the deployment profile. The profile and Terraform state were not modified:"
printf 'AZ_IDENTITIES_REUSE=TRUE\nAZ_IDENTITIES_RG=%s\n' "$identity_rg"
for index in "${!identity_names[@]}"; do
  printf '%s=%s\n' "${identity_variables[$index]}" "${identity_names[$index]}"
done
