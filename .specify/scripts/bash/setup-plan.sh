#!/usr/bin/env bash
# Setup implementation plan for a feature
#
# Usage: ./setup-plan.sh [OPTIONS]
#
# OPTIONS:
#   --json     Output results in JSON format
#   --help     Show help message

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Parse arguments
JSON=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --json|-Json)  JSON=true; shift ;;
        --help|-h|-Help)
            echo "Usage: ./setup-plan.sh [--json] [--help]"
            echo "  --json     Output results in JSON format"
            echo "  --help     Show this help message"
            exit 0
            ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

# Get all paths and variables from common functions
get_feature_paths_env

# Check if we're on a proper feature branch (only for git repos)
if ! test_feature_branch "$CURRENT_BRANCH" "$HAS_GIT"; then
    exit 1
fi

# Ensure the feature directory exists
mkdir -p "$FEATURE_DIR"

# Copy plan template if it exists, otherwise note it or create empty file
template="$REPO_ROOT/.specify/templates/plan-template.md"
if [ -f "$template" ]; then
    cp "$template" "$IMPL_PLAN"
    echo "Copied plan template to $IMPL_PLAN"
else
    echo "WARNING: Plan template not found at $template" >&2
    touch "$IMPL_PLAN"
fi

# Output results
if [ "$JSON" = true ]; then
    printf '{"FEATURE_SPEC":"%s","IMPL_PLAN":"%s","SPECS_DIR":"%s","BRANCH":"%s","HAS_GIT":%s}\n' \
        "$FEATURE_SPEC" "$IMPL_PLAN" "$FEATURE_DIR" "$CURRENT_BRANCH" "$HAS_GIT"
else
    echo "FEATURE_SPEC: $FEATURE_SPEC"
    echo "IMPL_PLAN: $IMPL_PLAN"
    echo "SPECS_DIR: $FEATURE_DIR"
    echo "BRANCH: $CURRENT_BRANCH"
    echo "HAS_GIT: $HAS_GIT"
fi
