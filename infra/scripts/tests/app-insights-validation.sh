#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"

run_validation() (
  export APP_AUTH_MODE=simple AWR_AUTH_MODE=none STACK_TARGET=both
  export AZ_SQL_REUSE=TRUE SQL_SERVER_NAME=test-server SQL_DATABASE_NAME=test-db SQL_RG=test-rg
  export AZ_IDENTITIES_REUSE=FALSE AZ_KEY_VAULT_ENABLED=FALSE AZ_KEY_VAULT_REUSE=FALSE
  export AZ_APIM_ENABLED=FALSE AZ_VNET_REUSE=FALSE ENTRA_REUSE=FALSE
  export AZ_APP_INSIGHTS_ENABLED="$1" AZ_APP_INSIGHTS_REUSE="$2"
  export AZ_APP_INSIGHTS_NAME="$3" AZ_APP_INSIGHTS_RG="$4"
  validate_reuse_coordinates
)

expect_pass() {
  if ! run_validation "$@" >/dev/null 2>&1; then
    echo "FAIL: expected Application Insights validation to pass: $*" >&2
    exit 1
  fi
}

expect_fail() {
  if run_validation "$@" >/dev/null 2>&1; then
    echo "FAIL: expected Application Insights validation to fail: $*" >&2
    exit 1
  fi
}

expect_pass FALSE FALSE "" ""
expect_pass FALSE TRUE "" ""
expect_pass TRUE FALSE "" ""
expect_pass TRUE TRUE shared-insights shared-monitoring-rg
expect_pass true true shared-insights shared-monitoring-rg
expect_fail TRUE TRUE "" shared-monitoring-rg
expect_fail TRUE TRUE shared-insights ""
expect_fail ENABLED FALSE "" ""
expect_fail TRUE invalid "" ""

(
  export AZURE_TENANT_ID=test-tenant ENVIRONMENT=test AZURE_LOCATION=australiaeast RESOURCE_GROUP=app-rg
  export AZ_APP_INSIGHTS_ENABLED=TRUE AZ_APP_INSIGHTS_REUSE=TRUE
  export AZ_APP_INSIGHTS_NAME=shared-insights AZ_APP_INSIGHTS_RG=shared-monitoring-rg
  export_tf_vars >/dev/null
  [[ "$TF_VAR_enable_app_insights" == true ]]
  [[ "$TF_VAR_reuse_app_insights" == true ]]
  [[ "$TF_VAR_existing_app_insights_name" == shared-insights ]]
  [[ "$TF_VAR_existing_app_insights_rg" == shared-monitoring-rg ]]
)

echo "PASS: Application Insights enablement, reuse validation and profile mapping."
