import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..", "..", "..");
const sharedMain = readFileSync(
  resolve(root, "infra", "terraform", "live", "shared", "main.tf"),
  "utf8",
);
const sharedVariables = readFileSync(
  resolve(root, "infra", "terraform", "live", "shared", "variables.tf"),
  "utf8",
);
const common = readFileSync(
  resolve(root, "infra", "scripts", "lib", "common.sh"),
  "utf8",
);
const networkingMain = readFileSync(
  resolve(root, "infra", "terraform", "modules", "foundation", "networking", "main.tf"),
  "utf8",
);

assert.match(
  sharedMain,
  /managed_key_vault_name\s*=\s*trimspace\(var\.existing_key_vault_name\)\s*!=\s*""\s*\?\s*var\.existing_key_vault_name\s*:\s*"kv-\$\{var\.project_name\}-\$\{var\.environment\}"/,
  "a non-empty profile name must override the generated managed-vault name",
);
assert.match(
  sharedMain,
  /managed_key_vault_resource_group\s*=\s*trimspace\(var\.existing_key_vault_rg\)\s*!=\s*""\s*\?\s*var\.existing_key_vault_rg\s*:\s*azurerm_resource_group\.main\.name/,
  "a non-empty profile resource group must override the main managed resource group",
);
assert.match(
  sharedMain,
  /data\s+"azurerm_resource_group"\s+"key_vault_managed_target"/,
  "a custom create-time resource group must be resolved before the vault is created",
);
assert.match(
  sharedMain,
  /name\s*=\s*local\.managed_key_vault_name[\s\S]*location\s*=\s*local\.managed_key_vault_location[\s\S]*resource_group_name\s*=\s*local\.managed_key_vault_resource_group/,
  "the Key Vault module must consume the resolved managed-vault coordinates",
);
assert.match(
  sharedVariables,
  /when reuse is false, a non-empty value overrides the generated managed-vault name/,
);
assert.match(
  sharedVariables,
  /when reuse is false, a non-empty value selects an existing target resource group/,
);
assert.match(common, /TF_VAR_existing_key_vault_name="\$\{AZ_KEY_VAULT_NAME:-\}"/);
assert.match(common, /TF_VAR_existing_key_vault_rg="\$\{AZ_KEY_VAULT_RG:-\}"/);
assert.match(
  sharedMain,
  /managed_key_vault_uses_external_rg/,
  "the shared root must distinguish custom managed-vault resource groups",
);

const keyVaultMain = readFileSync(
  resolve(root, "infra", "terraform", "modules", "foundation", "key-vault", "main.tf"),
  "utf8",
);
assert.match(
  keyVaultMain,
  /resource\s+"azapi_resource"\s+"bulk_auth"/,
  "the wrapping key must be created through the ARM control plane",
);
assert.match(
  keyVaultMain,
  /type\s*=\s*"Microsoft\.KeyVault\/vaults\/keys@2023-07-01"/,
);
assert.match(
  keyVaultMain,
  /keyOps\s*=\s*\["wrapKey",\s*"unwrapKey"\]/,
);
assert.match(
  keyVaultMain,
  /scope\s*=\s*azapi_resource\.bulk_auth\[0\]\.id/,
  "runtime RBAC must remain scoped to the ARM-created key",
);
assert.match(
  networkingMain,
  /resource\s+"azurerm_private_endpoint"\s+"key_vault"/,
  "private-only runtime access requires a Key Vault private endpoint",
);
assert.match(
  networkingMain,
  /privatelink\.vaultcore\.azure\.net/,
  "private-only runtime access requires the Key Vault private DNS zone",
);
assert.match(
  networkingMain,
  /subresource_names\s*=\s*\["vault"\]/,
);

console.log("Key Vault profile create/reuse contract assertions passed.");
