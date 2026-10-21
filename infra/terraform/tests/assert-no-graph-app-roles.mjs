#!/usr/bin/env node
// Plan assertion for bulk scoring from SharePoint / OneDrive (task T119):
// the managed identities and the API registration must never gain a Microsoft Graph APPLICATION permission for
// SharePoint or OneDrive content. Same-tenant content is read as the signed-in recruiter (delegated, on-behalf-of).
//
//   terraform plan -out tfplan && terraform show -json tfplan | node infra/terraform/tests/assert-no-graph-app-roles.mjs
//   node infra/terraform/tests/assert-no-graph-app-roles.mjs --self-test
//
// It fails when a planned resource
//   - is an azuread_app_role_assignment whose app role is a Graph application role (Sites.*, Files.*), or
//   - is an azuread_application / azuread_application_api_access whose required_resource_access asks for a
//     Microsoft Graph permission of type "Role".
import fs from "node:fs";

const GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000";
// Graph application role IDs, verified against the Graph service principal.
const SHAREPOINT_ONEDRIVE_ROLES = new Map([
  ["883ea226-0bf2-4a8f-9f9d-92c9162a727d", "Sites.Selected"],
  ["01d4889c-1287-42c6-ac1f-5d1e02578ef6", "Files.Read.All"],
  ["75359482-378d-4052-8f01-80520e7db3cd", "Files.ReadWrite.All"],
  ["332a536c-c7ef-4017-ab91-336970924f0d", "Sites.Read.All"],
  ["9492366f-7969-46a4-8d15-ed1a20078fff", "Sites.ReadWrite.All"],
]);

export function findViolations(plan) {
  const violations = [];
  for (const change of plan.resource_changes ?? []) {
    const actions = change.change?.actions ?? [];
    if (actions.every((a) => a === "no-op" || a === "read" || a === "delete")) continue;
    const after = change.change?.after ?? {};

    if (change.type === "azuread_app_role_assignment") {
      const role = SHAREPOINT_ONEDRIVE_ROLES.get(after.app_role_id);
      if (role) violations.push(`${change.address}: assigns the Graph application role ${role}`);
    }

    if (change.type === "azuread_application") {
      for (const rra of after.required_resource_access ?? []) {
        if (rra.resource_app_id !== GRAPH_APP_ID) continue;
        for (const access of rra.resource_access ?? []) {
          if (access.type === "Role") {
            const name = SHAREPOINT_ONEDRIVE_ROLES.get(access.id) ?? access.id;
            violations.push(`${change.address}: requires the Graph application permission ${name}`);
          }
        }
      }
    }

    if (change.type === "azuread_application_api_access" && after.api_client_id === GRAPH_APP_ID) {
      for (const id of after.role_ids ?? []) {
        violations.push(`${change.address}: requires the Graph application permission ${SHAREPOINT_ONEDRIVE_ROLES.get(id) ?? id}`);
      }
    }
  }
  return violations;
}

function selfTest() {
  const resource = (type, address, after, actions = ["create"]) => ({ type, address, change: { actions, after } });
  const cases = [
    ["empty plan passes", { resource_changes: [] }, 0],
    ["delegated Graph scopes pass", { resource_changes: [resource("azuread_application", "azuread_application.api[0]", {
      required_resource_access: [{ resource_app_id: GRAPH_APP_ID, resource_access: [{ id: "df85f4d6-205c-4ac5-a5ea-6bf408dba283", type: "Scope" }] }] })] }, 0],
    ["Sites.Selected role assignment fails", { resource_changes: [resource("azuread_app_role_assignment", "azuread_app_role_assignment.x", { app_role_id: "883ea226-0bf2-4a8f-9f9d-92c9162a727d" })] }, 1],
    ["an unrelated role assignment passes", { resource_changes: [resource("azuread_app_role_assignment", "azuread_app_role_assignment.awr_api_stack_a", { app_role_id: "7f26b10f-f15a-4f0f-b4e0-5a053e1b7611" })] }, 0],
    ["a Graph application permission on an app fails", { resource_changes: [resource("azuread_application", "azuread_application.api[0]", {
      required_resource_access: [{ resource_app_id: GRAPH_APP_ID, resource_access: [{ id: "01d4889c-1287-42c6-ac1f-5d1e02578ef6", type: "Role" }] }] })] }, 1],
    ["a removal is not a violation", { resource_changes: [resource("azuread_app_role_assignment", "azuread_app_role_assignment.x", { app_role_id: "883ea226-0bf2-4a8f-9f9d-92c9162a727d" }, ["delete"])] }, 0],
  ];
  let failed = 0;
  for (const [name, plan, expected] of cases) {
    const actual = findViolations(plan).length;
    const ok = actual === expected;
    if (!ok) failed++;
    console.log(`${ok ? "PASS" : "FAIL"}  ${name}`);
  }
  process.exit(failed === 0 ? 0 : 1);
}

if (process.argv[1]?.endsWith("assert-no-graph-app-roles.mjs")) {
  if (process.argv.includes("--self-test")) selfTest();
  const input = fs.readFileSync(0, "utf8");
  const violations = findViolations(JSON.parse(input));
  if (violations.length > 0) {
    console.error("Microsoft Graph application permissions for SharePoint or OneDrive must not be granted:");
    for (const v of violations) console.error(`  - ${v}`);
    process.exit(1);
  }
  console.log("OK: no Microsoft Graph application permission for SharePoint or OneDrive is planned.");
}
