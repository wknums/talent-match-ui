#!/usr/bin/env bash
# Setup implementation plan for a feature
#
# Usage: ./setup-plan.sh [OPTIONS] [FEATURE_NAME]
#
# OPTIONS:
#   --json              Output results in JSON format
#   --feature <name>    Specify the feature name (e.g. 001-talent-matching-platform)
#   --help              Show help message
#
# FEATURE_NAME (positional): If a non-option argument is provided, it is treated
#   as the feature name. This takes precedence over the SPECIFY_FEATURE env var
#   and the current git branch.
#
# Examples:
#   SPECIFY_FEATURE=001-my-feature bash .specify/scripts/bash/setup-plan.sh --json
#   bash .specify/scripts/bash/setup-plan.sh --json --feature 001-my-feature
#   bash .specify/scripts/bash/setup-plan.sh --json 001-my-feature

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Parse arguments
JSON=false
FEATURE_ARG=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --json|-Json)  JSON=true; shift ;;
        --feature)
            if [[ -n "${2:-}" && ! "$2" =~ ^- ]]; then
                FEATURE_ARG="$2"; shift 2
            else
                echo "ERROR: --feature requires a feature name argument" >&2; exit 1
            fi
            ;;
        --help|-h|-Help)
            echo "Usage: ./setup-plan.sh [--json] [--feature <name>] [--help] [FEATURE_NAME]"
            echo "  --json              Output results in JSON format"
            echo "  --feature <name>    Specify the feature name explicitly"
            echo "  --help              Show this help message"
            echo "  FEATURE_NAME        Positional feature name (alternative to --feature)"
            echo ""
            echo "Examples:"
            echo "  bash .specify/scripts/bash/setup-plan.sh --json --feature 001-my-feature"
            echo "  bash .specify/scripts/bash/setup-plan.sh --json 001-my-feature"
            echo "  SPECIFY_FEATURE=001-my-feature bash .specify/scripts/bash/setup-plan.sh --json"
            exit 0
            ;;
        -*) echo "Unknown option: $1" >&2; exit 1 ;;
        *)  FEATURE_ARG="$1"; shift ;;
    esac
done

# If a feature was provided via argument, set it as the override
if [ -n "$FEATURE_ARG" ]; then
    export SPECIFY_FEATURE="$FEATURE_ARG"
fi

# Get all paths and variables from common functions
get_feature_paths_env

# Check if we're on a proper feature branch (only for git repos)
if ! test_feature_branch "$CURRENT_BRANCH" "$HAS_GIT"; then
    exit 1
fi

# Ensure the feature directory exists
mkdir -p "$FEATURE_DIR"

# Copy plan template if it exists, otherwise create file with header stub
template="$REPO_ROOT/.specify/templates/plan-template.md"
if [ -f "$template" ]; then
    cp "$template" "$IMPL_PLAN"
    echo "Copied plan template to $IMPL_PLAN"
else
    echo "WARNING: Plan template not found at $template" >&2
    echo "# Implementation Plan — $CURRENT_BRANCH" > "$IMPL_PLAN"
fi

# Ensure other artifact files exist with header stubs (prevents empty-file edit failures)
for artifact_var in RESEARCH DATA_MODEL QUICKSTART; do
    artifact_path="${!artifact_var}"
    if [ ! -f "$artifact_path" ]; then
        artifact_name="$(basename "$artifact_path" .md)"
        echo "# ${artifact_name^} — $CURRENT_BRANCH" > "$artifact_path"
    fi
done

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
