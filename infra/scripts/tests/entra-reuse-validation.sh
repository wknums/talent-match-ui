#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"

run_validation() (
  export APP_AUTH_MODE="${8:-entra}"
  export AWR_AUTH_MODE=none
  export ENTRA_REUSE="$2"
  export AZ_SQL_REUSE=TRUE SQL_SERVER_NAME=test-server SQL_DATABASE_NAME=test-db SQL_RG=test-rg
  export AZ_IDENTITIES_REUSE=FALSE AZ_VNET_REUSE=FALSE AZ_APP_SERVICE_PLAN_REUSE=FALSE
  export AZ_KEY_VAULT_ENABLED=FALSE AZ_KEY_VAULT_REUSE=FALSE AZ_APIM_ENABLED=FALSE
  export AZ_APP_INSIGHTS_ENABLED=FALSE AZ_APP_INSIGHTS_REUSE=FALSE
  export ENTRA_API_APP_CLIENT_ID=api-client ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID=api-principal
  export ENTRA_API_IDENTIFIER_URI=api://test-api ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID=bootstrap-user
  export ENTRA_API_SCOPE_ID=00000000-0000-0000-0000-000000000010
  export ENTRA_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000011
  export ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000012
  export ENTRA_RECRUITER_APP_ROLE_ID=00000000-0000-0000-0000-000000000013
  export ENTRA_BUSINESS_PANEL_APP_ROLE_ID=00000000-0000-0000-0000-000000000014
  export STACK_TARGET="$1" ENTRA_STACK_A_CLIENT_ID="$3" ENTRA_STACK_B_CLIENT_ID="$4"
  export ENTRA_STACK_A_REDIRECT_URIS="$5" ENTRA_STACK_B_REDIRECT_URIS="$6"
  if [[ "$7" == missing-api ]]; then
    unset ENTRA_API_APP_CLIENT_ID
  fi
  case "${9:-}" in
    missing-scope) unset ENTRA_API_SCOPE_ID ;;
    partial-roles) unset ENTRA_BUSINESS_PANEL_APP_ROLE_ID ;;
    invalid-scope) export ENTRA_API_SCOPE_ID=not-a-uuid ;;
    invalid-role) export ENTRA_RECRUITER_APP_ROLE_ID=not-a-uuid ;;
    no-identifiers)
      unset ENTRA_API_SCOPE_ID ENTRA_ADMIN_APP_ROLE_ID ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID \
        ENTRA_RECRUITER_APP_ROLE_ID ENTRA_BUSINESS_PANEL_APP_ROLE_ID
      ;;
  esac
  validate_reuse_coordinates
)

expect_pass() {
  if ! run_validation "$@" > /dev/null; then
    echo "FAIL: expected validation to pass: $*" >&2
    exit 1
  fi
}

expect_fail() {
  if run_validation "$@" > /dev/null 2>&1; then
    echo "FAIL: expected validation to fail: $*" >&2
    exit 1
  fi
}

expect_pass stack-a TRUE client-a "" https://stack-a.example.com/ "" present
expect_pass stack-b TRUE "" client-b "" https://stack-b.example.com/ present
expect_pass both TRUE client-a client-b https://stack-a.example.com/ https://stack-b.example.com/ present
expect_pass stack-a FALSE "" "" https://stack-a.example.com/ "" present
expect_pass stack-b FALSE "" "" "" https://stack-b.example.com/ present
expect_pass shared-only TRUE "" "" "" "" present
expect_pass stack-a TRUE "" "" "" "" missing-api simple

expect_fail stack-a TRUE "" client-b https://stack-a.example.com/ "" present
expect_fail stack-b TRUE client-a "" "" https://stack-b.example.com/ present
expect_fail both TRUE client-a "" https://stack-a.example.com/ https://stack-b.example.com/ present
expect_fail stack-a FALSE "" "" "" https://stack-b.example.com/ present
expect_fail stack-b FALSE "" "" https://stack-a.example.com/ "" present
expect_fail stack-a TRUE client-a "" https://stack-a.example.com/ "" missing-api
expect_fail invalid TRUE client-a client-b https://stack-a.example.com/ https://stack-b.example.com/ present

for failure in missing-scope partial-roles invalid-scope invalid-role; do
  expect_fail stack-a TRUE client-a "" https://stack-a.example.com/ "" present entra "$failure"
  expect_fail stack-a FALSE "" "" https://stack-a.example.com/ "" present entra "$failure"
done
expect_pass stack-a TRUE "" "" "" "" missing-api simple no-identifiers

(
  export AZURE_TENANT_ID=test-tenant ENVIRONMENT=test AZURE_LOCATION=australiaeast RESOURCE_GROUP=app-rg
  export ENTRA_API_SCOPE_ID=00000000-0000-0000-0000-000000000010
  export ENTRA_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000011
  export ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID=00000000-0000-0000-0000-000000000012
  export ENTRA_RECRUITER_APP_ROLE_ID=00000000-0000-0000-0000-000000000013
  export ENTRA_BUSINESS_PANEL_APP_ROLE_ID=00000000-0000-0000-0000-000000000014
  export_tf_vars >/dev/null
  [[ "$TF_VAR_entra_api_scope_id" == "$ENTRA_API_SCOPE_ID" ]]
  [[ "$TF_VAR_entra_app_role_ids" == *"\"recruiter\":\"$ENTRA_RECRUITER_APP_ROLE_ID\""* ]]
  unset ENTRA_API_SCOPE_ID ENTRA_ADMIN_APP_ROLE_ID ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID \
    ENTRA_RECRUITER_APP_ROLE_ID ENTRA_BUSINESS_PANEL_APP_ROLE_ID
  export APP_AUTH_MODE=simple
  export_tf_vars >/dev/null
  [[ -z "$TF_VAR_entra_api_scope_id" && "$TF_VAR_entra_app_role_ids" == "{}" ]]
)

echo "PASS: Entra identifiers are explicit, validated, mapped without stale defaults, and optional only in simple-auth mode."
