#!/usr/bin/env bash
# Update agent context files with information from plan.md
#
# Mirrors the behavior of the PowerShell version:
#  1. Environment Validation
#  2. Plan Data Extraction
#  3. Agent File Management (create from template or update existing)
#  4. Content Generation (technology stack, recent changes, timestamp)
#  5. Multi-Agent Support
#
# Usage: ./update-agent-context.sh [agent-type]
#
# agent-type: claude|gemini|copilot|cursor-agent|qwen|opencode|codex|windsurf|kilocode|auggie|roo|codebuddy|amp|shai|q|agy|bob|qodercli|generic

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

AGENT_TYPE="${1:-}"

# Acquire environment paths
get_feature_paths_env
NEW_PLAN="$IMPL_PLAN"

# Agent file paths
CLAUDE_FILE="$REPO_ROOT/CLAUDE.md"
GEMINI_FILE="$REPO_ROOT/GEMINI.md"
COPILOT_FILE="$REPO_ROOT/.github/agents/copilot-instructions.md"
CURSOR_FILE="$REPO_ROOT/.cursor/rules/specify-rules.mdc"
QWEN_FILE="$REPO_ROOT/QWEN.md"
AGENTS_FILE="$REPO_ROOT/AGENTS.md"
WINDSURF_FILE="$REPO_ROOT/.windsurf/rules/specify-rules.md"
KILOCODE_FILE="$REPO_ROOT/.kilocode/rules/specify-rules.md"
AUGGIE_FILE="$REPO_ROOT/.augment/rules/specify-rules.md"
ROO_FILE="$REPO_ROOT/.roo/rules/specify-rules.md"
CODEBUDDY_FILE="$REPO_ROOT/CODEBUDDY.md"
QODER_FILE="$REPO_ROOT/QODER.md"
AMP_FILE="$REPO_ROOT/AGENTS.md"
SHAI_FILE="$REPO_ROOT/SHAI.md"
Q_FILE="$REPO_ROOT/AGENTS.md"
AGY_FILE="$REPO_ROOT/.agent/rules/specify-rules.md"
BOB_FILE="$REPO_ROOT/AGENTS.md"

TEMPLATE_FILE="$REPO_ROOT/.specify/templates/agent-file-template.md"

# Parsed plan data
NEW_LANG=""
NEW_FRAMEWORK=""
NEW_DB=""
NEW_PROJECT_TYPE=""

info()    { echo "INFO: $1"; }
success() { echo "✓ $1"; }
warn_msg(){ echo "WARNING: $1" >&2; }
err_msg() { echo "ERROR: $1" >&2; }

validate_environment() {
    if [ -z "$CURRENT_BRANCH" ]; then
        err_msg "Unable to determine current feature"
        if [ "$HAS_GIT" = "true" ]; then
            info "Make sure you're on a feature branch"
        else
            info "Set SPECIFY_FEATURE environment variable or create a feature first"
        fi
        exit 1
    fi
    if [ ! -f "$NEW_PLAN" ]; then
        err_msg "No plan.md found at $NEW_PLAN"
        info "Ensure you are working on a feature with a corresponding spec directory"
        if [ "$HAS_GIT" != "true" ]; then
            info "Use: export SPECIFY_FEATURE=your-feature-name or create a new feature first"
        fi
        exit 1
    fi
    if [ ! -f "$TEMPLATE_FILE" ]; then
        err_msg "Template file not found at $TEMPLATE_FILE"
        info "Run specify init to scaffold .specify/templates, or add agent-file-template.md there."
        exit 1
    fi
}

extract_plan_field() {
    local field_pattern="$1"
    local plan_file="$2"
    [ -f "$plan_file" ] || return 0

    # Use awk for robust extraction — handles multi-line values and avoids
    # sed escaping issues that caused "No match" errors with complex plan fields.
    # Reads across continuation lines (lines starting with - or whitespace after
    # the initial **Field**: value line) and joins them with " | ".
    local value
    value=$(awk -v field="$field_pattern" '
        BEGIN { found=0; val="" }
        # Match the bold field line: **Field**: value
        $0 ~ "^\\*\\*" field "\\*\\*:[[:space:]]*" {
            # Strip the **Field**: prefix
            sub("^\\*\\*" field "\\*\\*:[[:space:]]*", "")
            val = $0
            found = 1
            next
        }
        # If we already matched, collect continuation lines (start with - or whitespace)
        found == 1 && /^[[:space:]]*-[[:space:]]/ {
            line = $0
            sub(/^[[:space:]]*-[[:space:]]*/, "", line)
            val = val " | " line
            next
        }
        # Stop collecting on any non-continuation line
        found == 1 { exit }
        END {
            # Trim leading/trailing whitespace
            gsub(/^[[:space:]]+|[[:space:]]+$/, "", val)
            if (val != "" && val != "NEEDS CLARIFICATION" && val != "N/A") print val
        }
    ' "$plan_file" 2>/dev/null) || true

    echo "$value"
}

parse_plan_data() {
    local plan_file="$1"
    if [ ! -f "$plan_file" ]; then
        err_msg "Plan file not found: $plan_file"
        return 1
    fi
    info "Parsing plan data from $plan_file"
    NEW_LANG=$(extract_plan_field "Language/Version" "$plan_file")
    NEW_FRAMEWORK=$(extract_plan_field "Primary Dependencies" "$plan_file")
    NEW_DB=$(extract_plan_field "Storage" "$plan_file")
    NEW_PROJECT_TYPE=$(extract_plan_field "Project Type" "$plan_file")

    [ -n "$NEW_LANG" ] && info "Found language: $NEW_LANG" || warn_msg "No language information found in plan"
    [ -n "$NEW_FRAMEWORK" ] && info "Found framework: $NEW_FRAMEWORK"
    [ -n "$NEW_DB" ] && [ "$NEW_DB" != "N/A" ] && info "Found database: $NEW_DB"
    [ -n "$NEW_PROJECT_TYPE" ] && info "Found project type: $NEW_PROJECT_TYPE"
    return 0
}

format_technology_stack() {
    local lang="${1:-}"
    local framework="${2:-}"
    local parts=()
    [ -n "$lang" ] && [ "$lang" != "NEEDS CLARIFICATION" ] && parts+=("$lang")
    [ -n "$framework" ] && [ "$framework" != "NEEDS CLARIFICATION" ] && [ "$framework" != "N/A" ] && parts+=("$framework")
    if [ ${#parts[@]} -eq 0 ]; then
        echo ""
    else
        local IFS=" + "
        echo "${parts[*]}"
    fi
}

get_project_structure() {
    local project_type="${1:-}"
    if echo "$project_type" | grep -qi "web"; then
        printf "backend/\nfrontend/\ntests/"
    else
        printf "src/\ntests/"
    fi
}

get_commands_for_language() {
    local lang="${1:-}"
    case "$lang" in
        *Python*)       echo "cd src; pytest; ruff check ." ;;
        *Rust*)         echo "cargo test; cargo clippy" ;;
        *JavaScript*|*TypeScript*) echo "npm test; npm run lint" ;;
        *)              echo "# Add commands for $lang" ;;
    esac
}

get_language_conventions() {
    local lang="${1:-}"
    if [ -n "$lang" ]; then
        echo "${lang}: Follow standard conventions"
    else
        echo "General: Follow standard conventions"
    fi
}

new_agent_file() {
    local target_file="$1"
    local project_name="$2"
    local date="$3"

    if [ ! -f "$TEMPLATE_FILE" ]; then
        err_msg "Template not found at $TEMPLATE_FILE"
        return 1
    fi

    local content
    content=$(<"$TEMPLATE_FILE")

    local project_structure commands language_conventions
    project_structure=$(get_project_structure "$NEW_PROJECT_TYPE")
    commands=$(get_commands_for_language "$NEW_LANG")
    language_conventions=$(get_language_conventions "$NEW_LANG")

    content="${content//\[PROJECT NAME\]/$project_name}"
    content="${content//\[DATE\]/$date}"

    # Build technology stack string
    local tech_stack=""
    if [ -n "$NEW_LANG" ] && [ -n "$NEW_FRAMEWORK" ]; then
        tech_stack="- $NEW_LANG + $NEW_FRAMEWORK ($CURRENT_BRANCH)"
    elif [ -n "$NEW_LANG" ]; then
        tech_stack="- $NEW_LANG ($CURRENT_BRANCH)"
    elif [ -n "$NEW_FRAMEWORK" ]; then
        tech_stack="- $NEW_FRAMEWORK ($CURRENT_BRANCH)"
    fi

    content="${content//\[EXTRACTED FROM ALL PLAN.MD FILES\]/$tech_stack}"
    content="${content//\[ACTUAL STRUCTURE FROM PLANS\]/$project_structure}"
    content="${content//\[ONLY COMMANDS FOR ACTIVE TECHNOLOGIES\]/$commands}"
    content="${content//\[LANGUAGE-SPECIFIC, ONLY FOR LANGUAGES IN USE\]/$language_conventions}"

    # Build recent changes string
    local recent_changes=""
    if [ -n "$NEW_LANG" ] && [ -n "$NEW_FRAMEWORK" ]; then
        recent_changes="- ${CURRENT_BRANCH}: Added ${NEW_LANG} + ${NEW_FRAMEWORK}"
    elif [ -n "$NEW_LANG" ]; then
        recent_changes="- ${CURRENT_BRANCH}: Added ${NEW_LANG}"
    elif [ -n "$NEW_FRAMEWORK" ]; then
        recent_changes="- ${CURRENT_BRANCH}: Added ${NEW_FRAMEWORK}"
    fi

    content="${content//\[LAST 3 FEATURES AND WHAT THEY ADDED\]/$recent_changes}"

    # Prepend Cursor frontmatter for .mdc files
    if [[ "$target_file" == *.mdc ]]; then
        local frontmatter
        frontmatter="---
description: Project Development Guidelines
globs: [\"**/*\"]
alwaysApply: true
---
"
        content="$frontmatter$content"
    fi

    local parent_dir
    parent_dir=$(dirname "$target_file")
    mkdir -p "$parent_dir"
    printf '%s' "$content" > "$target_file"
    return 0
}

update_existing_agent_file() {
    local target_file="$1"
    local date="$2"

    if [ ! -f "$target_file" ]; then
        new_agent_file "$target_file" "$(basename "$REPO_ROOT")" "$date"
        return $?
    fi

    local tech_stack
    tech_stack=$(format_technology_stack "$NEW_LANG" "$NEW_FRAMEWORK")
    local new_tech_entries=()
    if [ -n "$tech_stack" ]; then
        if ! grep -qF "$tech_stack" "$target_file" 2>/dev/null; then
            new_tech_entries+=("- $tech_stack ($CURRENT_BRANCH)")
        fi
    fi
    if [ -n "$NEW_DB" ] && [ "$NEW_DB" != "N/A" ] && [ "$NEW_DB" != "NEEDS CLARIFICATION" ]; then
        if ! grep -qF "$NEW_DB" "$target_file" 2>/dev/null; then
            new_tech_entries+=("- $NEW_DB ($CURRENT_BRANCH)")
        fi
    fi

    local new_change_entry=""
    if [ -n "$tech_stack" ]; then
        new_change_entry="- ${CURRENT_BRANCH}: Added ${tech_stack}"
    elif [ -n "$NEW_DB" ] && [ "$NEW_DB" != "N/A" ] && [ "$NEW_DB" != "NEEDS CLARIFICATION" ]; then
        new_change_entry="- ${CURRENT_BRANCH}: Added ${NEW_DB}"
    fi

    local tmpfile
    tmpfile=$(mktemp)
    local in_tech=false in_changes=false tech_added=false change_added=false existing_changes=0

    while IFS= read -r line || [ -n "$line" ]; do
        if [ "$line" = "## Active Technologies" ]; then
            echo "$line" >> "$tmpfile"
            in_tech=true
            continue
        fi
        if [ "$in_tech" = true ] && [[ "$line" =~ ^##[[:space:]] ]]; then
            if [ "$tech_added" = false ] && [ ${#new_tech_entries[@]} -gt 0 ]; then
                for entry in "${new_tech_entries[@]}"; do echo "$entry" >> "$tmpfile"; done
                tech_added=true
            fi
            echo "$line" >> "$tmpfile"
            in_tech=false
            continue
        fi
        if [ "$in_tech" = true ] && [ -z "$(echo "$line" | tr -d '[:space:]')" ]; then
            if [ "$tech_added" = false ] && [ ${#new_tech_entries[@]} -gt 0 ]; then
                for entry in "${new_tech_entries[@]}"; do echo "$entry" >> "$tmpfile"; done
                tech_added=true
            fi
            echo "$line" >> "$tmpfile"
            continue
        fi
        if [ "$line" = "## Recent Changes" ]; then
            echo "$line" >> "$tmpfile"
            if [ -n "$new_change_entry" ]; then
                echo "$new_change_entry" >> "$tmpfile"
                change_added=true
            fi
            in_changes=true
            continue
        fi
        if [ "$in_changes" = true ] && [[ "$line" =~ ^##[[:space:]] ]]; then
            echo "$line" >> "$tmpfile"
            in_changes=false
            continue
        fi
        if [ "$in_changes" = true ] && [[ "$line" =~ ^-[[:space:]] ]]; then
            if [ "$existing_changes" -lt 2 ]; then
                echo "$line" >> "$tmpfile"
                existing_changes=$((existing_changes + 1))
            fi
            continue
        fi
        if echo "$line" | grep -q '\*\*Last updated\*\*:.*[0-9]\{4\}-[0-9]\{2\}-[0-9]\{2\}'; then
            echo "$line" | sed "s/[0-9]\{4\}-[0-9]\{2\}-[0-9]\{2\}/$date/" >> "$tmpfile"
            continue
        fi
        echo "$line" >> "$tmpfile"
    done < "$target_file"

    # Ensure Cursor .mdc files have YAML frontmatter
    if [[ "$target_file" == *.mdc ]]; then
        local first_line
        first_line=$(head -1 "$tmpfile")
        if [ "$first_line" != "---" ]; then
            local frontmatter_file
            frontmatter_file=$(mktemp)
            printf '%s\n' "---" "description: Project Development Guidelines" 'globs: ["**/*"]' "alwaysApply: true" "---" "" > "$frontmatter_file"
            cat "$tmpfile" >> "$frontmatter_file"
            mv "$frontmatter_file" "$tmpfile"
        fi
    fi

    mv "$tmpfile" "$target_file"
    return 0
}

update_agent_file() {
    local target_file="$1"
    local agent_name="$2"

    if [ -z "$target_file" ] || [ -z "$agent_name" ]; then
        err_msg "update_agent_file requires target_file and agent_name"
        return 1
    fi

    info "Updating $agent_name context file: $target_file"
    local project_name date
    project_name=$(basename "$REPO_ROOT")
    date=$(date +%Y-%m-%d)

    local dir
    dir=$(dirname "$target_file")
    mkdir -p "$dir"

    if [ ! -f "$target_file" ]; then
        if new_agent_file "$target_file" "$project_name" "$date"; then
            success "Created new $agent_name context file"
        else
            err_msg "Failed to create new agent file"
            return 1
        fi
    else
        if update_existing_agent_file "$target_file" "$date"; then
            success "Updated existing $agent_name context file"
        else
            err_msg "Failed to update agent file"
            return 1
        fi
    fi
    return 0
}

update_specific_agent() {
    local type="$1"
    case "$type" in
        claude)       update_agent_file "$CLAUDE_FILE"    "Claude Code" ;;
        gemini)       update_agent_file "$GEMINI_FILE"    "Gemini CLI" ;;
        copilot)      update_agent_file "$COPILOT_FILE"   "GitHub Copilot" ;;
        cursor-agent) update_agent_file "$CURSOR_FILE"    "Cursor IDE" ;;
        qwen)         update_agent_file "$QWEN_FILE"      "Qwen Code" ;;
        opencode)     update_agent_file "$AGENTS_FILE"    "opencode" ;;
        codex)        update_agent_file "$AGENTS_FILE"    "Codex CLI" ;;
        windsurf)     update_agent_file "$WINDSURF_FILE"  "Windsurf" ;;
        kilocode)     update_agent_file "$KILOCODE_FILE"  "Kilo Code" ;;
        auggie)       update_agent_file "$AUGGIE_FILE"    "Auggie CLI" ;;
        roo)          update_agent_file "$ROO_FILE"       "Roo Code" ;;
        codebuddy)    update_agent_file "$CODEBUDDY_FILE" "CodeBuddy CLI" ;;
        qodercli)     update_agent_file "$QODER_FILE"     "Qoder CLI" ;;
        amp)          update_agent_file "$AMP_FILE"       "Amp" ;;
        shai)         update_agent_file "$SHAI_FILE"      "SHAI" ;;
        q)            update_agent_file "$Q_FILE"         "Amazon Q Developer CLI" ;;
        agy)          update_agent_file "$AGY_FILE"       "Antigravity" ;;
        bob)          update_agent_file "$BOB_FILE"       "IBM Bob" ;;
        generic)      info "Generic agent: no predefined context file. Use the agent-specific update script for your agent." ;;
        *)
            err_msg "Unknown agent type '$type'"
            err_msg "Expected: claude|gemini|copilot|cursor-agent|qwen|opencode|codex|windsurf|kilocode|auggie|roo|codebuddy|amp|shai|q|agy|bob|qodercli|generic"
            return 1
            ;;
    esac
}

update_all_existing_agents() {
    local found=false ok=true

    [ -f "$CLAUDE_FILE" ]    && { update_agent_file "$CLAUDE_FILE"    "Claude Code"    || ok=false; found=true; }
    [ -f "$GEMINI_FILE" ]    && { update_agent_file "$GEMINI_FILE"    "Gemini CLI"     || ok=false; found=true; }
    [ -f "$COPILOT_FILE" ]   && { update_agent_file "$COPILOT_FILE"   "GitHub Copilot" || ok=false; found=true; }
    [ -f "$CURSOR_FILE" ]    && { update_agent_file "$CURSOR_FILE"    "Cursor IDE"     || ok=false; found=true; }
    [ -f "$QWEN_FILE" ]      && { update_agent_file "$QWEN_FILE"      "Qwen Code"     || ok=false; found=true; }
    [ -f "$AGENTS_FILE" ]    && { update_agent_file "$AGENTS_FILE"    "Codex/opencode" || ok=false; found=true; }
    [ -f "$WINDSURF_FILE" ]  && { update_agent_file "$WINDSURF_FILE"  "Windsurf"       || ok=false; found=true; }
    [ -f "$KILOCODE_FILE" ]  && { update_agent_file "$KILOCODE_FILE"  "Kilo Code"      || ok=false; found=true; }
    [ -f "$AUGGIE_FILE" ]    && { update_agent_file "$AUGGIE_FILE"    "Auggie CLI"     || ok=false; found=true; }
    [ -f "$ROO_FILE" ]       && { update_agent_file "$ROO_FILE"       "Roo Code"       || ok=false; found=true; }
    [ -f "$CODEBUDDY_FILE" ] && { update_agent_file "$CODEBUDDY_FILE" "CodeBuddy CLI"  || ok=false; found=true; }
    [ -f "$QODER_FILE" ]     && { update_agent_file "$QODER_FILE"     "Qoder CLI"      || ok=false; found=true; }
    [ -f "$SHAI_FILE" ]      && { update_agent_file "$SHAI_FILE"      "SHAI"           || ok=false; found=true; }
    [ -f "$Q_FILE" ]         && { update_agent_file "$Q_FILE"         "Amazon Q Developer CLI" || ok=false; found=true; }
    [ -f "$AGY_FILE" ]       && { update_agent_file "$AGY_FILE"       "Antigravity"    || ok=false; found=true; }
    [ -f "$BOB_FILE" ]       && { update_agent_file "$BOB_FILE"       "IBM Bob"        || ok=false; found=true; }

    if [ "$found" = false ]; then
        info "No existing agent files found, creating default Claude file..."
        update_agent_file "$CLAUDE_FILE" "Claude Code" || ok=false
    fi
    [ "$ok" = true ]
}

print_summary() {
    echo ""
    info "Summary of changes:"
    [ -n "$NEW_LANG" ] && echo "  - Added language: $NEW_LANG"
    [ -n "$NEW_FRAMEWORK" ] && echo "  - Added framework: $NEW_FRAMEWORK"
    [ -n "$NEW_DB" ] && [ "$NEW_DB" != "N/A" ] && echo "  - Added database: $NEW_DB"
    echo ""
    info "Usage: ./update-agent-context.sh [claude|gemini|copilot|cursor-agent|qwen|opencode|codex|windsurf|kilocode|auggie|roo|codebuddy|amp|shai|q|agy|bob|qodercli|generic]"
}

main() {
    validate_environment
    info "=== Updating agent context files for feature $CURRENT_BRANCH ==="
    if ! parse_plan_data "$NEW_PLAN"; then
        err_msg "Failed to parse plan data"
        exit 1
    fi

    local agent_success=true
    if [ -n "$AGENT_TYPE" ]; then
        info "Updating specific agent: $AGENT_TYPE"
        update_specific_agent "$AGENT_TYPE" || agent_success=false
    else
        info "No agent specified, updating all existing agent files..."
        update_all_existing_agents || agent_success=false
    fi

    print_summary
    if [ "$agent_success" = true ]; then
        success "Agent context update completed successfully"
        exit 0
    else
        err_msg "Agent context update completed with errors"
        exit 1
    fi
}

main
