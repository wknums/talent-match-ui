#!/usr/bin/env bash
# =============================================================================
# package-stack-a.sh — Build and package Stack A for App Service deployment
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACT_DIR="${REPO_ROOT}/artifacts/stack-a"

source "$SCRIPT_DIR/lib/common.sh"

print_banner "Packaging Stack A (Node.js/Express)"

cd "$REPO_ROOT"

# ---------------------------------------------------------------------------
# Step 1: Install dependencies
# ---------------------------------------------------------------------------
log_info "Installing Node.js dependencies..."
npm ci

# ---------------------------------------------------------------------------
# Step 2: Build client
# ---------------------------------------------------------------------------
log_info "Building client application..."
npm run build

# ---------------------------------------------------------------------------
# Step 3: Build server
# ---------------------------------------------------------------------------
log_info "Building server application..."
npm run build:server

# ---------------------------------------------------------------------------
# Step 4: Assemble deployment artifact
# ---------------------------------------------------------------------------
log_info "Assembling deployment artifact in $ARTIFACT_DIR"
rm -rf "$ARTIFACT_DIR"
mkdir -p "$ARTIFACT_DIR"

# Copy server build output
cp -r dist-server/* "$ARTIFACT_DIR/"

# tsc emits only JS, but db.ts reads these at runtime from its own directory
mkdir -p "$ARTIFACT_DIR/server/storage"
cp server/storage/*.sql "$ARTIFACT_DIR/server/storage/"

# Copy client build output
mkdir -p "$ARTIFACT_DIR/dist"
cp -r dist/* "$ARTIFACT_DIR/dist/"

# Stamp deploy metadata for runtime diagnostics
BUILD_CREATED_AT_UTC="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
BUILD_VERSION="$(date -u +"%Y%m%d%H%M%S")"
if GIT_SHA="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null)"; then
	BUILD_VERSION="${BUILD_VERSION}-${GIT_SHA}"
fi

cat > "$ARTIFACT_DIR/dist/build-info.json" <<EOF
{
	"version": "$BUILD_VERSION",
	"createdAtUtc": "$BUILD_CREATED_AT_UTC",
	"source": "package-stack-a.sh"
}
EOF

log_info "Stamped build metadata: version=$BUILD_VERSION createdAtUtc=$BUILD_CREATED_AT_UTC"

# Copy package.json only (no lockfile: it can pin platform-specific native binaries
# such as @rollup/rollup-win32-x64-msvc that break Oryx npm install on Linux).
cp package.json "$ARTIFACT_DIR/"

# Reduce artifact's package.json to runtime essentials:
# - keep only the `start` script (Oryx auto-runs `npm run build` if present,
#   which would fail because devDeps like tsc/vite are not installed)
# - drop devDependencies and optionalDependencies (devDeps may include
#   platform-locked native binaries that break cross-platform install)
cd "$ARTIFACT_DIR"
node -e "const fs=require('fs'); const p='package.json'; const j=JSON.parse(fs.readFileSync(p,'utf8')); j.scripts={start:'node server/index.js'}; delete j.devDependencies; delete j.optionalDependencies; fs.writeFileSync(p, JSON.stringify(j, null, 2) + '\n');"

# Intentionally exclude node_modules from the artifact.
# App Service installs dependencies during deployment when
# SCM_DO_BUILD_DURING_DEPLOYMENT=true and ENABLE_ORYX_BUILD=true.
log_info "Skipping node_modules in package (on-host dependency restore enabled)"

# Create startup helper for manual fallback scenarios
cat > "$ARTIFACT_DIR/startup.sh" << 'EOF'
#!/usr/bin/env bash
node server/index.js
EOF
chmod +x "$ARTIFACT_DIR/startup.sh"

# ---------------------------------------------------------------------------
# Step 5: Create zip artifact
# ---------------------------------------------------------------------------
cd "$ARTIFACT_DIR"
ARTIFACT_PATH="${REPO_ROOT}/artifacts/stack-a.zip"
rm -f "$ARTIFACT_PATH"

FILE_COUNT="$(find . -type f | wc -l | tr -d ' ')"
log_info "Creating zip artifact from $FILE_COUNT files (node_modules excluded)..."
if command -v zip >/dev/null 2>&1; then
	zip -q -r "$ARTIFACT_PATH" .
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
				[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(\$zip, \$_.FullName, \$entryName, [System.IO.Compression.CompressionLevel]::Fastest) | Out-Null \
			} \
		} finally { \
			\$zip.Dispose() \
		}"
else
	log_error "Neither 'zip' nor 'powershell.exe' is available; cannot create stack-a.zip"
	exit 1
fi

if [[ ! -f "$ARTIFACT_PATH" ]] || [[ ! -s "$ARTIFACT_PATH" ]]; then
	log_error "Failed to create zip artifact at: $ARTIFACT_PATH"
	exit 1
fi

log_success "Stack A artifact created: $ARTIFACT_PATH"
log_info "Artifact size: $(du -h "$ARTIFACT_PATH" | cut -f1)"
