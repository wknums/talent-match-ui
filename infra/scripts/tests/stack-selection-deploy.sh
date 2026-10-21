#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_dir="$(mktemp -d)"
export STACK_SELECTION_CAPTURE="$test_dir/terraform-calls"
profile="$test_dir/.env_qa"
log="$test_dir/deployment-log"
output="$test_dir/github-output"
trap 'echo "FAIL at line $LINENO: $BASH_COMMAND" >&2; [[ ! -f "$log" ]] || cat "$log" >&2' ERR
trap 'rm -f "$profile" "$log" "$output" "$STACK_SELECTION_CAPTURE"; rmdir "$test_dir"' EXIT

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
    case "$3" in
      app_service_plan_id) printf '"test-app-service-plan"\n' ;;
      *) printf '""\n' ;;
    esac
  elif [[ "$1" == plan ]]; then
    printf '%s|%s|%s\n' "${PWD##*/}" "$TF_VAR_stack_target" "$TF_VAR_app_auth_mode" >> "$STACK_SELECTION_CAPTURE"
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
STACK_TARGET=$1
APP_AUTH_MODE=entra
ENTRA_REUSE=TRUE
ENTRA_API_APP_CLIENT_ID=api-client
ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=api-principal
ENTRA_API_IDENTIFIER_URI=api://test-api
ENTRA_API_SCOPE_ID=00000000-0000-0000-0000-000000000010
ENTRA_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000011
ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000012
ENTRA_RECRUITER_APP_ROLE_ID=00000000-0000-0000-0000-000000000013
ENTRA_BUSINESS_PANEL_APP_ROLE_ID=00000000-0000-0000-0000-000000000014
ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID=bootstrap-user
ENTRA_STACK_A_CLIENT_ID=$2
ENTRA_STACK_B_CLIENT_ID=$3
ENTRA_STACK_A_REDIRECT_URIS=$4
ENTRA_STACK_B_REDIRECT_URIS=$5
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
AZ_APP_INSIGHTS_ENABLED=FALSE
AZ_APP_INSIGHTS_REUSE=FALSE
EOF
  : > "$STACK_SELECTION_CAPTURE"
  : > "$output"
}

for stack in a b; do
  if [[ "$stack" == a ]]; then
    write_profile stack-a client-a "" https://stack-a.example.com/ ""
  else
    write_profile stack-b "" client-b "" https://stack-b.example.com/
  fi
  bash "$SCRIPT_DIR/../deploy.sh" "$profile" test plan "stack-$stack" > "$log" 2>&1 || { cat "$log" >&2; exit 1; }
  grep -Fxq "shared|stack-$stack|entra" "$STACK_SELECTION_CAPTURE"
  grep -Fxq "stack-$stack|stack-$stack|entra" "$STACK_SELECTION_CAPTURE"
  [[ "$(wc -l < "$STACK_SELECTION_CAPTURE" | tr -d '[:space:]')" == 2 ]]

  : > "$STACK_SELECTION_CAPTURE"
  if bash "$SCRIPT_DIR/../deploy.sh" "$profile" test plan both > "$log" 2>&1; then
    echo "FAIL: a deployment command must not override a single-stack profile." >&2
    exit 1
  fi
  grep -Fq "not enabled by STACK_TARGET=stack-$stack" "$log"
  [[ ! -s "$STACK_SELECTION_CAPTURE" ]]

  (
    cd "$test_dir"
    GITHUB_EVENT_NAME=workflow_dispatch INPUT_ENVIRONMENT=staging INPUT_TARGET="stack-$stack" \
      GITHUB_OUTPUT="$output" bash "$SCRIPT_DIR/../resolve-targets.sh" > "$log" 2>&1
  ) || { cat "$log" >&2; exit 1; }
  grep -Fxq "deploy_stack_$stack=true" "$output"
done

write_profile stack-b "" client-b "" https://stack-b.example.com/
(
  cd "$test_dir"
  GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/develop CHANGED_SHARED=false \
    CHANGED_STACK_A=true CHANGED_STACK_B=true GITHUB_OUTPUT="$output" \
    bash "$SCRIPT_DIR/../resolve-targets.sh" > "$log" 2>&1
)
grep -Fxq "deploy_stack_a=false" "$output"
grep -Fxq "package_stack_a=false" "$output"
grep -Fxq "deploy_stack_b=true" "$output"

: > "$output"
(
  cd "$test_dir"
  GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/develop CHANGED_SHARED=false \
    CHANGED_STACK_A=true CHANGED_STACK_B=false GITHUB_OUTPUT="$output" \
    bash "$SCRIPT_DIR/../resolve-targets.sh" > "$log" 2>&1
)
grep -Fxq "deploy_shared=false" "$output"
grep -Fxq "deploy_stack_a=false" "$output"
grep -Fxq "deploy_stack_b=false" "$output"

for script in deploy.sh deploy-stack-a-app.sh; do
  if [[ "$script" == deploy.sh ]]; then
    args=("$profile" test plan stack-a)
  else
    args=("$profile")
  fi
  if bash "$SCRIPT_DIR/../$script" "${args[@]}" > "$log" 2>&1; then
    echo "FAIL: $script must refuse a disabled stack." >&2
    exit 1
  fi
  grep -Fq "not enabled by STACK_TARGET=stack-b" "$log"
done

write_profile stack-a client-a "" https://stack-a.example.com/ ""
if bash "$SCRIPT_DIR/../deploy-stack-b-app.sh" "$profile" > "$log" 2>&1; then
  echo "FAIL: Stack B artifact deployment must refuse a Stack A-only profile." >&2
  exit 1
fi
grep -Fq "not enabled by STACK_TARGET=stack-a" "$log"

echo "PASS: deployment roots, artifact guards and GitHub targets respect the selected environment profile."
