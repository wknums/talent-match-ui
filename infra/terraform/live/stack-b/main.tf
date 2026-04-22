# =============================================================================
# Live Root: Stack B (.NET Blazor WASM)
# =============================================================================
# Deploys Stack B app host consuming shared outputs. Does not manage Stack A.

locals {
  tags = {
    environment = var.environment
    project     = var.project_name
    stack       = "stack-b"
    managedBy   = "terraform"
  }

  stack_b_extra_app_settings = var.awr_seq_api_endpoint != "" ? {
    AWR_SEQ_API_ENDPOINT = var.awr_seq_api_endpoint
  } : {}

  stack_b_runtime_app_settings = merge(
    local.stack_b_extra_app_settings,
    {
      AWR_AUTH_MODE    = var.awr_auth_mode
      AWR_MAX_PARALLEL = tostring(var.awr_max_parallel)
      API_MODE         = var.api_mode
    }
  )
}

module "stack_b" {
  source = "../../modules/stack-b"

  environment         = var.environment
  location            = var.location
  resource_group_name = var.resource_group_name
  project_name        = var.project_name
  app_service_plan_id = var.app_service_plan_id
  identity_id         = var.identity_id
  identity_client_id  = var.identity_client_id
  key_vault_uri       = var.key_vault_uri
  sql_server_fqdn     = var.sql_server_fqdn
  sql_database_name   = var.sql_database_name
  apim_gateway_url    = var.apim_gateway_url
  use_key_vault_secret_refs = var.use_key_vault_secret_refs
  awr_api_key               = var.awr_api_key
  openai_api_key            = var.openai_api_key
  extra_app_settings        = local.stack_b_runtime_app_settings

  virtual_network_subnet_id = var.integration_subnet_id != "" ? var.integration_subnet_id : null
  allowed_ips               = var.allowed_ips

  tags = local.tags
}
