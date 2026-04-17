# =============================================================================
# Foundation Module: App Service Host (building block)
# =============================================================================
# This module is NOT reuse-aware — it always creates an app service.
# It is used by stack-a and stack-b composition modules.

resource "azurerm_linux_web_app" "main" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  service_plan_id     = var.service_plan_id
  https_only          = true

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  site_config {
    always_on = var.always_on

    application_stack {
      node_version   = var.node_version != "" ? var.node_version : null
      dotnet_version = var.dotnet_version != "" ? var.dotnet_version : null
    }
  }

  app_settings = var.app_settings

  tags = var.tags
}
