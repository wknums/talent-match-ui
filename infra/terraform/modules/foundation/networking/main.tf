# =============================================================================
# Foundation Module: Networking
# =============================================================================
# Looks up an existing VNet (never creates one), configures App Service VNet
# Integration, and optionally creates Azure SQL Private Link connectivity.

# --- Input Validation ---
# Exactly one of existing_subnet_name or subnet_cidr must be non-empty
locals {
  reuse_subnet                = var.existing_subnet_name != ""
  create_subnet               = var.subnet_cidr != ""
  create_sql_private_endpoint = !var.reuse_sql_private_endpoint
}

resource "terraform_data" "validate_subnet_mode" {
  lifecycle {
    precondition {
      condition     = (local.reuse_subnet || local.create_subnet) && !(local.reuse_subnet && local.create_subnet)
      error_message = "Specify exactly one of existing_subnet_name (reuse) or subnet_cidr (create)"
    }

    precondition {
      condition     = !local.create_subnet || (local.create_subnet && tonumber(split("/", var.subnet_cidr)[1]) <= 26)
      error_message = "Integration subnet CIDR must be at least /26 for App Service delegation"
    }

    precondition {
      condition = !local.create_sql_private_endpoint || (
        var.sql_private_endpoint_subnet_cidr != "" &&
        var.sql_server_id != "" &&
        var.sql_server_name != ""
      )
      error_message = "SQL server ID, SQL server name, and private endpoint subnet CIDR are required when creating SQL Private Link"
    }
  }
}

# --- VNet Data Lookup (always — never created) ---
data "azurerm_virtual_network" "main" {
  name                = var.vnet_name
  resource_group_name = var.vnet_resource_group
}

# --- Subnet: Reuse Mode ---
data "azurerm_subnet" "existing" {
  count                = local.reuse_subnet ? 1 : 0
  name                 = var.existing_subnet_name
  virtual_network_name = data.azurerm_virtual_network.main.name
  resource_group_name  = var.vnet_resource_group
}

# --- Subnet: Create Mode ---
resource "azurerm_subnet" "integration" {
  count                           = local.create_subnet ? 1 : 0
  name                            = var.subnet_name
  resource_group_name             = var.vnet_resource_group
  virtual_network_name            = data.azurerm_virtual_network.main.name
  address_prefixes                = [var.subnet_cidr]
  default_outbound_access_enabled = false

  delegation {
    name = "appservice-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

# --- Azure SQL Private Link ---
resource "azurerm_subnet" "sql_private_endpoint" {
  count                             = local.create_sql_private_endpoint ? 1 : 0
  name                              = var.sql_private_endpoint_subnet_name
  resource_group_name               = var.vnet_resource_group
  virtual_network_name              = data.azurerm_virtual_network.main.name
  address_prefixes                  = [var.sql_private_endpoint_subnet_cidr]
  default_outbound_access_enabled   = false
  private_endpoint_network_policies = "Disabled"
}

resource "azurerm_private_dns_zone" "sql" {
  count               = local.create_sql_private_endpoint ? 1 : 0
  name                = "privatelink.database.windows.net"
  resource_group_name = var.vnet_resource_group
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "sql" {
  count                 = local.create_sql_private_endpoint ? 1 : 0
  name                  = "link-${data.azurerm_virtual_network.main.name}-sql"
  resource_group_name   = var.vnet_resource_group
  private_dns_zone_name = azurerm_private_dns_zone.sql[0].name
  virtual_network_id    = data.azurerm_virtual_network.main.id
  registration_enabled  = false
  tags                  = var.tags
}

resource "azurerm_private_endpoint" "sql" {
  count               = local.create_sql_private_endpoint ? 1 : 0
  name                = "pe-${var.sql_server_name}"
  location            = data.azurerm_virtual_network.main.location
  resource_group_name = var.vnet_resource_group
  subnet_id           = azurerm_subnet.sql_private_endpoint[0].id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-${var.sql_server_name}"
    private_connection_resource_id = var.sql_server_id
    subresource_names              = ["sqlServer"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "sql"
    private_dns_zone_ids = [azurerm_private_dns_zone.sql[0].id]
  }
}
