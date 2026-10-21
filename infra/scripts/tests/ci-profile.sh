#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"
test_dir="$(mktemp -d)"
profile="$test_dir/.env_qa"
log="$test_dir/log"
output="$test_dir/github-output"
export CI_TARGET_CAPTURE="$test_dir/deploy-calls"
trap 'rm -f "$profile" "$profile.example" "$log" "$output" "$CI_TARGET_CAPTURE"; rmdir "$test_dir"' EXIT

export DEPLOYMENT_ENV_FILE=$'ENVIRONMENT=test\r\nSTACK_TARGET=stack-b\r\nRESOURCE_GROUP=operator-selected-group\r\nAZURE_TENANT_ID=test-tenant\r\nAZURE_SUBSCRIPTION_ID=test-subscription'
write_ci_env_file "$profile"
[[ -f "$profile" && -z "${DEPLOYMENT_ENV_FILE:-}" ]]
load_env_file "$profile" > "$log"
[[ "$RESOURCE_GROUP" == operator-selected-group ]]
[[ "$(tail -n 1 "$profile")" == AZURE_SUBSCRIPTION_ID=test-subscription ]]

for missing_secret in "" "   "; do
  if (export DEPLOYMENT_ENV_FILE="$missing_secret"; write_ci_env_file "$profile") > "$log" 2>&1; then
    echo "FAIL: an empty CI profile secret must not use example coordinates." >&2
    exit 1
  fi
  grep -Fq DEPLOYMENT_ENV_FILE "$log"
  ! grep -Fq operator-selected-group "$log"
done

if (export DEPLOYMENT_ENV_FILE='ENVIRONMENT=test'; write_ci_env_file "$test_dir") > "$log" 2>&1; then
  echo "FAIL: CI profile write errors must propagate to the caller." >&2
  exit 1
fi
grep -Fq "Unable to write the CI deployment profile" "$log"

printf 'ENVIRONMENT=test\nSTACK_TARGET=both\n' > "$profile.example"
(
  cd "$test_dir"
  export DEPLOYMENT_ENV_FILE=$'ENVIRONMENT=test\nSTACK_TARGET=stack-b'
  GITHUB_ACTIONS=true GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/develop \
    CHANGED_SHARED=false CHANGED_STACK_A=true CHANGED_STACK_B=true GITHUB_OUTPUT="$output" \
    bash "$SCRIPT_DIR/../resolve-targets.sh" > "$log" 2>&1
)
grep -Fxq deploy_stack_a=false "$output"
grep -Fxq deploy_stack_b=true "$output"
if (
  cd "$test_dir"
  export DEPLOYMENT_ENV_FILE=$'ENVIRONMENT=prod\nSTACK_TARGET=stack-b'
  GITHUB_ACTIONS=true GITHUB_EVENT_NAME=push GITHUB_REF=refs/heads/develop \
    bash "$SCRIPT_DIR/../resolve-targets.sh"
) > "$log" 2>&1; then
  echo "FAIL: CI must reject a profile for a different environment." >&2
  exit 1
fi

az() {
  case "$*" in
    "account show --query tenantId --output tsv") printf 'test-tenant\n' ;;
    "account show --query id --output tsv") printf 'test-subscription\n' ;;
    "webapp deploy "*)
      [[ "$*" == *"--resource-group operator-selected-group"* ]]
      [[ "$*" == *"--name operator-selected-$EXPECTED_STACK"* ]]
      [[ "$*" == *"--subscription test-subscription"* ]]
      printf '%s\n' "$EXPECTED_STACK" >> "$CI_TARGET_CAPTURE"
      ;;
    *) echo "Unexpected Azure command in test" >&2; return 1 ;;
  esac
}
terraform() {
  [[ "$*" == "output -json app_name" && "$TF_WORKSPACE" == test-subscription-test ]]
  if [[ "${MISSING_APP_NAME:-false}" != true ]]; then
    printf '"operator-selected-%s"\n' "${PWD##*/}"
  fi
}
export -f az terraform

cd "$REPO_ROOT"
export DEPLOYMENT_ENV_FILE=$'ENVIRONMENT=test\nSTACK_TARGET=both\nRESOURCE_GROUP=operator-selected-group\nAZURE_TENANT_ID=test-tenant\nAZURE_SUBSCRIPTION_ID=test-subscription'
write_ci_env_file "$profile"
for stack in a b; do
  export EXPECTED_STACK="stack-$stack" ENV_FILE="$profile"
  run="$(node --input-type=module -e '
    import fs from "node:fs";
    import yaml from "js-yaml";
    const workflow = yaml.load(fs.readFileSync(".github/workflows/selective-azure-deploy.yml", "utf8"));
    const job = workflow.jobs[`deploy-stack-${process.argv[1]}`];
    console.log(job.steps.find(step => step.name === "Deploy artifact to App Service").run);
  ' "$stack")"
  bash -c "$run" > "$log" 2>&1 || { cat "$log" >&2; exit 1; }
  grep -Fxq "$EXPECTED_STACK" "$CI_TARGET_CAPTURE"
  : > "$CI_TARGET_CAPTURE"
  if MISSING_APP_NAME=true bash -c "$run" > "$log" 2>&1; then
    echo "FAIL: missing Terraform targets must not fall back to example names." >&2
    exit 1
  fi
  [[ ! -s "$CI_TARGET_CAPTURE" ]]
done

echo "PASS: CI profiles are explicit, environment-matched, and workflow targets come from the profile and selected Terraform workspace."
