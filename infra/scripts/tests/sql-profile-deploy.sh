#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"
test_dir="$(mktemp -d)"
profile="$test_dir/customer profile.env.azure"
original="$test_dir/original"
log="$test_dir/deployment-log"
export SQL_PROFILE_CAPTURE="$test_dir/terraform-calls"
trap 'echo "FAIL at line $LINENO: $BASH_COMMAND" >&2; [[ ! -f "$log" ]] || cat "$log" >&2' ERR
trap 'rm -f "$profile" "$original" "$log" "$SQL_PROFILE_CAPTURE"; rmdir "$test_dir"' EXIT

az() {
  case "$*" in
    "account show --query tenantId --output tsv") printf 'test-tenant\n' ;;
    "account show --query id --output tsv") printf 'test-subscription\n' ;;
    "group exists"*) printf 'false\n' ;;
    *) echo "Unexpected Azure command in test: $*" >&2; return 1 ;;
  esac
}

terraform() {
  printf '%s|%s\n' "${PWD##*/}" "$1" >> "$SQL_PROFILE_CAPTURE"
  if [[ "$1" == output ]]; then
    if [[ $# -eq 2 ]]; then
      if [[ "${PWD##*/}" == shared ]]; then
        node -e '
const resource = type => `/subscriptions/test-subscription/resourceGroups/sql-resource-rg/providers/${type}`;
const values = {
  sql_server_id: process.env.MOCK_SQL_SERVER_ID,
  sql_server_name: "deployed-server",
  sql_database_name: "deployed database",
  sql_server_fqdn: "deployed-server.database.windows.net",
  app_service_plan_id: resource("Microsoft.Web/serverfarms/test-plan"),
  identity_stack_a_id: resource("Microsoft.ManagedIdentity/userAssignedIdentities/identity-a"),
  identity_stack_b_id: resource("Microsoft.ManagedIdentity/userAssignedIdentities/identity-b"),
};
delete values[process.env.MOCK_MISSING_OUTPUT];
console.log(JSON.stringify(Object.fromEntries(Object.entries(values).map(([key, value]) => [key, {value}]))));
'
      else
        printf '{"app_url":{"value":"https://%s.azurewebsites.net"}}\n' "${PWD##*/}"
      fi
      return 0
    fi
    [[ "$3" != "${MOCK_MISSING_OUTPUT:-}" ]] || return 1
    case "$3" in
      sql_server_id) printf '"%s"\n' "$MOCK_SQL_SERVER_ID" ;;
      sql_server_name) printf '"deployed-server"\n' ;;
      sql_database_name) printf '"deployed database"\n' ;;
      sql_server_fqdn) printf '"deployed-server.database.windows.net"\n' ;;
      app_service_plan_id) printf '"test-plan"\n' ;;
      *) printf '""\n' ;;
    esac
  elif [[ "$1" == apply && "${MOCK_APPLY_FAIL:-false}" == true ]]; then
    return 1
  fi
}
export -f az terraform

write_profile() {
  cat > "$profile" <<EOF
# Customer deployment profile
AZURE_TENANT_ID=test-tenant
AZURE_SUBSCRIPTION_ID=test-subscription
ENVIRONMENT=test
AZURE_LOCATION=australiaeast
RESOURCE_GROUP=application-rg
STACK_TARGET=both
APP_AUTH_MODE=simple
AWR_AUTH_MODE=none
AWR_SEQ_API_ENDPOINT=http://127.0.0.1:8080
AZ_SQL_REUSE=$1
ALLOW_CREATE_AZURE_SQL=TRUE
SQL_AAD_ADMIN_LOGIN=admin@example.com
SQL_AAD_ADMIN_OBJECT_ID=test-admin
AZ_SQL_BOOTSTRAP_ENABLED=FALSE
AZ_KEY_VAULT_ENABLED=FALSE
AZ_KEY_VAULT_REUSE=FALSE
AZ_IDENTITIES_REUSE=FALSE
AZ_VNET_REUSE=FALSE
ENTRA_REUSE=FALSE
AZ_APP_INSIGHTS_ENABLED=FALSE
AZ_APP_INSIGHTS_REUSE=FALSE
# Preserve comments and unrelated settings verbatim.
UNRELATED_SECRET="sensitive-canary=do-not-log"
EOF
  if [[ "$1" == TRUE ]]; then
    printf 'SQL_RG=old-rg\nSQL_SERVER_NAME=old-server\nSQL_DATABASE_NAME=old-db\n' >> "$profile"
  fi
  export MOCK_SQL_SERVER_ID='/subscriptions/test-subscription/resourceGroups/sql-resource-rg/providers/Microsoft.Sql/servers/deployed-server'
  unset MOCK_MISSING_OUTPUT MOCK_APPLY_FAIL
  : > "$SQL_PROFILE_CAPTURE"
}

assert_coordinates() (
  unset SQL_RG SQL_SERVER_NAME SQL_DATABASE_NAME SQL_SERVER_FQDN
  load_env_file "$profile" > /dev/null
  [[ "$SQL_RG" == sql-resource-rg ]]
  [[ "$SQL_SERVER_NAME" == deployed-server ]]
  [[ "$SQL_DATABASE_NAME" == 'deployed database' ]]
  [[ "$SQL_SERVER_FQDN" == deployed-server.database.windows.net ]]
  [[ "$AZ_SQL_REUSE" == "$1" ]]
  [[ "$UNRELATED_SECRET" == sensitive-canary=do-not-log ]]
  ! grep -Fq sensitive-canary "$log"
)

deploy() {
  bash "$SCRIPT_DIR/../deploy.sh" "$profile" test "$1" "$2" > "$log" 2>&1
}

for reuse in FALSE TRUE; do
  for target in shared-only stack-a stack-b both; do
    write_profile "$reuse"
    mode_before="$(stat -c '%a' "$profile")"
    deploy apply "$target"
    assert_coordinates "$reuse"
    [[ "$(stat -c '%a' "$profile")" == "$mode_before" ]]
    for key in SQL_RG SQL_SERVER_NAME SQL_DATABASE_NAME SQL_SERVER_FQDN; do
      [[ "$(grep -Ec "^$key=" "$profile")" == 1 ]]
    done
    grep -Fxq '# Preserve comments and unrelated settings verbatim.' "$profile"
    grep -Fxq 'UNRELATED_SECRET="sensitive-canary=do-not-log"' "$profile"
    cp "$profile" "$original"
    deploy apply "$target"
    cmp -s "$profile" "$original"
  done
done

write_profile FALSE
printf 'SQL_RG=\n SQL_SERVER_NAME = stale-server\nSQL_DATABASE_NAME=\nSQL_SERVER_FQDN=stale.example.com\nSQL_RG=duplicate-stale-rg\n' >> "$profile"
deploy apply shared-only
assert_coordinates FALSE
[[ "$(grep -Ec '^SQL_RG=' "$profile")" == 2 ]]
! grep -Eq 'stale|SQL_DATABASE_NAME=$' "$profile"

write_profile FALSE
printf 'UNRELATED_LAST=keep-this' >> "$profile"
deploy apply shared-only
assert_coordinates FALSE
grep -Fxq UNRELATED_LAST=keep-this "$profile"

write_profile FALSE
sed 's/$/\r/' "$profile" > "$original"
cp "$original" "$profile"
deploy apply shared-only
assert_coordinates FALSE
! grep -qv $'\r$' "$profile"

for target in shared-only stack-a stack-b both stack-b-only; do
  write_profile FALSE
  cp "$profile" "$original"
  deploy plan "$target"
  cmp -s "$profile" "$original"
done

write_profile FALSE
cp "$profile" "$original"
deploy apply stack-b-only
grep -Fxq 'E2E_STACK_B_BASE_URL="https://stack-b.azurewebsites.net"' "$profile"
! grep -Eq '^SQL_(RG|SERVER_NAME|DATABASE_NAME)=' "$profile"

for output in sql_server_id sql_server_name sql_database_name sql_server_fqdn; do
  write_profile FALSE
  cp "$profile" "$original"
  export MOCK_MISSING_OUTPUT="$output"
  if deploy apply stack-b; then
    echo "FAIL: missing $output must stop deployment." >&2
    exit 1
  fi
  grep -Fq "$output" "$log"
  cmp -s "$profile" "$original"
  ! grep -Fq 'stack-b|apply' "$SQL_PROFILE_CAPTURE"
done

for server_id in \
  invalid-resource-id \
  /subscriptions/other-subscription/resourceGroups/sql-resource-rg/providers/Microsoft.Sql/servers/deployed-server \
  /subscriptions/test-subscription/resourceGroups/sql-resource-rg/providers/Microsoft.Sql/servers/other-server; do
  write_profile FALSE
  cp "$profile" "$original"
  export MOCK_SQL_SERVER_ID="$server_id"
  if deploy apply shared-only; then
    echo "FAIL: an invalid or inconsistent SQL resource ID must stop deployment." >&2
    exit 1
  fi
  grep -Fq 'SQL server resource ID' "$log"
  cmp -s "$profile" "$original"
done

write_profile FALSE
cp "$profile" "$original"
export MOCK_APPLY_FAIL=true
if deploy apply shared-only; then
  echo "FAIL: a failed Terraform apply must stop deployment." >&2
  exit 1
fi
cmp -s "$profile" "$original"

echo "PASS: shared apply persists SQL coordinates for bootstrap; plans, ownership flags, unrelated settings, and failed deployments are preserved."
