# =============================================================================
# Foundation Module: API Management (reuse-aware)
# =============================================================================

# --- Data source for reused APIM ---
data "azurerm_api_management" "existing" {
  count               = var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

# --- Managed APIM ---
resource "azurerm_api_management" "main" {
  count               = var.reuse ? 0 : 1
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  publisher_name      = var.publisher_name
  publisher_email     = var.publisher_email
  sku_name            = var.sku_name

  tags = var.tags
}
