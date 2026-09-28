output "id" {
  description = "Key Vault resource ID"
  value       = var.enabled ? (var.reuse ? data.azurerm_key_vault.existing[0].id : azurerm_key_vault.main[0].id) : ""
}

output "name" {
  description = "Key Vault name"
  value       = var.enabled ? (var.reuse ? data.azurerm_key_vault.existing[0].name : azurerm_key_vault.main[0].name) : ""
}

output "vault_uri" {
  description = "Key Vault URI"
  value       = var.enabled ? (var.reuse ? data.azurerm_key_vault.existing[0].vault_uri : azurerm_key_vault.main[0].vault_uri) : ""
}
