#!/usr/bin/env bash
# Consolidated prerequisite checking script
#
# Usage: ./check-prerequisites.sh [OPTIONS]
#
# OPTIONS:
#   --json               Output in JSON format
#   --require-tasks      Require tasks.md to exist (for implementation phase)
#   --include-tasks      Include tasks.md in AVAILABLE_DOCS list
#   --paths-only         Only output path variables (no validation)
#   --help, -h           Show help message

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Parse arguments
JSON=false
REQUIRE_TASKS=false
INCLUDE_TASKS=false
PATHS_ONLY=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --json|-Json)       JSON=true; shift ;;
        --require-tasks|-RequireTasks) REQUIRE_TASKS=true; shift ;;
        --include-tasks|-IncludeTasks) INCLUDE_TASKS=true; shift ;;
        --paths-only|-PathsOnly)       PATHS_ONLY=true; shift ;;
        --help|-h|-Help)
            cat <<'EOF'
Usage: check-prerequisites.sh [OPTIONS]

Consolidated prerequisite checking for Spec-Driven Development workflow.

OPTIONS:
  --json               Output in JSON format
  --require-tasks      Require tasks.md to exist (for implementation phase)
  --include-tasks      Include tasks.md in AVAILABLE_DOCS list
  --paths-only         Only output path variables (no prerequisite validation)
  --help, -h           Show this help message

EXAMPLES:
  # Check task prerequisites (plan.md required)
  ./check-prerequisites.sh --json

  # Check implementation prerequisites (plan.md + tasks.md required)
  ./check-prerequisites.sh --json --require-tasks --include-tasks

  # Get feature paths only (no validation)
  ./check-prerequisites.sh --paths-only
EOF
            exit 0
            ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# Get feature paths
get_feature_paths_env

if ! test_feature_branch "$CURRENT_BRANCH" "$HAS_GIT"; then
    exit 1
fi

# If paths-only mode, output paths and exit
if [ "$PATHS_ONLY" = true ]; then
    if [ "$JSON" = true ]; then
        printf '{"REPO_ROOT":"%s","BRANCH":"%s","FEATURE_DIR":"%s","FEATURE_SPEC":"%s","IMPL_PLAN":"%s","TASKS":"%s"}\n' \
            "$REPO_ROOT" "$CURRENT_BRANCH" "$FEATURE_DIR" "$FEATURE_SPEC" "$IMPL_PLAN" "$TASKS"
    else
        echo "REPO_ROOT: $REPO_ROOT"
        echo "BRANCH: $CURRENT_BRANCH"
        echo "FEATURE_DIR: $FEATURE_DIR"
        echo "FEATURE_SPEC: $FEATURE_SPEC"
        echo "IMPL_PLAN: $IMPL_PLAN"
        echo "TASKS: $TASKS"
    fi
    exit 0
fi

# Validate required directories and files
if [ ! -d "$FEATURE_DIR" ]; then
    echo "ERROR: Feature directory not found: $FEATURE_DIR"
    echo "Run /speckit.specify first to create the feature structure."
    exit 1
fi

if [ ! -f "$IMPL_PLAN" ]; then
    echo "ERROR: plan.md not found in $FEATURE_DIR"
    echo "Run /speckit.plan first to create the implementation plan."
    exit 1
fi

# Check for tasks.md if required
if [ "$REQUIRE_TASKS" = true ] && [ ! -f "$TASKS" ]; then
    echo "ERROR: tasks.md not found in $FEATURE_DIR"
    echo "Run /speckit.tasks first to create the task list."
    exit 1
fi

# Build list of available documents
docs=()

if [ -f "$RESEARCH" ]; then docs+=("research.md"); fi
if [ -f "$DATA_MODEL" ]; then docs+=("data-model.md"); fi
if [ -d "$CONTRACTS_DIR" ] && [ -n "$(find "$CONTRACTS_DIR" -maxdepth 1 -type f 2>/dev/null | head -1)" ]; then
    docs+=("contracts/")
fi
if [ -f "$QUICKSTART" ]; then docs+=("quickstart.md"); fi
if [ "$INCLUDE_TASKS" = true ] && [ -f "$TASKS" ]; then
    docs+=("tasks.md")
fi

# Output results
if [ "$JSON" = true ]; then
    # Build JSON array for docs
    docs_json="["
    first=true
    for doc in "${docs[@]}"; do
        if [ "$first" = true ]; then
            first=false
        else
            docs_json+=","
        fi
        docs_json+="\"$doc\""
    done
    docs_json+="]"
    printf '{"FEATURE_DIR":"%s","AVAILABLE_DOCS":%s}\n' "$FEATURE_DIR" "$docs_json"
else
    echo "FEATURE_DIR:$FEATURE_DIR"
    echo "AVAILABLE_DOCS:"
    test_file_exists "$RESEARCH" "research.md" || true
    test_file_exists "$DATA_MODEL" "data-model.md" || true
    test_dir_has_files "$CONTRACTS_DIR" "contracts/" || true
    test_file_exists "$QUICKSTART" "quickstart.md" || true
    if [ "$INCLUDE_TASKS" = true ]; then
        test_file_exists "$TASKS" "tasks.md" || true
    fi
fi
