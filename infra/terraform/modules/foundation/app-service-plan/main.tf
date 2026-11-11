# =============================================================================
# Foundation Module: App Service Plan (reuse-aware)
# =============================================================================

# --- Data source for reused plan ---
data "azurerm_service_plan" "existing" {
  count               = var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

# --- Managed plan ---
resource "azurerm_service_plan" "main" {
  count               = var.reuse ? 0 : 1
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  os_type             = "Linux"
  sku_name            = var.sku_name

  tags = var.tags
}
