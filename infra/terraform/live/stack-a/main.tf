# =============================================================================
# Live Root: Stack A (Node.js/Express)
# =============================================================================
# Deploys Stack A app host consuming shared outputs. Does not manage Stack B.

locals {
  tags = {
    environment = var.environment
    project     = var.project_name
    stack       = "stack-a"
    managedBy   = "terraform"
  }

  stack_a_extra_app_settings = var.awr_seq_api_endpoint != "" ? {
    AWR_SEQ_API_ENDPOINT = var.awr_seq_api_endpoint
  } : {}

  stack_a_runtime_app_settings = merge(
    local.stack_a_extra_app_settings,
    {
      APP_AUTH_MODE                         = var.app_auth_mode
      AZURE_TENANT_ID                       = var.tenant_id
      ENTRA_API_APP_CLIENT_ID               = var.entra_api_app_client_id
      ENTRA_API_SERVICE_PRINCIPAL_OBJECT_ID = var.entra_api_service_principal_object_id
      ENTRA_API_IDENTIFIER_URI              = var.entra_api_identifier_uri
      ENTRA_API_SCOPE                       = var.entra_api_scope
      ENTRA_STACK_A_CLIENT_ID               = var.entra_spa_client_id
      ENTRA_ADMIN_APP_ROLE_ID               = var.entra_app_role_ids["admin"]
      ENTRA_ORGANIZATION_ADMIN_APP_ROLE_ID  = var.entra_app_role_ids["organization_admin"]
      ENTRA_RECRUITER_APP_ROLE_ID           = var.entra_app_role_ids["recruiter"]
      ENTRA_BUSINESS_PANEL_APP_ROLE_ID      = var.entra_app_role_ids["business_panel"]
      ENTRA_BOOTSTRAP_ADMIN_OBJECT_ID       = var.entra_bootstrap_admin_object_id
      AWR_AUTH_MODE                         = var.awr_auth_mode
      AWR_AAD_AUDIENCE                      = var.awr_aad_audience
      AWR_MAX_PARALLEL                      = tostring(var.awr_max_parallel)
      API_MODE                              = var.api_mode
    }
  )
}

module "stack_a" {
  source = "../../modules/stack-a"

  environment               = var.environment
  location                  = var.location
  resource_group_name       = var.resource_group_name
  project_name              = var.project_name
  app_service_plan_id       = var.app_service_plan_id
  identity_id               = var.identity_id
  identity_client_id        = var.identity_client_id
  key_vault_uri             = var.key_vault_uri
  sql_server_fqdn           = var.sql_server_fqdn
  sql_database_name         = var.sql_database_name
  apim_gateway_url          = var.apim_gateway_url
  use_key_vault_secret_refs = var.use_key_vault_secret_refs
  awr_api_key               = var.awr_api_key
  extra_app_settings        = local.stack_a_runtime_app_settings

  virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null
  allowed_ips               = var.allowed_ips

  tags = local.tags
}
