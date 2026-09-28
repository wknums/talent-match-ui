# =============================================================================
# Foundation Module: Azure SQL (reuse-aware)
# =============================================================================

# --- Data sources for reused SQL ---
data "azurerm_mssql_server" "existing" {
  count               = var.reuse ? 1 : 0
  name                = var.existing_name
  resource_group_name = var.existing_resource_group
}

data "azurerm_mssql_database" "existing" {
  count     = var.reuse ? 1 : 0
  name      = var.existing_database_name
  server_id = data.azurerm_mssql_server.existing[0].id
}

# --- Managed SQL Server ---
resource "azurerm_mssql_server" "main" {
  count                         = var.reuse ? 0 : 1
  name                          = var.name
  resource_group_name           = var.resource_group_name
  location                      = var.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true
  administrator_login           = var.admin_password != "" ? var.admin_login : null
  administrator_login_password  = var.admin_password != "" ? var.admin_password : null

  azuread_administrator {
    login_username              = var.aad_admin_login
    object_id                   = var.aad_admin_object_id
    azuread_authentication_only = var.admin_password == ""
  }

  tags = var.tags
}

# --- Managed SQL Database ---
resource "azurerm_mssql_database" "main" {
  count     = var.reuse ? 0 : 1
  name      = var.database_name
  server_id = azurerm_mssql_server.main[0].id
  sku_name  = var.database_sku

  tags = var.tags
}

# --- Firewall rule to allow Azure services (managed only) ---
resource "azurerm_mssql_firewall_rule" "allow_azure" {
  count            = var.reuse ? 0 : 1
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.main[0].id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_firewall_rule" "operator" {
  for_each = var.reuse ? toset([]) : toset(var.allowed_ips)

  name             = "AllowOperator-${replace(each.value, ".", "-")}"
  server_id        = azurerm_mssql_server.main[0].id
  start_ip_address = each.value
  end_ip_address   = each.value
}

