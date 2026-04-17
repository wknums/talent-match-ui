#!/usr/bin/env bash
# =============================================================================
# package-stack-b.sh — Publish and package Stack B for App Service deployment
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACT_DIR="${REPO_ROOT}/artifacts/stack-b"
PROJECT_PATH="dotnet/src/Web.Server/TalentMatch.Web.Server.csproj"

source "$SCRIPT_DIR/lib/common.sh"

print_banner "Packaging Stack B (.NET Blazor WASM)"

cd "$REPO_ROOT"

# ---------------------------------------------------------------------------
# Step 1: Publish Stack B
# ---------------------------------------------------------------------------
log_info "Publishing Stack B from $PROJECT_PATH..."
dotnet publish "$PROJECT_PATH" \
  --configuration Release \
  --output "$ARTIFACT_DIR" \
  --self-contained false

# ---------------------------------------------------------------------------
# Step 2: Create zip artifact
# ---------------------------------------------------------------------------
cd "$ARTIFACT_DIR"
ARTIFACT_PATH="${REPO_ROOT}/artifacts/stack-b.zip"
rm -f "$ARTIFACT_PATH"
zip -r "$ARTIFACT_PATH" . -x "*.git*"

log_success "Stack B artifact created: $ARTIFACT_PATH"
log_info "Artifact size: $(du -h "$ARTIFACT_PATH" | cut -f1)"
