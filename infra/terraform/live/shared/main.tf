# =============================================================================
# Live Root: Shared Infrastructure
# =============================================================================
# Provisions or reuses shared Azure resources consumed by both stacks.

locals {
  tags = {
    environment = var.environment
    project     = var.project_name
    managedBy   = "terraform"
  }
}

# --- Resource Group ---
resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

# --- App Service Plan ---
module "app_service_plan" {
  source = "../../modules/foundation/app-service-plan"

  reuse                   = var.reuse_app_service_plan
  existing_name           = var.existing_app_service_plan_name
  existing_resource_group = var.existing_app_service_plan_rg

  name                = "plan-${var.project_name}-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku_name            = var.app_service_plan_sku

  tags = local.tags
}

# --- Azure SQL ---
module "sql" {
  source = "../../modules/foundation/sql"

  reuse                  = var.reuse_sql
  existing_name          = var.existing_sql_server_name
  existing_database_name = var.existing_sql_database_name
  existing_resource_group = var.existing_sql_rg

  name                = "sql-${var.project_name}-${var.environment}"
  database_name       = "sqldb-${var.project_name}-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  admin_login         = var.sql_admin_login
  admin_password      = var.sql_admin_password
  aad_admin_login     = var.sql_aad_admin_login
  aad_admin_object_id = var.sql_aad_admin_object_id

  tags = local.tags
}

# --- Key Vault ---
module "key_vault" {
  source = "../../modules/foundation/key-vault"

  reuse                   = var.reuse_key_vault
  existing_name           = var.existing_key_vault_name
  existing_resource_group = var.existing_key_vault_rg

  name                = "kv-${var.project_name}-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name

  tags = local.tags
}

# --- API Management ---
module "apim" {
  source = "../../modules/foundation/apim"

  reuse                   = var.reuse_apim
  existing_name           = var.existing_apim_name
  existing_resource_group = var.existing_apim_rg

  name                = "apim-${var.project_name}-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name

  tags = local.tags
}

# --- Managed Identities ---
module "identities" {
  source = "../../modules/foundation/identities"

  reuse                   = var.reuse_identities
  existing_stack_a_name   = var.existing_identity_stack_a_name
  existing_stack_b_name   = var.existing_identity_stack_b_name
  existing_resource_group = var.existing_identities_rg

  stack_a_name        = "id-${var.project_name}-node-${var.environment}"
  stack_b_name        = "id-${var.project_name}-blazor-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name

  tags = local.tags
}

# --- Networking (US6) ---
module "networking" {
  count  = var.reuse_vnet ? 1 : 0
  source = "../../modules/foundation/networking"

  vnet_name            = var.vnet_name
  vnet_resource_group  = var.vnet_resource_group
  existing_subnet_name = var.existing_integration_subnet_name
  subnet_cidr          = var.integration_subnet_cidr

  tags = local.tags
}
