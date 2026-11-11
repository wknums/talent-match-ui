# =============================================================================
# Live Root: Shared Infrastructure
# =============================================================================
# Provisions or reuses shared Azure resources consumed by both stacks.

locals {
  stack_a_enabled   = contains(["stack-a", "both"], var.stack_target)
  stack_b_enabled   = contains(["stack-b", "both"], var.stack_target)
  awr_entra_enabled = var.awr_auth_mode == "entra" && var.awr_api_client_id != ""
  runtime_identity_principal_ids = merge(
    local.stack_a_enabled ? { stack_a = module.identities.stack_a_principal_id } : {},
    local.stack_b_enabled ? { stack_b = module.identities.stack_b_principal_id } : {}
  )

  tags = {
    environment = var.environment
    project     = var.project_name
    managedBy   = "terraform"
  }

  managed_key_vault_name = trimspace(var.existing_key_vault_name) != "" ? var.existing_key_vault_name : "kv-${var.project_name}-${var.environment}"
  managed_key_vault_uses_external_rg = (
    var.enable_key_vault
    && !var.reuse_key_vault
    && trimspace(var.existing_key_vault_rg) != ""
    && var.existing_key_vault_rg != var.resource_group_name
  )
  managed_key_vault_resource_group = trimspace(var.existing_key_vault_rg) != "" ? var.existing_key_vault_rg : azurerm_resource_group.main.name
  managed_key_vault_location       = local.managed_key_vault_uses_external_rg ? data.azurerm_resource_group.key_vault_managed_target[0].location : azurerm_resource_group.main.location
}

# --- Resource Group ---
resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

resource "terraform_data" "validate_source_connector_network" {
  input = var.source_connector_enabled

  lifecycle {
    precondition {
      condition     = !var.source_connector_enabled || (var.enable_key_vault && var.reuse_vnet)
      error_message = "The delegated source connector requires Key Vault and VNet integration so runtime authorization keys stay private-only."
    }
  }
}

# A custom resource group for a newly managed vault must already exist. The shared
# root owns its main resource group but does not implicitly create arbitrary groups.
data "azurerm_resource_group" "key_vault_managed_target" {
  count = local.managed_key_vault_uses_external_rg ? 1 : 0
  name  = var.existing_key_vault_rg
}

# --- Microsoft Entra applications ---
module "entra" {
  source = "../../modules/foundation/entra"

  enabled        = var.app_auth_mode == "entra"
  enable_stack_a = local.stack_a_enabled
  enable_stack_b = local.stack_b_enabled

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

  source_connector_enabled                          = var.source_connector_enabled
  reuse_source_connector                            = var.reuse_source_connector
  existing_source_connector_client_id               = var.existing_source_connector_client_id
  source_connector_display_name                     = "${var.project_name}-${var.environment}-microsoft365-source"
  source_connector_redirect_uris                    = var.source_connector_redirect_uris
  source_connector_federated_identity_principal_ids = local.runtime_identity_principal_ids
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

  name                = local.managed_key_vault_name
  location            = local.managed_key_vault_location
  resource_group_name = local.managed_key_vault_resource_group

  bulk_auth_key_name                  = var.source_connector_enabled ? var.bulk_auth_key_name : ""
  bulk_auth_crypto_user_principal_ids = local.runtime_identity_principal_ids

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

# --- Application Insights ---
module "app_insights" {
  source = "../../modules/foundation/app-insights"

  enabled                 = var.enable_app_insights
  reuse                   = var.reuse_app_insights
  existing_name           = var.existing_app_insights_name
  existing_resource_group = var.existing_app_insights_rg

  name                    = "appi-${var.project_name}-${var.environment}"
  workspace_name          = "law-${var.project_name}-${var.environment}"
  location                = azurerm_resource_group.main.location
  resource_group_name     = azurerm_resource_group.main.name
  publisher_principal_ids = local.runtime_identity_principal_ids

  tags = local.tags
}

# --- Managed Identities ---
module "identities" {
  source = "../../modules/foundation/identities"

  reuse                   = var.reuse_identities
  enable_stack_a          = local.stack_a_enabled
  enable_stack_b          = local.stack_b_enabled
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
  count = local.awr_entra_enabled && (local.stack_a_enabled || local.stack_b_enabled) ? 1 : 0

  client_id = var.awr_api_client_id
}

resource "azuread_app_role_assignment" "awr_api_stack_a" {
  count = local.awr_entra_enabled && local.stack_a_enabled ? 1 : 0

  app_role_id         = data.azuread_service_principal.awr_api[0].app_role_ids[var.awr_api_app_role_value]
  principal_object_id = module.identities.stack_a_principal_id
  resource_object_id  = data.azuread_service_principal.awr_api[0].object_id
}

resource "azuread_app_role_assignment" "awr_api_stack_b" {
  count = local.awr_entra_enabled && local.stack_b_enabled ? 1 : 0

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
  key_vault_id                     = module.key_vault.id
  key_vault_name                   = module.key_vault.name

  tags = local.tags
}
