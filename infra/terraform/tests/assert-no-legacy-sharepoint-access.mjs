#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(TEST_DIR, "..", "..", "..");
const SCAN_ROOTS = [
  path.join(REPO_ROOT, "infra", "terraform", "live"),
  path.join(REPO_ROOT, "infra", "terraform", "modules"),
  path.join(REPO_ROOT, "infra", "scripts"),
];
const TEXT_EXTENSIONS = new Set([".tf", ".sh", ".ps1"]);

const RULES = [
  ["legacy reader registration", /\bsharepoint_reader(?:_[A-Za-z0-9_]+)?\b/i],
  ["Sites.Selected application permission", /\bSites\.Selected\b|883ea226-0bf2-4a8f-9f9d-92c9162a727d/i],
  ["legacy OBO source authorization", /\bon-behalf-of\b|\bOBO\b|bulk_scoring_delegated_scopes|federated_identity_credential"\s+"bulk_scoring/i],
  ["legacy app-only source access", /\bapp-only\b|\bclient_credentials\b|sp_client_token(?:_cert)?\b/i],
  ["legacy source mode", /\bSHAREPOINT_MODE\b|\bSameTenant\b|\bCrossTenant\b/i],
  ["legacy source host/root allow-list", /\b(?:SHAREPOINT|ONEDRIVE)_ALLOWED_(?:HOSTS|ROOTS)\b|\bpermitted_roots?\b/i],
  ["legacy site-grant wiring", /grant-sharepoint-site\.sh|\bsp_grant_(?:app_roles?|site_permission)\b/i],
];

function relative(file) {
  return path.relative(REPO_ROOT, file).split(path.sep).join("/");
}

function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name, "en"))) {
    if (entry.name === ".terraform" || entry.name === "tests") continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walk(fullPath));
    else if (entry.isFile() && TEXT_EXTENSIONS.has(path.extname(entry.name))) files.push(fullPath);
  }
  return files;
}

function firstMatch(text, expression) {
  const match = expression.exec(text);
  if (!match) return null;
  const line = text.slice(0, match.index).split(/\r?\n/).length;
  return { line, excerpt: match[0].replace(/\s+/g, " ").trim() };
}

const files = SCAN_ROOTS.flatMap(walk)
  .sort((left, right) => relative(left).localeCompare(relative(right), "en"));
const violations = [];

for (const file of files) {
  const text = fs.readFileSync(file, "utf8");
  for (const [description, expression] of RULES) {
    expression.lastIndex = 0;
    const match = firstMatch(text, expression);
    if (match) {
      violations.push(`${relative(file)}:${match.line}: ${description} (${match.excerpt})`);
    }
  }
}

const grantScript = path.join(REPO_ROOT, "infra", "scripts", "grant-sharepoint-site.sh");
if (fs.existsSync(grantScript) && !violations.some((violation) => violation.startsWith(relative(grantScript)))) {
  violations.push(`${relative(grantScript)}: legacy site-grant helper still exists`);
}

if (violations.length > 0) {
  console.error("FAIL: legacy SharePoint access paths remain:");
  for (const violation of violations) console.error(`  - ${violation}`);
  process.exitCode = 1;
} else {
  console.log(`PASS: ${files.length} infrastructure files contain no reader, OBO, app-only, Sites.Selected, source allow-list, or site-grant path.`);
}
