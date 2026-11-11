#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../lib/common.sh"
test_dir="$(mktemp -d)"
profile="$test_dir/customer profile.env.azure"
log="$test_dir/log"
trap 'rm -f "$profile" "$log"; rmdir "$test_dir"' EXIT

fail() {
  echo "FAIL: $*" >&2
  [[ ! -f "$log" ]] || cat "$log" >&2
  exit 1
}

expect_invalid_entry() {
  local entry="$1" reason="$2" ending="${3:-newline}"
  printf '# heading\n\n   # indented comment\nVALID_BEFORE=present\n' > "$profile"
  printf '%s' "$entry" >> "$profile"
  if [[ "$ending" == newline ]]; then
    printf '\nVALID_AFTER=must-not-load\n' >> "$profile"
  fi

  if (load_env_file "$profile"; echo UNEXPECTED_CONTINUATION) > "$log" 2>&1; then
    fail "a malformed environment entry was accepted"
  fi
  grep -Fq "$profile:5:" "$log" || fail "the error must identify the profile and physical line number ($reason)"
  grep -Fq "$reason" "$log" || fail "the error must explain the entry's problem"
  if grep -Eq 'sensitive-canary|UNEXPECTED_CONTINUATION|not a valid identifier|xargs:' "$log"; then
    fail "the loader leaked entry contents or continued after an invalid entry"
  fi
}

expect_invalid_entry '=sensitive-canary' 'invalid variable name'
expect_invalid_entry '   =sensitive-canary' 'invalid variable name'
expect_invalid_entry 'BAD-KEY=sensitive-canary' 'invalid variable name'
expect_invalid_entry 'BAD KEY=sensitive-canary' 'invalid variable name'
expect_invalid_entry '1BAD=sensitive-canary' 'invalid variable name'
expect_invalid_entry 'sensitive-canary' 'expected KEY=VALUE'
expect_invalid_entry "BAD'KEY=sensitive-canary" 'cannot parse variable name'
expect_invalid_entry 'TOKEN="sensitive-canary' 'cannot parse value'
expect_invalid_entry "TOKEN='sensitive-canary" 'cannot parse value'
expect_invalid_entry 'UID=sensitive-canary' "cannot export variable 'UID'"
expect_invalid_entry 'BAD-KEY=sensitive-canary' 'invalid variable name' eof
expect_invalid_entry 'sensitive-canary' 'expected KEY=VALUE' eof

(
  printf '%s\r\n' \
    '# heading' \
    '' \
    '   ' \
    '   # indented comment' \
    ' ENV_TEST_PLAIN = simple ' \
    'ENV_TEST_DOUBLE="two words"' \
    "ENV_TEST_SINGLE='two words'" \
    'ENV_TEST_EMPTY=' \
    'ENV_TEST_EQUALS=alpha=beta==' \
    'ENV_TEST_LITERAL=$(printf should-not-run)' \
    'ENV_TEST_DUPLICATE=old' \
    'ENV_TEST_DUPLICATE=new' > "$profile"
  printf 'ENV_TEST_FINAL=last-value' >> "$profile"
  load_env_file "$profile" > "$log"
  [[ "$ENV_TEST_PLAIN" == simple ]] || fail "whitespace trimming changed"
  [[ "$ENV_TEST_DOUBLE" == 'two words' && "$ENV_TEST_SINGLE" == 'two words' ]] \
    || fail "quoted values changed"
  [[ "${ENV_TEST_EMPTY+x}" == x && -z "$ENV_TEST_EMPTY" ]] || fail "empty values must be exported"
  [[ "$ENV_TEST_EQUALS" == 'alpha=beta==' ]] || fail "equals signs in values must be preserved"
  [[ "$ENV_TEST_LITERAL" == '$(printf should-not-run)' ]] || fail "values must not be evaluated"
  [[ "$ENV_TEST_DUPLICATE" == new ]] || fail "later assignments must still win"
  [[ "$ENV_TEST_FINAL" == last-value ]] || fail "the final line must load without a newline"
  bash -c '[[ "$ENV_TEST_PLAIN" == simple && "$ENV_TEST_FINAL" == last-value ]]' \
    || fail "loaded values must be exported to child processes"
)

(
  printf '   \r\n# comment\r\n\r\n' > "$profile"
  load_env_file "$profile" > "$log"
)

az() { echo UNEXPECTED_AZURE_CALL; return 99; }
terraform() { echo UNEXPECTED_TERRAFORM_CALL; return 99; }
export -f az terraform
printf '# customer profile\n\n=sensitive-canary\n' > "$profile"
if bash "$SCRIPT_DIR/../deploy.sh" "$profile" test apply stack-b > "$log" 2>&1; then
  fail "deployment must reject a malformed profile"
fi
grep -Fq "$profile:3:" "$log" || fail "deployment must report the malformed profile line"
if grep -Eq 'sensitive-canary|UNEXPECTED_AZURE_CALL|UNEXPECTED_TERRAFORM_CALL' "$log"; then
  fail "deployment must stop before cloud commands without disclosing values"
fi

echo "PASS: environment-file errors identify the file, line, and reason without exposing values; valid profiles retain their loading behavior."
