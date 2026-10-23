#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
profile="$(mktemp)"
export MONITORING_CAPTURE="$(mktemp)"
deployment_log="$(mktemp)"
trap 'rm -f "$profile" "$MONITORING_CAPTURE" "$deployment_log"' EXIT

az() {
  case "$*" in
    "account show --query tenantId --output tsv") printf 'test-tenant\n' ;;
    "account show --query id --output tsv") printf 'test-subscription\n' ;;
    "group exists"*) printf 'false\n' ;;
    *) echo "Unexpected Azure command in test: $*" >&2; return 1 ;;
  esac
}

terraform() {
  if [[ "$1" == output ]]; then
    if [[ $# -eq 2 ]]; then
      if [[ "${PWD##*/}" == shared ]]; then
        cat <<'JSON'
{"sql_server_id":{"value":"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.Sql/servers/test-server"},"sql_server_name":{"value":"test-server"},"sql_database_name":{"value":"test-db"},"sql_server_fqdn":{"value":"test-server.database.windows.net"},"app_service_plan_id":{"value":"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.Web/serverfarms/test-plan"},"identity_stack_a_id":{"value":"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity-a"},"identity_stack_b_id":{"value":"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/identity-b"},"app_insights_id":{"value":"/subscriptions/test-subscription/resourceGroups/shared-monitoring-rg/providers/Microsoft.Insights/components/shared-insights"}}
JSON
      else
        printf '{"app_url":{"value":"https://%s.azurewebsites.net"}}\n' "${PWD##*/}"
      fi
      return 0
    fi
    case "$3" in
      app_insights_connection_string) printf '%s\n' "$MOCK_INSIGHTS_OUTPUT" ;;
      sql_server_id) printf '"/subscriptions/test-subscription/resourceGroups/test-rg/providers/Microsoft.Sql/servers/test-server"\n' ;;
      sql_server_name) printf '"test-server"\n' ;;
      sql_database_name) printf '"test-db"\n' ;;
      sql_server_fqdn) printf '"test-server.database.windows.net"\n' ;;
      *) printf '""\n' ;;
    esac
  elif [[ "$1" == plan && "${PWD##*/}" == stack-* ]]; then
    printf '%s|%s\n' "${PWD##*/}" "$TF_VAR_app_insights_connection_string" >> "$MONITORING_CAPTURE"
  fi
}

export -f az terraform

write_profile() {
  cat > "$profile" <<EOF
AZURE_TENANT_ID=test-tenant
AZURE_SUBSCRIPTION_ID=test-subscription
ENVIRONMENT=test
AZURE_LOCATION=australiaeast
RESOURCE_GROUP=app-rg
STACK_TARGET=both
APP_AUTH_MODE=simple
AWR_AUTH_MODE=none
AWR_SEQ_API_ENDPOINT=http://127.0.0.1:8080
AZ_SQL_REUSE=TRUE
SQL_SERVER_NAME=test-server
SQL_DATABASE_NAME=test-db
SQL_RG=test-rg
AZ_SQL_BOOTSTRAP_ENABLED=FALSE
AZ_KEY_VAULT_ENABLED=FALSE
AZ_KEY_VAULT_REUSE=FALSE
AZ_IDENTITIES_REUSE=FALSE
AZ_VNET_REUSE=FALSE
ENTRA_REUSE=FALSE
AZ_APP_INSIGHTS_ENABLED=$1
AZ_APP_INSIGHTS_REUSE=$2
AZ_APP_INSIGHTS_NAME=shared-insights
AZ_APP_INSIGHTS_RG=shared-monitoring-rg
EOF
  : > "$MONITORING_CAPTURE"
}

export MOCK_INSIGHTS_OUTPUT='"InstrumentationKey=00000000-0000-0000-0000-000000000002"'
for reuse in TRUE FALSE; do
  write_profile TRUE "$reuse"
  bash "$SCRIPT_DIR/../deploy.sh" "$profile" test apply both > "$deployment_log" 2>&1
  grep -Fxq 'stack-a|InstrumentationKey=00000000-0000-0000-0000-000000000002' "$MONITORING_CAPTURE"
  grep -Fxq 'stack-b|InstrumentationKey=00000000-0000-0000-0000-000000000002' "$MONITORING_CAPTURE"
  [[ "$(wc -l < "$MONITORING_CAPTURE" | tr -d '[:space:]')" == 2 ]]
done

write_profile FALSE FALSE
export TF_VAR_app_insights_connection_string=stale-destination
bash "$SCRIPT_DIR/../deploy.sh" "$profile" test apply both > "$deployment_log" 2>&1
grep -Fxq 'stack-a|' "$MONITORING_CAPTURE"
grep -Fxq 'stack-b|' "$MONITORING_CAPTURE"

write_profile TRUE TRUE
export MOCK_INSIGHTS_OUTPUT='""'
if bash "$SCRIPT_DIR/../deploy.sh" "$profile" test apply both > "$deployment_log" 2>&1; then
  echo "FAIL: enabled monitoring must reject a missing shared connection string." >&2
  exit 1
fi
grep -Fq 'Application Insights is enabled but its shared connection string is unavailable' "$deployment_log"
[[ ! -s "$MONITORING_CAPTURE" ]]

echo "PASS: both stacks receive the same created/reused monitoring destination; disabled and missing outputs are handled explicitly."
