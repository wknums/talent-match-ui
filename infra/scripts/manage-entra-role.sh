#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

EXIT_ARGUMENTS=2
EXIT_CONTEXT=3
EXIT_VALIDATION=4
EXIT_GRAPH=5
EXIT_SQL=6
EXIT_PARTIAL=7

fail() {
  local exit_code="$1"
  local phase="$2"
  shift 2
  printf '{"status":"failed","phase":"%s","exitCode":%d}\n' "$phase" "$exit_code"
  log_error "[$phase] $*"
  exit "$exit_code"
}

usage() {
  cat >&2 <<'EOF'
Usage:
  manage-entra-role.sh <env-file> map-group --group-object-id <uuid> --role <role> [--organization-id <uuid>] [--department-id <uuid>]
  manage-entra-role.sh <env-file> assign --user-object-id <uuid> --mapping-id <uuid> --default-department-id <uuid>
  manage-entra-role.sh <env-file> check --user-object-id <uuid>
  manage-entra-role.sh <env-file> revoke --user-object-id <uuid> --assignment-id <uuid>
EOF
  exit "$EXIT_ARGUMENTS"
}

[[ $# -ge 2 ]] || usage
ENV_FILE="$1"
COMMAND="$2"
shift 2
[[ "$COMMAND" == "map-group" || "$COMMAND" == "assign" || "$COMMAND" == "check" || "$COMMAND" == "revoke" ]] || usage
[[ -f "$ENV_FILE" ]] || fail "$EXIT_ARGUMENTS" arguments "Environment file not found: $ENV_FILE"

GROUP_OBJECT_ID=""
ROLE=""
ORGANIZATION_ID=""
DEPARTMENT_ID=""
USER_OBJECT_ID=""
MAPPING_ID=""
DEFAULT_DEPARTMENT_ID=""
ASSIGNMENT_ID=""

while [[ $# -gt 0 ]]; do
  [[ $# -ge 2 && "$1" == --* && "$2" != --* ]] || usage
  case "$1" in
    --group-object-id) GROUP_OBJECT_ID="$2" ;;
    --role) ROLE="$2" ;;
    --organization-id) ORGANIZATION_ID="$2" ;;
    --department-id) DEPARTMENT_ID="$2" ;;
    --user-object-id) USER_OBJECT_ID="$2" ;;
    --mapping-id) MAPPING_ID="$2" ;;
    --default-department-id) DEFAULT_DEPARTMENT_ID="$2" ;;
    --assignment-id) ASSIGNMENT_ID="$2" ;;
    *) usage ;;
  esac
  shift 2
done

uuid_pattern='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
require_uuid() {
  local option_name="$1"
  local value="$2"
  [[ "$value" =~ $uuid_pattern ]] || fail "$EXIT_ARGUMENTS" arguments "$option_name must be a UUID."
}

case "$COMMAND" in
  map-group)
    require_uuid "--group-object-id" "$GROUP_OBJECT_ID"
    [[ -n "$ROLE" ]] || fail "$EXIT_ARGUMENTS" arguments "--role is required."
    case "$ROLE" in
      admin)
        [[ -z "$ORGANIZATION_ID" && -z "$DEPARTMENT_ID" ]] \
          || fail "$EXIT_ARGUMENTS" arguments "Role admin forbids organization and department scope."
        ;;
      organization_admin)
        require_uuid "--organization-id" "$ORGANIZATION_ID"
        [[ -z "$DEPARTMENT_ID" ]] \
          || fail "$EXIT_ARGUMENTS" arguments "Role organization_admin forbids department scope."
        ;;
      recruiter)
        require_uuid "--organization-id" "$ORGANIZATION_ID"
        require_uuid "--department-id" "$DEPARTMENT_ID"
        ;;
      business_panel)
        require_uuid "--organization-id" "$ORGANIZATION_ID"
        [[ -z "$DEPARTMENT_ID" ]] || require_uuid "--department-id" "$DEPARTMENT_ID"
        ;;
      *) fail "$EXIT_ARGUMENTS" arguments "Unsupported role: $ROLE" ;;
    esac
    ;;
  assign)
    require_uuid "--user-object-id" "$USER_OBJECT_ID"
    require_uuid "--mapping-id" "$MAPPING_ID"
    require_uuid "--default-department-id" "$DEFAULT_DEPARTMENT_ID"
    ;;
  check)
    require_uuid "--user-object-id" "$USER_OBJECT_ID"
    ;;
  revoke)
    require_uuid "--user-object-id" "$USER_OBJECT_ID"
    require_uuid "--assignment-id" "$ASSIGNMENT_ID"
    ;;
esac

load_env_file "$ENV_FILE"

required_configuration=(
  AZURE_TENANT_ID AZURE_SUBSCRIPTION_ID SQL_RG SQL_SERVER_NAME SQL_DATABASE_NAME
  ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID ENTRA_ADMIN_APP_ROLE_ID
  ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID ENTRA_RECRUITER_APP_ROLE_ID
  ENTRA_BUSINESS_PANEL_APP_ROLE_ID
)
for variable_name in "${required_configuration[@]}"; do
  [[ -n "${!variable_name:-}" ]] \
    || fail "$EXIT_ARGUMENTS" configuration "Missing required environment variable: $variable_name"
done

AZURE_TENANT_ID="$(printf '%s' "$AZURE_TENANT_ID" | tr -d '\r')"
AZURE_SUBSCRIPTION_ID="$(printf '%s' "$AZURE_SUBSCRIPTION_ID" | tr -d '\r')"
if ! (validate_azure_context); then
  fail "$EXIT_CONTEXT" context "Active Azure tenant/subscription does not exactly match the environment profile. Authenticate to the declared context and rerun the same command."
fi

query_json() {
  local phase="$1"
  shift
  local output
  if ! output="$(az "$@" --output json 2>/dev/null | tr -d '\r')"; then
    fail "$EXIT_VALIDATION" "$phase" "Azure validation failed while running: az $*"
  fi
  printf '%s' "$output"
}

query_tsv() {
  local phase="$1"
  shift
  local output
  if ! output="$(az "$@" --output tsv 2>/dev/null | tr -d '\r')"; then
    fail "$EXIT_VALIDATION" "$phase" "Azure validation failed while running: az $*"
  fi
  printf '%s' "$output"
}

query_graph_json() {
  local output
  if ! output="$(az rest "$@" --output json 2>/dev/null | tr -d '\r')"; then
    fail "$EXIT_GRAPH" graph-read "Microsoft Graph read failed. Resolve Graph permissions or availability and rerun the same command."
  fi
  printf '%s' "$output"
}

query_graph_tsv() {
  local output
  if ! output="$(az rest "$@" --output tsv 2>/dev/null | tr -d '\r')"; then
    fail "$EXIT_GRAPH" graph-read "Microsoft Graph read failed. Resolve Graph permissions or availability and rerun the same command."
  fi
  printf '%s' "$output"
}

json_field() {
  node -e '
const value = JSON.parse(process.argv[1]);
const path = process.argv[2].split(".");
let current = value;
for (const segment of path) current = current?.[segment];
if (current !== undefined && current !== null) process.stdout.write(String(current));
' "$1" "$2"
}

json_value() {
  node -e 'process.stdout.write(JSON.stringify(process.argv[1]))' "$1"
}

role_id_for() {
  case "$1" in
    admin) printf '%s' "$ENTRA_ADMIN_APP_ROLE_ID" ;;
    organization_admin) printf '%s' "$ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID" ;;
    recruiter) printf '%s' "$ENTRA_RECRUITER_APP_ROLE_ID" ;;
    business_panel) printf '%s' "$ENTRA_BUSINESS_PANEL_APP_ROLE_ID" ;;
    *) fail "$EXIT_ARGUMENTS" arguments "Unsupported role: $1" ;;
  esac
}

validate_group() {
  local group_id="$1"
  local group_json resolved_id security_enabled group_types
  group_json="$(query_json group-validation ad group show --group "$group_id")"
  resolved_id="$(json_field "$group_json" id)"
  security_enabled="$(json_field "$group_json" securityEnabled)"
  group_types="$(json_field "$group_json" groupTypes)"
  [[ "$resolved_id" == "$group_id" && "${security_enabled,,}" == "true" ]] \
    || fail "$EXIT_VALIDATION" group-validation "The configured group must be an app-specific tenant security group."
  [[ "$group_types" != *DynamicMembership* ]] \
    || fail "$EXIT_VALIDATION" group-validation "Dynamic groups are not supported; direct user membership is required."
}

validate_user() {
  local user_id="$1"
  local user_json resolved_id account_enabled user_type username full_name email
  user_json="$(query_json user-validation ad user show --id "$user_id")"
  resolved_id="$(json_field "$user_json" id)"
  account_enabled="$(json_field "$user_json" accountEnabled)"
  user_type="$(json_field "$user_json" userType)"
  username="$(json_field "$user_json" userPrincipalName)"
  full_name="$(json_field "$user_json" displayName)"
  email="$(json_field "$user_json" mail)"
  [[ "$resolved_id" == "$user_id" && "${account_enabled,,}" == "true" && "$user_type" == "Member" ]] \
    || fail "$EXIT_VALIDATION" user-validation "The target must be an enabled tenant member user."
  [[ -n "$username" && -n "$full_name" ]] \
    || fail "$EXIT_VALIDATION" user-validation "The target user is missing immutable presentation identity."
  ENTRA_TARGET_USERNAME="$username"
  ENTRA_TARGET_FULL_NAME="$full_name"
  ENTRA_TARGET_EMAIL="${email:-$username}"
}

validate_app_role() {
  local role="$1"
  local role_id="$2"
  local role_json resolved_id role_value role_enabled
  role_json="$(query_json role-validation ad sp show \
    --id "$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID" \
    --query "{id:appRoles[?id=='$role_id'] | [0].id,value:appRoles[?id=='$role_id'] | [0].value,isEnabled:appRoles[?id=='$role_id'] | [0].isEnabled}")"
  resolved_id="$(json_field "$role_json" id)"
  role_value="$(json_field "$role_json" value)"
  role_enabled="$(json_field "$role_json" isEnabled)"
  [[ "$resolved_id" == "$role_id" && "$role_value" == "$role" && "${role_enabled,,}" == "true" ]] \
    || fail "$EXIT_VALIDATION" role-validation "The configured app-role ID does not match enabled role $role."
}

group_role_assignment_count() {
  local group_id="$1"
  local role_id="$2"
  query_graph_tsv --method get \
    --url "https://graph.microsoft.com/v1.0/servicePrincipals/$ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID/appRoleAssignedTo" \
    --query "length(value[?principalId=='$group_id' && appRoleId=='$role_id'])"
}

require_group_role_assignment() {
  local group_id="$1"
  local role_id="$2"
  local count
  count="$(group_role_assignment_count "$group_id" "$role_id")"
  [[ "$count" == "1" ]] \
    || fail "$EXIT_VALIDATION" graph-validation "The security group must have exactly one assignment to the expected API app role."
}

graph_write() {
  local method="$1"
  local url="$2"
  shift 2
  if ! az rest --method "$method" --url "$url" "$@" --output none >/dev/null 2>&1; then
    fail "$EXIT_GRAPH" graph-mutation "Microsoft Graph $method operation failed. Rerun the same command after resolving Graph permissions or availability."
  fi
}

direct_membership_count() {
  local group_id="$1"
  local user_id="$2"
  query_graph_tsv --method get \
    --url "https://graph.microsoft.com/v1.0/groups/$group_id/members?\$select=id" \
    --query "length(value[?id=='$user_id'])"
}

ensure_direct_group_membership() {
  local group_id="$1"
  local user_id="$2"
  local count body
  count="$(direct_membership_count "$group_id" "$user_id")"
  [[ "$count" == "0" || "$count" == "1" ]] \
    || fail "$EXIT_VALIDATION" graph-validation "Direct group membership returned an invalid duplicate state."
  if [[ "$count" == "0" ]]; then
    body="{\"@odata.id\":\"https://graph.microsoft.com/v1.0/directoryObjects/$user_id\"}"
    graph_write post "https://graph.microsoft.com/v1.0/groups/$group_id/members/\$ref" \
      --headers 'Content-Type=application/json' --body "$body"
  fi
}

run_sql() {
  local operation="$1"
  shift
  local token_file output helper token_file_native
  token_file="$(mktemp)"
  helper="${ENTRA_ROLE_DATA_HELPER:-$SCRIPT_DIR/manage-entra-role-data.mjs}"
  # Git Bash callers must export MSYS_NO_PATHCONV=1 for ARM scope arguments, which also
  # stops automatic path translation, so node would read "/c/..." as "C:\c\...".
  if command -v cygpath >/dev/null 2>&1; then
    helper="$(cygpath -w "$helper")"
    token_file_native="$(cygpath -w "$token_file")"
  else
    token_file_native="$token_file"
  fi
  if ! az account get-access-token \
    --resource 'https://database.windows.net/' \
    --query accessToken --output tsv 2>/dev/null | tr -d '\r' > "$token_file"; then
    rm -f "$token_file"
    fail "$EXIT_SQL" sql-token "Unable to acquire an Azure SQL access token."
  fi

  if ! output="$(
    AZURE_TENANT_ID="$AZURE_TENANT_ID" \
    ENTRA_ROLE_OPERATOR_OBJECT_ID="${ENTRA_ROLE_OPERATOR_OBJECT_ID:-operator-cli}" \
    ENTRA_TARGET_USERNAME="${ENTRA_TARGET_USERNAME:-}" \
    ENTRA_TARGET_FULL_NAME="${ENTRA_TARGET_FULL_NAME:-}" \
    ENTRA_TARGET_EMAIL="${ENTRA_TARGET_EMAIL:-}" \
    SQL_SERVER_FQDN="${SQL_SERVER_FQDN:-${SQL_SERVER_NAME}.database.windows.net}" \
    SQL_DATABASE_NAME="$SQL_DATABASE_NAME" \
    SQL_TOKEN_FILE="$token_file_native" \
    node "$helper" "$operation" "$@"
  )"; then
    rm -f "$token_file"
    fail "$EXIT_SQL" sql-mutation "Shared SQL $operation failed. Rerun the same command after resolving SQL access or validation."
  fi
  rm -f "$token_file"
  printf '%s' "$output"
}

inspect_mapping() {
  run_sql inspect-mapping \
    --mapping-id "$MAPPING_ID" \
    --default-department-id "$DEFAULT_DEPARTMENT_ID"
}

case "$COMMAND" in
  map-group)
    validate_group "$GROUP_OBJECT_ID"
    ROLE_ID="$(role_id_for "$ROLE")"
    validate_app_role "$ROLE" "$ROLE_ID"
    require_group_role_assignment "$GROUP_OBJECT_ID" "$ROLE_ID"
    SQL_SUMMARY="$(run_sql map-group \
      --group-object-id "$GROUP_OBJECT_ID" \
      --role "$ROLE" \
      --organization-id "$ORGANIZATION_ID" \
      --department-id "$DEPARTMENT_ID")"
    MAPPING_ID="$(json_field "$SQL_SUMMARY" mappingId)"
    CONVERGED="$(json_field "$SQL_SUMMARY" converged)"
    printf '{"command":"map-group","mappingId":%s,"groupObjectId":%s,"role":%s,"converged":%s}\n' \
      "$(json_value "$MAPPING_ID")" "$(json_value "$GROUP_OBJECT_ID")" \
      "$(json_value "$ROLE")" "${CONVERGED:-false}"
    ;;

  assign)
    validate_user "$USER_OBJECT_ID"
    MAPPING_SUMMARY="$(inspect_mapping)"
    MAPPING_TENANT_ID="$(json_field "$MAPPING_SUMMARY" tenantId)"
    GROUP_OBJECT_ID="$(json_field "$MAPPING_SUMMARY" groupObjectId)"
    ROLE="$(json_field "$MAPPING_SUMMARY" role)"
    ORGANIZATION_ID="$(json_field "$MAPPING_SUMMARY" organizationId)"
    DEPARTMENT_ID="$(json_field "$MAPPING_SUMMARY" departmentId)"
    MAPPING_ENABLED="$(json_field "$MAPPING_SUMMARY" enabled)"
    DEFAULT_READY="$(json_field "$MAPPING_SUMMARY" defaultDepartmentReady)"
    [[ "$MAPPING_TENANT_ID" == "$AZURE_TENANT_ID" && "${MAPPING_ENABLED,,}" == "true" && "${DEFAULT_READY,,}" == "true" ]] \
      || fail "$EXIT_VALIDATION" mapping-validation "The mapping is disabled, belongs to another tenant, or rejects the explicit default."
    validate_group "$GROUP_OBJECT_ID"
    ROLE_ID="$(role_id_for "$ROLE")"
    validate_app_role "$ROLE" "$ROLE_ID"
    require_group_role_assignment "$GROUP_OBJECT_ID" "$ROLE_ID"
    ensure_direct_group_membership "$GROUP_OBJECT_ID" "$USER_OBJECT_ID"
    SQL_SUMMARY="$(run_sql assign \
      --user-object-id "$USER_OBJECT_ID" \
      --mapping-id "$MAPPING_ID" \
      --default-department-id "$DEFAULT_DEPARTMENT_ID")"
    ASSIGNMENT_ID="$(json_field "$SQL_SUMMARY" assignmentId)"
    VERIFY_SUMMARY="$(run_sql check-assignment \
      --user-object-id "$USER_OBJECT_ID" \
      --mapping-id "$MAPPING_ID" \
      --default-department-id "$DEFAULT_DEPARTMENT_ID")"
    CONVERGED="$(json_field "$VERIFY_SUMMARY" converged)"
    GRAPH_MEMBERSHIP_COUNT="$(direct_membership_count "$GROUP_OBJECT_ID" "$USER_OBJECT_ID")"
    [[ "${CONVERGED,,}" == "true" && "$GRAPH_MEMBERSHIP_COUNT" == "1" ]] \
      || fail "$EXIT_PARTIAL" postcondition "Assignment planes did not converge. Rerun the same assign command."
    printf '{"command":"assign","userObjectId":%s,"mappingId":%s,"assignmentId":%s,"defaultDepartmentId":%s,"converged":%s}\n' \
      "$(json_value "$USER_OBJECT_ID")" "$(json_value "$MAPPING_ID")" \
      "$(json_value "$ASSIGNMENT_ID")" "$(json_value "$DEFAULT_DEPARTMENT_ID")" \
      "${CONVERGED:-false}"
    ;;

  check)
    validate_user "$USER_OBJECT_ID"
    SQL_SUMMARY="$(run_sql check --user-object-id "$USER_OBJECT_ID")"
    GROUP_MEMBERSHIPS="$(query_graph_json --method get \
      --url "https://graph.microsoft.com/v1.0/users/$USER_OBJECT_ID/memberOf/microsoft.graph.group?\$select=id,securityEnabled")"
    APP_ROLE_ASSIGNMENTS="$(query_graph_json --method get \
      --url "https://graph.microsoft.com/v1.0/users/$USER_OBJECT_ID/appRoleAssignments?\$select=resourceId,appRoleId")"
    printf '{"command":"check","userObjectId":%s,"sql":%s,"directGroupMemberships":%s,"appRoleAssignments":%s,"changed":false}\n' \
      "$(json_value "$USER_OBJECT_ID")" "$SQL_SUMMARY" "$GROUP_MEMBERSHIPS" "$APP_ROLE_ASSIGNMENTS"
    ;;

  revoke)
    validate_user "$USER_OBJECT_ID"
    SQL_SUMMARY="$(run_sql revoke \
      --user-object-id "$USER_OBJECT_ID" \
      --assignment-id "$ASSIGNMENT_ID")"
    GROUP_OBJECT_ID="$(json_field "$SQL_SUMMARY" groupObjectId)"
    ACTIVE_DEPENDENCIES="$(json_field "$SQL_SUMMARY" activeGroupDependencyCount)"
    SQL_REVOKED="$(json_field "$SQL_SUMMARY" sqlRevoked)"
    [[ "${SQL_REVOKED,,}" == "true" ]] \
      || fail "$EXIT_SQL" sql-revocation "The targeted SQL assignment did not converge to revoked."

    GROUP_MEMBERSHIP_REMOVED=false
    if [[ "$ACTIVE_DEPENDENCIES" == "0" && -n "$GROUP_OBJECT_ID" ]]; then
      MEMBERSHIP_COUNT="$(direct_membership_count "$GROUP_OBJECT_ID" "$USER_OBJECT_ID")"
      if [[ "$MEMBERSHIP_COUNT" == "1" ]]; then
        if ! az rest --method delete \
          --url "https://graph.microsoft.com/v1.0/groups/$GROUP_OBJECT_ID/members/$USER_OBJECT_ID/\$ref" \
          --output none >/dev/null 2>&1; then
          fail "$EXIT_PARTIAL" graph-cleanup "SQL access is revoked, but Graph group cleanup failed. Rerun the same revoke command to converge."
        fi
        GROUP_MEMBERSHIP_REMOVED=true
      fi
    fi

    GRAPH_MEMBERSHIP_COUNT="$(direct_membership_count "$GROUP_OBJECT_ID" "$USER_OBJECT_ID")"
    if [[ "$ACTIVE_DEPENDENCIES" == "0" && "$GRAPH_MEMBERSHIP_COUNT" != "0" ]]; then
      fail "$EXIT_PARTIAL" postcondition "SQL is revoked, but direct group membership remains. Rerun the same revoke command."
    fi
    if [[ "$ACTIVE_DEPENDENCIES" != "0" && "$GRAPH_MEMBERSHIP_COUNT" != "1" ]]; then
      fail "$EXIT_PARTIAL" postcondition "Another active assignment depends on group membership, but that membership is missing."
    fi

    VERIFY_SUMMARY="$(run_sql check-revoke \
      --user-object-id "$USER_OBJECT_ID" \
      --assignment-id "$ASSIGNMENT_ID")"
    VERIFY_REVOKED="$(json_field "$VERIFY_SUMMARY" sqlRevoked)"
    [[ "${VERIFY_REVOKED,,}" == "true" ]] \
      || fail "$EXIT_PARTIAL" postcondition "Targeted SQL revocation could not be verified. Rerun the same revoke command."
    printf '{"command":"revoke","userObjectId":%s,"assignmentId":%s,"sqlRevoked":true,"groupMembershipRemoved":%s,"converged":true}\n' \
      "$(json_value "$USER_OBJECT_ID")" "$(json_value "$ASSIGNMENT_ID")" "$GROUP_MEMBERSHIP_REMOVED"
    ;;
esac