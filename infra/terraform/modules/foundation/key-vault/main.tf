# =============================================================================
# Foundation Module: Key Vault (reuse-aware)
# =============================================================================

data "azurerm_client_config" "current" {}

# --- Data source for reused Key Vault ---
data "azurerm_key_vault" "existing" {
  count               = var.enabled && var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

# --- Managed Key Vault ---
resource "azurerm_key_vault" "main" {
  count                      = var.enabled && !var.reuse ? 1 : 0
  name                       = var.name
  location                   = var.location
  resource_group_name        = var.resource_group_name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  public_network_access_enabled = false
  soft_delete_retention_days = 7
  purge_protection_enabled   = false

  tags = var.tags
}
