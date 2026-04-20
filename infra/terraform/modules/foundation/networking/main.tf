# =============================================================================
# Foundation Module: Networking
# =============================================================================
# Looks up an existing VNet (never creates one) and either reuses an existing
# delegated subnet or creates a new one for App Service VNet Integration.
# No Private Endpoint resources are created — the existing PE on the VNet
# is relied upon for SQL connectivity.

# --- Input Validation ---
# Exactly one of existing_subnet_name or subnet_cidr must be non-empty
locals {
  reuse_subnet  = var.existing_subnet_name != ""
  create_subnet = var.subnet_cidr != ""
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
  count                = local.create_subnet ? 1 : 0
  name                 = var.subnet_name
  resource_group_name  = var.vnet_resource_group
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = [var.subnet_cidr]

  delegation {
    name = "appservice-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}
