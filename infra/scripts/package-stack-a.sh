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

# Copy client build output
mkdir -p "$ARTIFACT_DIR/dist"
cp -r dist/* "$ARTIFACT_DIR/dist/"

# Copy package files for production dependencies
cp package.json "$ARTIFACT_DIR/"
cp package-lock.json "$ARTIFACT_DIR/"

# Install production dependencies only
cd "$ARTIFACT_DIR"
npm ci --omit=dev

# Create web.config for App Service (Node.js on Linux uses startup command)
cat > "$ARTIFACT_DIR/startup.sh" << 'EOF'
#!/usr/bin/env bash
node index.js
EOF
chmod +x "$ARTIFACT_DIR/startup.sh"

# ---------------------------------------------------------------------------
# Step 5: Create zip artifact
# ---------------------------------------------------------------------------
cd "$ARTIFACT_DIR"
ARTIFACT_PATH="${REPO_ROOT}/artifacts/stack-a.zip"
rm -f "$ARTIFACT_PATH"
zip -r "$ARTIFACT_PATH" . -x "*.git*"

log_success "Stack A artifact created: $ARTIFACT_PATH"
log_info "Artifact size: $(du -h "$ARTIFACT_PATH" | cut -f1)"
