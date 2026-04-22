#!/usr/bin/env bash
# =============================================================================
# package-stack-b.sh — Publish and package Stack B for App Service deployment
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACT_DIR="${REPO_ROOT}/artifacts/stack-b"
ARTIFACT_DIR_DOTNET="$ARTIFACT_DIR"
PROJECT_PATH="dotnet/src/Web.Server/TalentMatch.Web.Server.csproj"

source "$SCRIPT_DIR/lib/common.sh"

print_banner "Packaging Stack B (.NET Blazor WASM)"

cd "$REPO_ROOT"
rm -rf "$ARTIFACT_DIR"
mkdir -p "$ARTIFACT_DIR"

if command -v cygpath >/dev/null 2>&1; then
  ARTIFACT_DIR_DOTNET="$(cygpath -w "$ARTIFACT_DIR")"
fi

# ---------------------------------------------------------------------------
# Step 1: Publish Stack B
# ---------------------------------------------------------------------------
log_info "Publishing Stack B from $PROJECT_PATH..."
dotnet publish "$PROJECT_PATH" \
  --configuration Release \
  --output "$ARTIFACT_DIR_DOTNET" \
  --self-contained false

# ---------------------------------------------------------------------------
# Step 1b: Stamp deploy metadata for runtime diagnostics
# ---------------------------------------------------------------------------
BUILD_CREATED_AT_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
BUILD_VERSION="$(date -u +"%Y%m%d%H%M%S")"
if GIT_SHA="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null)"; then
  BUILD_VERSION="${BUILD_VERSION}-${GIT_SHA}"
fi

# Place in wwwroot so it's served as a static asset
mkdir -p "$ARTIFACT_DIR/wwwroot"
cat > "$ARTIFACT_DIR/wwwroot/build-info.json" <<EOF
{
  "version": "$BUILD_VERSION",
  "createdAtUtc": "$BUILD_CREATED_AT_UTC",
  "source": "package-stack-b.sh"
}
EOF

log_info "Stamped build metadata to wwwroot: version=$BUILD_VERSION createdAtUtc=$BUILD_CREATED_AT_UTC"

# ---------------------------------------------------------------------------
# Step 2: Create zip artifact
# ---------------------------------------------------------------------------
ARTIFACT_PATH="${REPO_ROOT}/artifacts/stack-b.zip"
rm -f "$ARTIFACT_PATH"

log_info "Creating zip artifact..."
if command -v zip >/dev/null 2>&1; then
  (
    cd "$ARTIFACT_DIR"
    zip -q -r "$ARTIFACT_PATH" .
  )
elif command -v powershell.exe >/dev/null 2>&1; then
  ARTIFACT_PATH_WIN="$ARTIFACT_PATH"
  ARTIFACT_DIR_WIN="$ARTIFACT_DIR"

  if command -v cygpath >/dev/null 2>&1; then
    ARTIFACT_PATH_WIN="$(cygpath -w "$ARTIFACT_PATH")"
    ARTIFACT_DIR_WIN="$(cygpath -w "$ARTIFACT_DIR")"
  fi

  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "\
    \$ErrorActionPreference = 'Stop'; \
    Add-Type -AssemblyName System.IO.Compression; \
    Add-Type -AssemblyName System.IO.Compression.FileSystem; \
    \$SourceDir = '$ARTIFACT_DIR_WIN'; \
    \$DestinationZip = '$ARTIFACT_PATH_WIN'; \
    if (Test-Path \$DestinationZip) { Remove-Item -Force \$DestinationZip }; \
    \$zip = [System.IO.Compression.ZipFile]::Open(\$DestinationZip, [System.IO.Compression.ZipArchiveMode]::Create); \
    try { \
      Get-ChildItem -Path \$SourceDir -Recurse -File | ForEach-Object { \
        \$entryName = \$_.FullName.Substring(\$SourceDir.Length).TrimStart('\\', '/').Replace('\\', '/'); \
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(\$zip, \$_.FullName, \$entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null \
      } \
    } finally { \
      \$zip.Dispose() \
    }"
else
  log_error "Neither 'zip' nor 'powershell.exe' is available; cannot create stack-b.zip"
  exit 1
fi

if [[ ! -f "$ARTIFACT_PATH" ]] || [[ ! -s "$ARTIFACT_PATH" ]]; then
  log_error "Failed to create zip artifact at: $ARTIFACT_PATH"
  exit 1
fi

log_success "Stack B artifact created: $ARTIFACT_PATH"
log_info "Artifact size: $(du -h "$ARTIFACT_PATH" | cut -f1)"
