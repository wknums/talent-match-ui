# =============================================================================
# Foundation Module: App Service Host (building block)
# =============================================================================
# This module is NOT reuse-aware — it always creates an app service.
# It is used by stack-a and stack-b composition modules.

resource "azurerm_linux_web_app" "main" {
  name                                           = var.name
  location                                       = var.location
  resource_group_name                            = var.resource_group_name
  service_plan_id                                = var.service_plan_id
  key_vault_reference_identity_id                = var.identity_id
  ftp_publish_basic_authentication_enabled       = false
  https_only                                     = true
  webdeploy_publish_basic_authentication_enabled = false
  virtual_network_subnet_id                      = var.virtual_network_subnet_id

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  site_config {
    app_command_line              = var.app_command_line != "" ? var.app_command_line : null
    always_on                     = var.always_on
    vnet_route_all_enabled        = var.virtual_network_subnet_id != null ? true : null
    ip_restriction_default_action = length(var.allowed_ips) > 0 ? "Deny" : null

    application_stack {
      node_version   = var.node_version != "" ? var.node_version : null
      dotnet_version = var.dotnet_version != "" ? var.dotnet_version : null
    }

    dynamic "ip_restriction" {
      for_each = var.allowed_ips
      content {
        ip_address = "${ip_restriction.value}/32"
        action     = "Allow"
        priority   = 100 + ip_restriction.key
        name       = "AllowIP-${ip_restriction.key}"
      }
    }
  }

  app_settings = var.app_settings

  tags = var.tags
}
