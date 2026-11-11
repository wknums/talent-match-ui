#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"

run_validation() (
  export APP_AUTH_MODE=simple
  export AWR_AUTH_MODE=none
  export AZ_SQL_REUSE=TRUE
  export SQL_SERVER_NAME=test-server
  export SQL_DATABASE_NAME=test-db
  export SQL_RG=test-rg
  export AZ_IDENTITIES_REUSE=TRUE
  unset AZ_IDENTITY_STACK_A_NAME AZ_IDENTITY_STACK_B_NAME AZ_IDENTITIES_RG

  export STACK_TARGET="$1"
  [[ -n "$2" ]] && export AZ_IDENTITY_STACK_A_NAME="$2"
  [[ -n "$3" ]] && export AZ_IDENTITY_STACK_B_NAME="$3"
  [[ -n "$4" ]] && export AZ_IDENTITIES_RG="$4"
  validate_reuse_coordinates >/dev/null 2>&1
)

expect_pass() {
  if ! run_validation "$@"; then
    echo "FAIL: expected validation to pass: $*" >&2
    exit 1
  fi
}

expect_fail() {
  if run_validation "$@"; then
    echo "FAIL: expected validation to fail: $*" >&2
    exit 1
  fi
}

expect_pass stack-a id-stack-a "" identities-rg
expect_pass stack-b "" id-stack-b identities-rg
expect_pass both id-stack-a id-stack-b identities-rg
expect_pass shared-only "" "" ""

expect_fail stack-a "" id-stack-b identities-rg
expect_fail stack-b id-stack-a "" identities-rg
expect_fail both id-stack-a "" identities-rg
expect_fail both "" id-stack-b identities-rg
expect_fail stack-a id-stack-a "" ""
expect_fail invalid id-stack-a id-stack-b identities-rg

echo "PASS: managed identity reuse validation follows STACK_TARGET."
