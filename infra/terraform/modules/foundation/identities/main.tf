# =============================================================================
# Foundation Module: Managed Identities (reuse-aware)
# =============================================================================

# --- Data sources for reused identities ---
data "azurerm_user_assigned_identity" "stack_a" {
  count               = var.reuse ? 1 : 0
  name                = var.existing_stack_a_name
  resource_group_name = var.existing_resource_group
}

data "azurerm_user_assigned_identity" "stack_b" {
  count               = var.reuse ? 1 : 0
  name                = var.existing_stack_b_name
  resource_group_name = var.existing_resource_group
}

# --- Managed identities ---
resource "azurerm_user_assigned_identity" "stack_a" {
  count               = var.reuse ? 0 : 1
  name                = var.stack_a_name
  location            = var.location
  resource_group_name = var.resource_group_name

  tags = var.tags
}

resource "azurerm_user_assigned_identity" "stack_b" {
  count               = var.reuse ? 0 : 1
  name                = var.stack_b_name
  location            = var.location
  resource_group_name = var.resource_group_name

  tags = var.tags
}
