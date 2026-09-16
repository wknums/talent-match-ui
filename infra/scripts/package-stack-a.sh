#!/usr/bin/env bash
# =============================================================================
# package-stack-a.sh — Build and package Stack A for App Service deployment
# =============================================================================
# Usage: ./infra/scripts/package-stack-a.sh [env-file]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
ARTIFACT_DIR="${REPO_ROOT}/artifacts/stack-a"

source "$SCRIPT_DIR/lib/common.sh"

print_banner "Packaging Stack A (Node.js/Express)"

cd "$REPO_ROOT"

ENV_FILE="${1:-}"
if [[ -n "$ENV_FILE" ]]; then
	load_env_file "$ENV_FILE"
fi

export VITE_APP_AUTH_MODE="${VITE_APP_AUTH_MODE:-${APP_AUTH_MODE:-simple}}"
if command -v node >/dev/null 2>&1; then
	NODE_COMMAND="node"
elif command -v node.exe >/dev/null 2>&1; then
	NODE_COMMAND="node.exe"
else
	log_fatal "Node.js is required to package Stack A"
fi

if [[ "$VITE_APP_AUTH_MODE" == "entra" ]]; then
	export VITE_ENTRA_TENANT_ID="${VITE_ENTRA_TENANT_ID:-${AZURE_TENANT_ID:-}}"
	export VITE_ENTRA_STACK_A_CLIENT_ID="${VITE_ENTRA_STACK_A_CLIENT_ID:-${ENTRA_STACK_A_CLIENT_ID:-}}"
	export VITE_ENTRA_API_APP_CLIENT_ID="${VITE_ENTRA_API_APP_CLIENT_ID:-${ENTRA_API_APP_CLIENT_ID:-}}"
	export VITE_ENTRA_API_IDENTIFIER_URI="${VITE_ENTRA_API_IDENTIFIER_URI:-${ENTRA_API_IDENTIFIER_URI:-}}"
	export VITE_ENTRA_API_SCOPE="${VITE_ENTRA_API_SCOPE:-${ENTRA_API_SCOPE:-access_as_user}}"
	validate_required \
		"VITE_ENTRA_TENANT_ID" \
		"VITE_ENTRA_STACK_A_CLIENT_ID" \
		"VITE_ENTRA_API_APP_CLIENT_ID" \
		"VITE_ENTRA_API_IDENTIFIER_URI" \
		"VITE_ENTRA_API_SCOPE"
elif [[ "$VITE_APP_AUTH_MODE" != "simple" ]]; then
	log_fatal "VITE_APP_AUTH_MODE must be simple or entra"
fi

log_info "Building client with authentication mode: $VITE_APP_AUTH_MODE"

# ---------------------------------------------------------------------------
# Step 1: Prepare dependencies
# ---------------------------------------------------------------------------
INSTALL_MODE="${STACK_A_INSTALL_MODE:-auto}"
case "$INSTALL_MODE" in
	auto)
		if [[ -f node_modules/.package-lock.json \
			&& node_modules/.package-lock.json -nt package-lock.json \
			&& node_modules/.package-lock.json -nt package.json \
			&& -x node_modules/.bin/vite \
			&& -x node_modules/.bin/tsc ]]; then
			log_info "Reusing node_modules validated by npm's current hidden lockfile (set STACK_A_INSTALL_MODE=clean for npm ci)"
		else
			log_info "node_modules is missing or stale; installing dependencies with npm ci..."
			npm ci
		fi
		;;
	clean)
		log_info "Installing dependencies with npm ci (clean mode)..."
		npm ci
		;;
	*)
		log_fatal "STACK_A_INSTALL_MODE must be auto or clean"
		;;
esac

# ---------------------------------------------------------------------------
# Step 2: Build client
# ---------------------------------------------------------------------------
log_info "Building client application..."
vite_env_file="$REPO_ROOT/.env.production.local"
cat > "$vite_env_file" <<EOF
VITE_APP_AUTH_MODE=$VITE_APP_AUTH_MODE
VITE_ENTRA_TENANT_ID=${VITE_ENTRA_TENANT_ID:-}
VITE_ENTRA_STACK_A_CLIENT_ID=${VITE_ENTRA_STACK_A_CLIENT_ID:-}
VITE_ENTRA_API_APP_CLIENT_ID=${VITE_ENTRA_API_APP_CLIENT_ID:-}
VITE_ENTRA_API_IDENTIFIER_URI=${VITE_ENTRA_API_IDENTIFIER_URI:-}
VITE_ENTRA_API_SCOPE=${VITE_ENTRA_API_SCOPE:-}
EOF
trap 'rm -f "$vite_env_file"' EXIT
npm run build:client
rm -f "$vite_env_file"
trap - EXIT

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
	"source": "package-stack-a.sh",
	"authMode": "$VITE_APP_AUTH_MODE"
}
EOF

log_info "Stamped build metadata: version=$BUILD_VERSION createdAtUtc=$BUILD_CREATED_AT_UTC"

# Copy package.json as the basis for a minimal runtime manifest.
cp package.json "$ARTIFACT_DIR/"

# Reduce artifact's package.json to runtime essentials:
# - keep only dependencies imported by the compiled server
# - exclude better-sqlite3 because Azure deployments use Azure SQL and its
#   native Windows binary cannot be deployed to Linux
cd "$ARTIFACT_DIR"
"$NODE_COMMAND" -e "const fs=require('fs'); const p='package.json'; const j=JSON.parse(fs.readFileSync(p,'utf8')); const runtime=['@azure/identity','@azure/storage-blob','express','jose','mssql','zod']; j.scripts={start:'node server/index.js'}; j.dependencies=Object.fromEntries(runtime.map(name => [name, j.dependencies[name]])); delete j.devDependencies; delete j.optionalDependencies; fs.writeFileSync(p, JSON.stringify(j, null, 2) + '\n');"

log_info "Installing minimal production dependencies into the artifact..."
npm install --omit=dev --ignore-scripts --no-audit --no-fund

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
log_info "Creating self-contained zip artifact from $FILE_COUNT files..."
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
