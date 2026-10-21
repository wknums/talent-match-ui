import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..", "..", "..");
const read = (...parts) => readFileSync(resolve(root, ...parts), "utf8");
const shared = read("infra", "terraform", "live", "shared", "main.tf");
const outputs = read("infra", "terraform", "live", "shared", "outputs.tf");
const deploy = read("infra", "scripts", "deploy.sh");
const workflow = read(".github", "workflows", "selective-azure-deploy.yml");

assert.match(shared, /module "app_insights"/);
assert.match(shared, /enabled\s*=\s*var\.enable_app_insights/);
assert.match(shared, /reuse\s*=\s*var\.reuse_app_insights/);
assert.match(shared, /existing_resource_group\s*=\s*var\.existing_app_insights_rg/);
assert.match(outputs, /output "app_insights_connection_string" \{[^}]*sensitive\s*=\s*true/s);
assert.match(deploy, /TF_VAR_app_insights_connection_string=/);
assert.match(deploy, /Application Insights is enabled but its shared connection string is unavailable/);
assert.match(read("infra", "scripts", "deprovision.sh"), /check_resource "Application Insights/);

for (const stack of ["a", "b"]) {
  const main = read("infra", "terraform", "live", `stack-${stack}`, "main.tf");
  const composition = read("infra", "terraform", "modules", `stack-${stack}`, "main.tf");
  assert.match(main, /app_insights_connection_string\s*=\s*var\.app_insights_connection_string/);
  assert.match(composition, /app_insights_connection_string\s*=\s*var\.app_insights_connection_string/);
  assert.match(composition, /identity_client_id\s*=\s*var\.identity_client_id/);
}

for (const profile of [".env_local.example", ".env_qa.example", ".env_prod.example"]) {
  const text = read(profile);
  assert.match(text, /^AZ_APP_INSIGHTS_ENABLED=FALSE$/m);
  assert.match(text, /^AZ_APP_INSIGHTS_REUSE=FALSE$/m);
  assert.match(text, /^# AZ_APP_INSIGHTS_NAME=$/m);
  assert.match(text, /^# AZ_APP_INSIGHTS_RG=$/m);
}

assert.equal((workflow.match(/AZ_APP_INSIGHTS_ENABLED: \$\{\{ vars\.AZ_APP_INSIGHTS_ENABLED/g) ?? []).length, 3);
assert.equal((workflow.match(/"\$AZ_APP_INSIGHTS_ENABLED" "\$AZ_APP_INSIGHTS_REUSE"/g) ?? []).length, 3);
console.log("Application Insights profile, shared-output and both-stack wiring assertions passed.");
