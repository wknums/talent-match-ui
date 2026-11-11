#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url));
const TERRAFORM_DIR = path.resolve(TEST_DIR, "..");
const REPO_ROOT = path.resolve(TERRAFORM_DIR, "..", "..");
const GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000";
const FILES_READ_ALL_SCOPE = "df85f4d6-205c-4ac5-a5ea-6bf408dba283";
const SITES_READ_ALL_SCOPE = "205e70e5-aba6-4c52-a976-6d2d46c48043";

function relative(file) {
  return path.relative(REPO_ROOT, file).split(path.sep).join("/");
}

function walkTerraformFiles(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name, "en"))) {
    if (entry.name === ".terraform") continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkTerraformFiles(fullPath));
    else if (entry.isFile() && entry.name.endsWith(".tf")) files.push(fullPath);
  }
  return files;
}

function findClosingBrace(text, openingBrace) {
  let depth = 0;
  let quote = null;
  let escaped = false;
  let lineComment = false;
  let blockComment = false;

  for (let index = openingBrace; index < text.length; index++) {
    const character = text[index];
    const next = text[index + 1];

    if (lineComment) {
      if (character === "\n") lineComment = false;
      continue;
    }
    if (blockComment) {
      if (character === "*" && next === "/") {
        blockComment = false;
        index++;
      }
      continue;
    }
    if (quote) {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === quote) quote = null;
      continue;
    }
    if (character === "#") {
      lineComment = true;
      continue;
    }
    if (character === "/" && next === "/") {
      lineComment = true;
      index++;
      continue;
    }
    if (character === "/" && next === "*") {
      blockComment = true;
      index++;
      continue;
    }
    if (character === '"' || character === "'") {
      quote = character;
      continue;
    }
    if (character === "{") depth++;
    if (character === "}" && --depth === 0) return index;
  }
  return -1;
}

function extractResourceBlocks(file, text) {
  const blocks = [];
  const header = /\bresource\s+"([^"]+)"\s+"([^"]+)"\s*\{/g;
  for (let match = header.exec(text); match; match = header.exec(text)) {
    const openingBrace = header.lastIndex - 1;
    const closingBrace = findClosingBrace(text, openingBrace);
    if (closingBrace < 0) continue;
    blocks.push({
      type: match[1],
      name: match[2],
      file,
      body: text.slice(openingBrace + 1, closingBrace),
    });
    header.lastIndex = closingBrace + 1;
  }
  return blocks;
}

function assertSourceConnector() {
  const files = walkTerraformFiles(TERRAFORM_DIR);
  const sources = files.map((file) => ({ file, text: fs.readFileSync(file, "utf8") }));
  const resources = sources.flatMap(({ file, text }) => extractResourceBlocks(file, text));
  const violations = [];

  const applications = resources.filter((resource) => resource.type === "azuread_application");
  const connectorCandidates = applications.filter((resource) =>
    /\bsign_in_audience\s*=\s*"AzureADMultipleOrgs"/.test(resource.body)
    && /\bweb\s*\{/.test(resource.body));

  if (connectorCandidates.length !== 1) {
    violations.push(`expected exactly one AzureADMultipleOrgs connector with a Web redirect block; found ${connectorCandidates.length}`);
  }

  const connector = connectorCandidates[0];
  if (connector) {
    const location = `${relative(connector.file)}: azuread_application.${connector.name}`;
    if (!/\bredirect_uris\s*=\s*var\.[A-Za-z0-9_]*(?:redirect|callback)[A-Za-z0-9_]*/i.test(connector.body)) {
      violations.push(`${location} must take its exact Web callback list from a redirect/callback variable`);
    }
    if (/\bsingle_page_application\s*\{|\bpublic_client\s*\{/.test(connector.body)) {
      violations.push(`${location} must not configure SPA or public-client redirects`);
    }
    if (!connector.body.includes(GRAPH_APP_ID)) {
      violations.push(`${location} must declare Microsoft Graph required_resource_access`);
    }
    for (const [scope, id] of [["Files.Read.All", FILES_READ_ALL_SCOPE], ["Sites.Read.All", SITES_READ_ALL_SCOPE]]) {
      if (!connector.body.includes(id)) violations.push(`${location} is missing delegated ${scope} (${id})`);
    }
    const graphAccess = connector.body.match(/required_resource_access\s*\{[\s\S]*?\n\s*\}/g) ?? [];
    if (!graphAccess.some((block) => block.includes(GRAPH_APP_ID) && /\btype\s*=\s*"Scope"/.test(block))) {
      violations.push(`${location} must request Microsoft Graph permissions as delegated Scope entries`);
    }
    if (graphAccess.some((block) => block.includes(GRAPH_APP_ID) && /\btype\s*=\s*"Role"/.test(block))) {
      violations.push(`${location} requests a forbidden Microsoft Graph application Role`);
    }

    const connectorReference = new RegExp(`azuread_application\\.${connector.name}(?:\\[\\d+\\])?\\.id`);
    const federation = resources.filter((resource) =>
      resource.type === "azuread_application_federated_identity_credential"
      && connectorReference.test(resource.body));
    if (federation.length === 0) {
      violations.push(`${location} has no managed-identity federated credentials`);
    } else {
      const federationText = federation.map((resource) => resource.body).join("\n");
      if (!/api:\/\/AzureADTokenExchange/.test(federationText)) {
        violations.push(`${location} federation must use the api://AzureADTokenExchange audience`);
      }
      if (!/\b(for_each|subject)\b[\s\S]*federated_identity_principal_ids/.test(federationText)) {
        violations.push(`${location} federation must be driven only by the reviewed hosting managed identities`);
      }
    }

    for (const credential of resources.filter((resource) =>
      ["azuread_application_password", "azuread_application_certificate"].includes(resource.type)
      && connectorReference.test(resource.body))) {
      violations.push(`${relative(credential.file)}: ${credential.type}.${credential.name} adds a forbidden connector credential`);
    }
  }

  const stackBMain = path.join(TERRAFORM_DIR, "modules", "stack-b", "main.tf");
  const stackBText = fs.existsSync(stackBMain) ? fs.readFileSync(stackBMain, "utf8") : "";
  if (!/MICROSOFT365_SOURCE_CLIENT_ID/.test(stackBText)) {
    violations.push("infra/terraform/modules/stack-b/main.tf must wire MICROSOFT365_SOURCE_CLIENT_ID from the connector output");
  }

  const stackBLiveMain = path.join(TERRAFORM_DIR, "live", "stack-b", "main.tf");
  const stackBLiveText = fs.existsSync(stackBLiveMain) ? fs.readFileSync(stackBLiveMain, "utf8") : "";
  if (!/MICROSOFT365_SOURCE_MAX_CONCURRENCY\s*=\s*var\.microsoft365_source_max_concurrency/.test(stackBLiveText)) {
    violations.push("infra/terraform/live/stack-b/main.tf must wire MICROSOFT365_SOURCE_MAX_CONCURRENCY");
  }

  const commonScript = path.join(REPO_ROOT, "infra", "scripts", "lib", "common.sh");
  const commonText = fs.existsSync(commonScript) ? fs.readFileSync(commonScript, "utf8") : "";
  if (!/TF_VAR_microsoft365_source_max_concurrency=.*MICROSOFT365_SOURCE_MAX_CONCURRENCY/.test(commonText)) {
    violations.push("infra/scripts/lib/common.sh must export MICROSOFT365_SOURCE_MAX_CONCURRENCY to Terraform");
  }

  const entraOutputs = path.join(TERRAFORM_DIR, "modules", "foundation", "entra", "outputs.tf");
  const outputText = fs.existsSync(entraOutputs) ? fs.readFileSync(entraOutputs, "utf8") : "";
  if (!/output\s+"[^"]*(?:source|connector)[^"]*client_id"/i.test(outputText)) {
    violations.push("infra/terraform/modules/foundation/entra/outputs.tf must expose the source connector client ID");
  }

  if (violations.length > 0) {
    console.error("FAIL: delegated source connector Terraform contract is incomplete:");
    for (const violation of violations) console.error(`  - ${violation}`);
    return 1;
  }

  console.log(`PASS: one delegated-only source connector is defined across ${files.length} Terraform files.`);
  return 0;
}

process.exitCode = assertSourceConnector();
