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
  count                        = var.reuse ? 0 : 1
  name                         = var.name
  resource_group_name          = var.resource_group_name
  location                     = var.location
  version                      = "12.0"
  minimum_tls_version          = "1.2"
  administrator_login          = var.admin_login
  administrator_login_password = var.admin_password

  azuread_administrator {
    login_username = var.aad_admin_login
    object_id      = var.aad_admin_object_id
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

# --- Ensure the talentmatch schema exists (managed databases only) ---
resource "terraform_data" "ensure_schema" {
  count = var.reuse ? 0 : 1

  # Re-run if the database resource changes
  input = azurerm_mssql_database.main[0].id

  provisioner "local-exec" {
    command = <<-EOT
      az sql db query \
        --server "${azurerm_mssql_server.main[0].name}" \
        --name "${azurerm_mssql_database.main[0].name}" \
        --resource-group "${var.resource_group_name}" \
        --query "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'talentmatch') EXEC('CREATE SCHEMA [talentmatch]')"
    EOT
  }

  depends_on = [
    azurerm_mssql_database.main,
    azurerm_mssql_firewall_rule.allow_azure,
  ]
}
