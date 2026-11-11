#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_dir="$(mktemp -d)"
profile="$test_dir/customer profile.env.azure"
original="$test_dir/original"
log="$test_dir/log"
export PROFILE_SYNC_CALLS="$test_dir/calls"
trap 'echo "FAIL at line $LINENO: $BASH_COMMAND" >&2; [[ ! -f "$log" ]] || cat "$log" >&2' ERR
trap 'rm -f "$profile" "$original" "$log" "$PROFILE_SYNC_CALLS"; rmdir "$test_dir"' EXIT

export AZURE_LOCATION=australiaeast RESOURCE_GROUP=app-rg AWR_AUTH_MODE=none
export AWR_SEQ_API_ENDPOINT=https://scoring.example.com AZ_SQL_REUSE=FALSE
export ALLOW_CREATE_AZURE_SQL=TRUE SQL_AAD_ADMIN_LOGIN=admin SQL_AAD_ADMIN_OBJECT_ID=admin-id
export AZ_SQL_BOOTSTRAP_ENABLED=FALSE AZ_KEY_VAULT_ENABLED=TRUE AZ_KEY_VAULT_REUSE=FALSE
export AZ_APIM_ENABLED=FALSE AZ_APP_INSIGHTS_ENABLED=FALSE AZ_IDENTITIES_REUSE=FALSE AZ_VNET_REUSE=FALSE

az() {
  case "$*" in
    "account show --query tenantId --output tsv") printf 'test-tenant\n' ;;
    "account show --query id --output tsv") printf 'test-subscription\n' ;;
    "group exists"*) printf 'false\n' ;;
    *) echo "Unexpected Azure mutation or lookup: $*" >&2; return 1 ;;
  esac
}

terraform() {
  printf '%s|%s\n' "${PWD##*/}" "$*" >> "$PROFILE_SYNC_CALLS"
  [[ "${MOCK_FAIL_OUTPUTS:-false}" != true || "$1" != output ]] || return 1
  if [[ "$1" == output && $# -eq 2 ]]; then
    if [[ "${PWD##*/}" == shared ]]; then
      cat <<'JSON'
{"sql_server_id":{"value":"/subscriptions/test-subscription/resourceGroups/sql-rg/providers/Microsoft.Sql/servers/test-server"},"sql_server_name":{"value":"test-server"},"sql_database_name":{"value":"test-db"},"sql_server_fqdn":{"value":"test-server.database.windows.net"},"app_service_plan_id":{"value":"/subscriptions/test-subscription/resourceGroups/plan-rg/providers/Microsoft.Web/serverfarms/test-plan"},"identity_stack_b_id":{"value":"/subscriptions/test-subscription/resourceGroups/identity-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity-b"},"key_vault_id":{"value":"/subscriptions/test-subscription/resourceGroups/vault-rg/providers/Microsoft.KeyVault/vaults/test-vault"},"key_vault_uri":{"value":"https://test-vault.vault.azure.net/"},"source_connector_client_id":{"value":"source-client"},"source_connector_redirect_uris":{"value":[]},"bulk_auth_key_uri":{"value":"https://test-vault.vault.azure.net/keys/bulk-auth"},"unrelated_secret":{"value":"sensitive-output-canary","sensitive":true}}
JSON
    else
      printf '{"app_url":{"value":"https://actual-b-unique.azurewebsites.net"}}\n'
    fi
  elif [[ "$1" == output ]]; then
    case "$3" in
      app_service_plan_id) printf '"/subscriptions/test-subscription/resourceGroups/plan-rg/providers/Microsoft.Web/serverfarms/test-plan"\n' ;;
      sql_server_fqdn) printf '"test-server.database.windows.net"\n' ;;
      sql_database_name) printf '"test-db"\n' ;;
      identity_stack_b_id) printf '"/subscriptions/test-subscription/resourceGroups/identity-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity-b"\n' ;;
      source_connector_client_id) printf '"source-client"\n' ;;
      app_url) printf '"https://actual-b-unique.azurewebsites.net"\n' ;;
      *) printf '""\n' ;;
    esac
  fi
}
export -f az terraform

cat > "$profile" <<'ENV'
AZURE_TENANT_ID=test-tenant
AZURE_SUBSCRIPTION_ID=test-subscription
ENVIRONMENT=test
STACK_TARGET=stack-b
APP_AUTH_MODE=simple
MICROSOFT365_SOURCE_MODE=graph
MICROSOFT365_SOURCE_REDIRECT_URIS=
PRIVATE_SETTING="profile-secret-canary"
ENV
: > "$PROFILE_SYNC_CALLS"
bash "$SCRIPT_DIR/../sync-deployment-profile.sh" "$profile" > "$log" 2>&1
grep -Fxq 'SQL_RG="sql-rg"' "$profile"
grep -Fxq 'MICROSOFT365_SOURCE_CLIENT_ID="source-client"' "$profile"
grep -Fxq 'MICROSOFT365_SOURCE_REDIRECT_URIS="https://actual-b-unique.azurewebsites.net/api/source-connections/callback,https://actual-b-unique.azurewebsites.net/api/source-connections/admin-consent-callback"' "$profile"
grep -Fq 'register these exact Web callbacks' "$log"
[[ "$(wc -l < "$PROFILE_SYNC_CALLS" | tr -d '[:space:]')" == 2 ]]
! grep -Evq '^(shared|stack-b)\|output -json$' "$PROFILE_SYNC_CALLS"
! grep -Eq 'sensitive-output-canary|profile-secret-canary' "$log"

cp "$profile" "$original"
: > "$PROFILE_SYNC_CALLS"
bash "$SCRIPT_DIR/../deploy.sh" "$profile" test plan stack-b-only > "$log" 2>&1
cmp -s "$profile" "$original"

: > "$PROFILE_SYNC_CALLS"
bash "$SCRIPT_DIR/../deploy.sh" "$profile" test apply stack-b > "$log" 2>&1
cmp -s "$profile" "$original"
grep -Fxq 'shared|apply -input=false -auto-approve tfplan' "$PROFILE_SYNC_CALLS"
grep -Fxq 'stack-b|apply -input=false -auto-approve tfplan' "$PROFILE_SYNC_CALLS"
grep -Fxq 'shared|output -json' "$PROFILE_SYNC_CALLS"
grep -Fxq 'stack-b|output -json' "$PROFILE_SYNC_CALLS"
! grep -Fq 'stack-a|' "$PROFILE_SYNC_CALLS"
! grep -Eq 'sensitive-output-canary|profile-secret-canary' "$log"

export MOCK_FAIL_OUTPUTS=true
if bash "$SCRIPT_DIR/../sync-deployment-profile.sh" "$profile" > "$log" 2>&1; then
  echo "FAIL: failed state reads must not be treated as a successful profile sync." >&2
  exit 1
fi
grep -Fq 'Unable to read shared Terraform outputs' "$log"
cmp -s "$profile" "$original"

echo "PASS: read-only state sync and apply backfill the selected profile without Entra mutation, secret disclosure, guessed hosts, or plan writes."
