#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_dir="$(mktemp -d)"
profile="$test_dir/customer profile.env"
original="$test_dir/original"
log="$test_dir/log"
export IDENTITY_SETUP_CALLS="$test_dir/calls"
export IDENTITY_SETUP_STATE="$test_dir/identities"
trap 'rm -f "$profile" "$original" "$log" "$IDENTITY_SETUP_CALLS" "$IDENTITY_SETUP_STATE"; rmdir "$test_dir"' EXIT

fail() {
  echo "FAIL: $*" >&2
  [[ ! -f "$log" ]] || cat "$log" >&2
  exit 1
}

az() {
  printf '%s\n' "$*" >> "$IDENTITY_SETUP_CALLS"
  local command="$1 $2" name="" group="" subscription="" query=""
  shift 2
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --name) name="$2"; shift 2 ;;
      --resource-group) group="$2"; shift 2 ;;
      --subscription) subscription="$2"; shift 2 ;;
      --query) query="$2"; shift 2 ;;
      --output|--location) shift 2 ;;
      *) echo "Unexpected Azure argument: $1" >&2; return 90 ;;
    esac
  done
  case "$command" in
    "account show")
      case "$query" in
        tenantId) printf '%s\n' "${MOCK_TENANT:-test-tenant}" ;;
        id) printf '%s\n' "${MOCK_SUBSCRIPTION:-test-subscription}" ;;
        *) return 91 ;;
      esac
      ;;
    "group exists")
      [[ "$subscription" == test-subscription ]] || return 92
      [[ "${MOCK_FAILURE:-}" != group ]] || { echo "Group lookup denied" >&2; return 1; }
      printf '%s\n' "${MOCK_GROUP_EXISTS:-true}"
      ;;
    "identity list")
      [[ "$subscription" == test-subscription ]] || return 92
      [[ "${MOCK_FAILURE:-}" != list ]] || { echo "Identity lookup denied" >&2; return 1; }
      [[ "$query" == '[].[name,id]' ]] || return 94
      local existing_group
      while IFS='|' read -r existing_group name; do
        [[ "$existing_group" == "$group" ]] || continue
        case "${MOCK_FAILURE:-}" in
          wrong-id) printf '%s\tunexpected-resource-id\n' "$name" ;;
          empty-id) printf '%s\t\n' "$name" ;;
          *)
            printf '%s\t/subscriptions/%s/resourceGroups/%s/providers/Microsoft.ManagedIdentity/userAssignedIdentities/%s\r\n' \
              "$name" "$subscription" "$group" "$name"
            ;;
        esac
      done < "$IDENTITY_SETUP_STATE"
      ;;
    "identity create")
      [[ "$subscription" == test-subscription ]] || return 92
      [[ "${MOCK_FAILURE:-}" != create ]] || { echo "Identity creation denied" >&2; return 1; }
      [[ "${MOCK_FAILURE:-}" != empty-create ]] || return 0
      printf '%s|%s\n' "$group" "$name" >> "$IDENTITY_SETUP_STATE"
      printf '/subscriptions/%s/resourceGroups/%s/providers/Microsoft.ManagedIdentity/userAssignedIdentities/%s\r\n' \
        "$subscription" "$group" "$name"
      ;;
    *) echo "Unexpected Azure command: $command" >&2; return 93 ;;
  esac
}
terraform() { echo "Terraform must not run during separate MI setup" >&2; return 99; }
export -f az terraform

write_profile() {
  unset MOCK_FAILURE MOCK_TENANT MOCK_SUBSCRIPTION MOCK_GROUP_EXISTS
  printf '%s\n' \
    'AZURE_TENANT_ID=test-tenant' \
    'AZURE_SUBSCRIPTION_ID=test-subscription' \
    'AZURE_LOCATION=australiaeast' \
    'ENVIRONMENT=test' \
    'PROJECT_NAME=talentmatch' \
    'RESOURCE_GROUP=app-rg' \
    "STACK_TARGET=$1" \
    'APP_AUTH_MODE=entra' \
    'ENTRA_REUSE=TRUE' \
    'AZ_IDENTITIES_REUSE=TRUE' \
    'UNRELATED_SECRET=sensitive-canary' > "$profile"
  : > "$IDENTITY_SETUP_CALLS"
  : > "$IDENTITY_SETUP_STATE"
}

run_setup() {
  cp "$profile" "$original"
  local result=0
  bash "$SCRIPT_DIR/../setup-identity.sh" "$profile" > "$log" 2>&1 || result=$?
  cmp -s "$profile" "$original" || fail "setup must not rewrite the deployment profile"
  if grep -Fq sensitive-canary "$log"; then
    fail "setup must not disclose unrelated configuration values"
  fi
  return "$result"
}

expect_failure() {
  if run_setup; then
    fail "setup unexpectedly succeeded"
  fi
  grep -Fq "$1" "$log" || fail "setup did not explain its failure"
  if grep -Fq 'identity create' "$IDENTITY_SETUP_CALLS"; then
    fail "a failed preflight must not create an identity"
  fi
}

write_profile both
run_setup || fail "setup should create both selected identities"
[[ "$(grep -c '^identity create ' "$IDENTITY_SETUP_CALLS")" == 2 ]] || fail "both identities must be created"
grep -Fxq AZ_IDENTITIES_REUSE=TRUE "$log" || fail "setup must provide the Terraform reuse setting"
grep -Fxq AZ_IDENTITIES_RG=app-rg "$log" || fail "setup must provide the resolved resource group"
grep -Fxq AZ_IDENTITY_STACK_A_NAME=id-talentmatch-node-test "$log" || fail "Stack A's default name must match Terraform"
grep -Fxq AZ_IDENTITY_STACK_B_NAME=id-talentmatch-blazor-test "$log" || fail "Stack B's default name must match Terraform"
grep -E '^AZ_(IDENTITIES_REUSE|IDENTITIES_RG|IDENTITY_STACK_[AB]_NAME)=' "$log" >> "$profile"
(
  source "$SCRIPT_DIR/../lib/common.sh"
  load_env_file "$profile"
  export APP_AUTH_MODE=simple AWR_AUTH_MODE=none AZ_SQL_REUSE=TRUE
  export SQL_SERVER_NAME=test-server SQL_DATABASE_NAME=test-db SQL_RG=sql-rg
  export_tf_vars
  validate_reuse_coordinates
  [[ "${TF_VAR_reuse_identities:-}" == true && "${TF_VAR_reuse_entra:-}" == true ]]
  [[ "${TF_VAR_existing_identity_stack_a_name:-}" == id-talentmatch-node-test ]]
  [[ "${TF_VAR_existing_identity_stack_b_name:-}" == id-talentmatch-blazor-test ]]
  [[ "${TF_VAR_existing_identities_rg:-}" == app-rg ]]
) > "$log" 2>&1 || fail "the emitted coordinates must validate and map to Terraform without changing Entra reuse"
: > "$IDENTITY_SETUP_CALLS"
run_setup || fail "a second setup must reuse the existing identities"
if grep -Fq 'identity create' "$IDENTITY_SETUP_CALLS"; then
  fail "a second setup must not recreate identities"
fi

for stack in a b; do
  write_profile "stack-$stack"
  printf 'AZ_IDENTITIES_RG=custom-identities-rg\nAZ_IDENTITY_STACK_%s_NAME=custom-selected-identity\n' "${stack^^}" >> "$profile"
  run_setup || fail "single-stack setup must accept custom identity coordinates"
  [[ "$(wc -l < "$IDENTITY_SETUP_STATE" | tr -d '[:space:]')" == 1 ]] || fail "only the selected identity must be created"
  grep -Fxq 'custom-identities-rg|custom-selected-identity' "$IDENTITY_SETUP_STATE" || fail "custom coordinates were ignored"
  other=a
  [[ "$stack" == a ]] && other=b
  if grep -Fq "AZ_IDENTITY_STACK_${other^^}_NAME=" "$log"; then
    fail "unselected stack coordinates must not be emitted"
  fi
done

write_profile shared-only
printf 'AZURE_TENANT_ID=\nAZURE_SUBSCRIPTION_ID=\n' >> "$profile"
run_setup || fail "shared-only must not need managed-identity setup"
[[ ! -s "$IDENTITY_SETUP_CALLS" ]] || fail "shared-only must not call Azure"

write_profile invalid
expect_failure "STACK_TARGET"
write_profile stack-b
printf 'AZ_IDENTITY_STACK_B_NAME=invalid/name\n' >> "$profile"
expect_failure "Invalid managed identity name"
write_profile both
printf 'AZ_IDENTITY_STACK_B_NAME=invalid/name\n' >> "$profile"
expect_failure "Invalid managed identity name"
write_profile stack-b
printf 'AZ_IDENTITIES_RG=\nRESOURCE_GROUP=\n' >> "$profile"
expect_failure "AZ_IDENTITIES_RG or RESOURCE_GROUP"
write_profile stack-b
export MOCK_TENANT=wrong-tenant
expect_failure "Active Azure tenant does not match"
write_profile stack-b
export MOCK_SUBSCRIPTION=wrong-subscription
expect_failure "Active Azure subscription does not match"
write_profile stack-b
export MOCK_GROUP_EXISTS=false
expect_failure "does not exist"
write_profile stack-b
export MOCK_GROUP_EXISTS=unexpected
expect_failure "Unexpected resource-group lookup response"
for error in group list; do
  write_profile stack-b
  export MOCK_FAILURE="$error"
  expect_failure "Unable to"
done

for error in wrong-id empty-id; do
  write_profile stack-b
  printf 'app-rg|id-talentmatch-blazor-test\n' > "$IDENTITY_SETUP_STATE"
  export MOCK_FAILURE="$error"
  expect_failure "Unexpected managed identity"
done

write_profile stack-b
printf 'AZ_IDENTITY_STACK_B_NAME=ID-TALENTMATCH-BLAZOR-TEST\n' >> "$profile"
printf 'app-rg|id-talentmatch-blazor-test\n' > "$IDENTITY_SETUP_STATE"
run_setup || fail "existing identity names must be matched case-insensitively"
if grep -Fq 'identity create' "$IDENTITY_SETUP_CALLS"; then
  fail "a case difference must not cause an existing identity to be recreated"
fi

for error in create empty-create; do
  write_profile stack-b
  export MOCK_FAILURE="$error"
  if run_setup; then
    fail "identity creation failures must not succeed"
  fi
  grep -Fq '[ERROR]' "$log" || fail "identity creation failures need an explicit error"
  if grep -Fxq AZ_IDENTITIES_REUSE=TRUE "$log"; then
    fail "failed setup must not emit a success-shaped configuration handoff"
  fi
done

write_profile stack-b
printf 'ENTRA_REUSE=FALSE\nAZ_IDENTITIES_REUSE=FALSE\n' >> "$profile"
run_setup || fail "explicit setup must be independent of app-registration and Terraform reuse flags"
grep -Fq 'ownership' "$log" || fail "Terraform create mode must produce an ownership warning"

echo "PASS: separate managed-identity setup is idempotent, stack-scoped, context-validated, explicit about failures, and leaves app registrations and profile ownership unchanged."
