#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

fail() {
  local exit_code="$1"
  shift
  log_error "$*"
  exit "$exit_code"
}

usage() {
  echo "Usage: $0 <env-file> check|apply" >&2
  exit 2
}

[[ $# -eq 2 ]] || usage
ENV_FILE="$1"
MODE="$2"
[[ "$MODE" == "check" || "$MODE" == "apply" ]] || usage

load_env_file "$ENV_FILE"
validate_required \
  "AZURE_TENANT_ID" \
  "AZURE_SUBSCRIPTION_ID" \
  "SQL_RG" \
  "SQL_SERVER_NAME" \
  "SQL_DATABASE_NAME" \
  "ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID" \
  "ENTRA_ADMIN_APP_ROLE_ID" \
  "ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID" \
  "ENTRA_BOOTSTRAP_ORGANIZATION_NAME" \
  "ENTRA_BOOTSTRAP_DEPARTMENT_NAME"
validate_azure_context

query_tsv() {
  local output
  if ! output="$(az "$@" --output tsv 2>/dev/null | tr -d '\r')"; then
    fail 4 "Azure validation failed while running: az $*"
  fi
  printf '%s' "$output"
}

# `az ad user show` omits accountEnabled/userType/mail from its default projection,
# so read the bootstrap principal through Graph with an explicit $select instead.
bootstrap_user_query() {
  query_tsv rest --method GET \
    --url "https://graph.microsoft.com/v1.0/users/$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID?\$select=id,accountEnabled,userType,userPrincipalName,displayName,mail" \
    --query "$1"
}

bootstrap_user_id="$(bootstrap_user_query id)"
bootstrap_user_enabled="$(bootstrap_user_query accountEnabled)"
bootstrap_user_type="$(bootstrap_user_query userType)"
bootstrap_username="$(bootstrap_user_query userPrincipalName)"
bootstrap_full_name="$(bootstrap_user_query displayName)"
bootstrap_email="$(bootstrap_user_query mail)"

[[ "$bootstrap_user_id" == "$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID" ]] \
  || fail 4 "The bootstrap object does not resolve to the configured tenant user."
[[ "${bootstrap_user_enabled,,}" == "true" ]] \
  || fail 4 "The bootstrap tenant user is disabled."
[[ "$bootstrap_user_type" == "Member" ]] \
  || fail 4 "The bootstrap principal must be a tenant member user."
[[ -n "$bootstrap_username" && -n "$bootstrap_full_name" ]] \
  || fail 4 "The bootstrap tenant user is missing a username or display name."
bootstrap_email="${bootstrap_email:-$bootstrap_username}"

sql_admin_object_id="$(query_tsv sql server ad-admin list \
  --resource-group "$SQL_RG" \
  --server "$SQL_SERVER_NAME" \
  --query '[0].sid')"
[[ "$sql_admin_object_id" == "$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID" ]] \
  || fail 4 "The configured bootstrap user is not the Azure SQL Microsoft Entra administrator."

admin_role_value="$(query_tsv ad sp show \
  --id "$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID" \
  --query "appRoles[?id=='$ENTRA_ADMIN_APP_ROLE_ID'] | [0].value")"
admin_role_enabled="$(query_tsv ad sp show \
  --id "$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID" \
  --query "appRoles[?id=='$ENTRA_ADMIN_APP_ROLE_ID'] | [0].isEnabled")"
[[ "$admin_role_value" == "admin" && "${admin_role_enabled,,}" == "true" ]] \
  || fail 4 "ENTRA_ADMIN_APP_ROLE_ID is not the enabled Admin role on the configured API service principal."

GRAPH_ASSIGNMENTS_URL="https://graph.microsoft.com/v1.0/servicePrincipals/$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID/appRoleAssignedTo"

graph_assignment_count() {
  query_tsv rest \
    --method get \
    --url "$GRAPH_ASSIGNMENTS_URL" \
    --query "length(value[?principalId=='$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID' && appRoleId=='$ENTRA_ADMIN_APP_ROLE_ID'])"
}

ensure_graph_assignment() {
  local count
  count="$(graph_assignment_count)"
  [[ "$count" == "0" ]] || {
    [[ "$count" == "1" ]] || fail 7 "Multiple direct Admin app-role assignments exist for the bootstrap user."
    return
  }

  local body
  body="{\"principalId\":\"$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID\",\"resourceId\":\"$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID\",\"appRoleId\":\"$ENTRA_ADMIN_APP_ROLE_ID\"}"
  az rest \
    --method post \
    --url "$GRAPH_ASSIGNMENTS_URL" \
    --headers 'Content-Type=application/json' \
    --body "$body" \
    --output none >/dev/null \
    || fail 5 "Microsoft Graph rejected the direct Admin app-role assignment."
}

# Git Bash callers must export MSYS_NO_PATHCONV=1 for ARM scope arguments, which also
# stops automatic path translation, so node would read "/c/..." as "C:\c\...".
to_native_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf '%s' "$1"
  fi
}

run_sql_seed() {
  local sql_mode="$1"
  local token_file output seed_script
  token_file="$(mktemp)"
  seed_script="$(to_native_path "$SCRIPT_DIR/seed-entra-admin-data.mjs")"
  if ! az account get-access-token \
    --resource 'https://database.windows.net/' \
    --query accessToken \
    --output tsv 2>/dev/null | tr -d '\r' > "$token_file"; then
    rm -f "$token_file"
    fail 6 "Unable to acquire an Azure SQL access token."
  fi

  if ! output="$(
    AZURE_TENANT_ID="$AZURE_TENANT_ID" \
    ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID="$ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID" \
    ENTRA_BOOTSTRAP_ADMIN_USERNAME="$bootstrap_username" \
    ENTRA_BOOTSTRAP_ADMIN_FULL_NAME="$bootstrap_full_name" \
    ENTRA_BOOTSTRAP_ADMIN_EMAIL="$bootstrap_email" \
    ENTRA_BOOTSTRAP_ORGANIZATION_NAME="$ENTRA_BOOTSTRAP_ORGANIZATION_NAME" \
    ENTRA_BOOTSTRAP_DEPARTMENT_NAME="$ENTRA_BOOTSTRAP_DEPARTMENT_NAME" \
    SQL_SERVER_FQDN="${SQL_SERVER_FQDN:-${SQL_SERVER_NAME}.database.windows.net}" \
    SQL_DATABASE_NAME="$SQL_DATABASE_NAME" \
    SQL_TOKEN_FILE="$(to_native_path "$token_file")" \
    node "$seed_script" "$sql_mode"
  )"; then
    rm -f "$token_file"
    fail 6 "The shared SQL bootstrap $sql_mode operation failed."
  fi
  rm -f "$token_file"
  printf '%s' "$output"
}

sql_is_ready() {
  node -e '
const result = JSON.parse(process.argv[1]);
const fields = [
  "userReady", "organizationReady", "departmentReady",
  "organizationMembershipReady", "departmentMembershipReady", "roleAssignmentReady",
];
process.stdout.write(fields.every(field => Boolean(result[field])) ? "true" : "false");
' "$1"
}

if [[ "$MODE" == "apply" ]]; then
  ensure_graph_assignment
  run_sql_seed apply >/dev/null
fi

graph_count="$(graph_assignment_count)"
sql_summary="$(run_sql_seed check)"
sql_ready="$(sql_is_ready "$sql_summary")"
graph_ready=false
[[ "$graph_count" == "1" ]] && graph_ready=true

printf '{"mode":"%s","graphAssignmentReady":%s,"sql":%s}\n' \
  "$MODE" "$graph_ready" "$sql_summary"

if [[ "$graph_ready" != "true" || "$sql_ready" != "true" ]]; then
  fail 7 "Bootstrap state is not converged. Run: $0 $ENV_FILE apply"
fi

log_success "Bootstrap Admin Graph and shared SQL authorization state is converged"