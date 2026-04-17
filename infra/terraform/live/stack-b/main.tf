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
}

module "stack_b" {
  source = "../../modules/stack-b"

  environment         = var.environment
  location            = var.location
  resource_group_name = var.resource_group_name
  project_name        = var.project_name
  app_service_plan_id = var.app_service_plan_id
  identity_id         = var.identity_id
  key_vault_uri       = var.key_vault_uri
  sql_server_fqdn     = var.sql_server_fqdn
  sql_database_name   = var.sql_database_name
  apim_gateway_url    = var.apim_gateway_url

  tags = local.tags
}
