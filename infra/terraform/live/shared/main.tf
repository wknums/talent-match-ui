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

# --- Microsoft Entra applications ---
module "entra" {
  source = "../../modules/foundation/entra"

  reuse                                    = var.reuse_entra
  existing_api_app_client_id               = var.existing_entra_api_app_client_id
  existing_api_service_principal_object_id = var.existing_entra_api_service_principal_object_id
  existing_stack_a_client_id               = var.existing_entra_stack_a_client_id
  existing_stack_b_client_id               = var.existing_entra_stack_b_client_id

  api_display_name          = "${var.project_name}-${var.environment}-api"
  api_identifier_uri        = var.entra_api_identifier_uri
  api_scope_id              = var.entra_api_scope_id
  api_scope_value           = var.entra_api_scope
  app_role_ids              = var.entra_app_role_ids
  stack_a_display_name      = "${var.project_name}-${var.environment}-stack-a"
  stack_a_redirect_uris     = var.entra_stack_a_redirect_uris
  stack_b_display_name      = "${var.project_name}-${var.environment}-stack-b"
  stack_b_redirect_uris     = var.entra_stack_b_redirect_uris
  create_role_groups        = var.entra_create_role_groups
  role_group_names          = var.entra_role_group_names
  bootstrap_admin_object_id = var.entra_bootstrap_admin_object_id
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

  reuse                   = var.reuse_sql
  existing_name           = var.existing_sql_server_name
  existing_database_name  = var.existing_sql_database_name
  existing_resource_group = var.existing_sql_rg

  name                = "sql-${var.project_name}-${var.environment}"
  database_name       = "sqldb-${var.project_name}-${var.environment}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  admin_login         = var.sql_admin_login
  admin_password      = var.sql_admin_password
  aad_admin_login     = var.sql_aad_admin_login
  aad_admin_object_id = var.sql_aad_admin_object_id
  allowed_ips         = var.allowed_ips

  tags = local.tags
}

# --- Key Vault ---
module "key_vault" {
  source = "../../modules/foundation/key-vault"

  enabled                 = var.enable_key_vault
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

  enabled                 = var.enable_apim
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

data "azuread_service_principal" "awr_api" {
  count = var.awr_api_client_id != "" ? 1 : 0

  client_id = var.awr_api_client_id
}

resource "azuread_app_role_assignment" "awr_api_stack_a" {
  count = var.awr_api_client_id != "" ? 1 : 0

  app_role_id         = data.azuread_service_principal.awr_api[0].app_role_ids[var.awr_api_app_role_value]
  principal_object_id = module.identities.stack_a_principal_id
  resource_object_id  = data.azuread_service_principal.awr_api[0].object_id
}

resource "azuread_app_role_assignment" "awr_api_stack_b" {
  count = var.awr_api_client_id != "" ? 1 : 0

  app_role_id         = data.azuread_service_principal.awr_api[0].app_role_ids[var.awr_api_app_role_value]
  principal_object_id = module.identities.stack_b_principal_id
  resource_object_id  = data.azuread_service_principal.awr_api[0].object_id
}

# --- Networking (US6) ---
module "networking" {
  count  = var.reuse_vnet ? 1 : 0
  source = "../../modules/foundation/networking"

  vnet_name            = var.vnet_name
  vnet_resource_group  = var.vnet_resource_group
  existing_subnet_name = var.existing_integration_subnet_name
  subnet_cidr          = var.integration_subnet_cidr

  reuse_sql_private_endpoint       = var.reuse_sql_private_endpoint
  sql_private_endpoint_subnet_name = var.sql_private_endpoint_subnet_name
  sql_private_endpoint_subnet_cidr = var.sql_private_endpoint_subnet_cidr
  sql_server_id                    = module.sql.server_id
  sql_server_name                  = module.sql.server_name

  tags = local.tags
}
