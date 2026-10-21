data "azurerm_application_insights" "existing" {
  count               = var.enabled && var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

resource "azurerm_log_analytics_workspace" "main" {
  count               = var.enabled && !var.reuse ? 1 : 0
  name                = var.workspace_name
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  retention_in_days   = 30

  tags = var.tags
}

resource "azurerm_application_insights" "main" {
  count                        = var.enabled && !var.reuse ? 1 : 0
  name                         = var.name
  location                     = var.location
  resource_group_name          = var.resource_group_name
  workspace_id                 = azurerm_log_analytics_workspace.main[0].id
  application_type             = "web"
  local_authentication_enabled = false

  tags = var.tags
}

locals {
  id                = var.enabled ? (var.reuse ? data.azurerm_application_insights.existing[0].id : azurerm_application_insights.main[0].id) : ""
  connection_string = var.enabled ? (var.reuse ? data.azurerm_application_insights.existing[0].connection_string : azurerm_application_insights.main[0].connection_string) : ""
}

resource "azurerm_role_assignment" "publisher" {
  for_each = var.enabled ? var.publisher_principal_ids : {}

  scope                = local.id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = each.value
  principal_type       = "ServicePrincipal"
}
